using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.DesktopV2.Views;

/// <summary>
/// 文件压缩页签：把 PDF、图片和 Office 文档的体积压小（类似 WPS 的文件压缩），
/// 而不是打包成 ZIP。ZIP / GZip 打包解压作为次要功能保留在下方折叠区。
/// 全部在本机完成，输出只新建文件。
/// </summary>
public partial class CompressTab : UserControl
{
    private readonly LocalFileCompressor _compressor = new();
    private readonly IArchiveTool _archive = new ArchiveTool();
    private readonly ObservableCollection<CompressItem> _items = [];
    private CancellationTokenSource? _operation;
    private bool _busy;
    private string? _outputFolder;

    /// <summary>列表里的一行：记录原始大小和压缩结果，供界面直接绑定。</summary>
    public sealed class CompressItem : INotifyPropertyChanged
    {
        private string _resultText = "—";
        private string _changeText = "";
        private string _statusText = "等待压缩";
        private bool _isDone;
        private bool _hasFailed;

        public string FullPath { get; }
        public string FileName { get; }
        public string KindLabel { get; }
        public long OriginalBytes { get; }
        public string OriginalText { get; }

        public string ResultText { get => _resultText; set => Set(ref _resultText, value); }
        public string ChangeText { get => _changeText; set => Set(ref _changeText, value); }
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }
        public bool HasFailed { get => _hasFailed; set => Set(ref _hasFailed, value); }

        public CompressItem(string path)
        {
            FullPath = path;
            FileName = Path.GetFileName(path);
            var kind = LocalFileCompressor.Detect(path);
            KindLabel = CompressKindDetector.Describe(kind);
            OriginalBytes = new FileInfo(path).Length;
            OriginalText = FileSizeFormatter.Format(OriginalBytes);
        }

        public void Reset()
        {
            ResultText = "—";
            ChangeText = "";
            StatusText = "等待压缩";
            IsDone = false;
            HasFailed = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public CompressTab()
    {
        InitializeComponent();
        ItemGrid.ItemsSource = _items;
        RefreshState();
    }

    private CompressOptions Options
    {
        get
        {
            var (quality, scale, dpi) = LevelChoice.SelectedIndex switch
            {
                0 => (85, 100, 150),
                2 => (55, 75, 96),
                3 => (40, 50, 72),
                _ => (70, 100, 120)
            };
            return new CompressOptions
            {
                Quality = quality,
                ScalePercent = scale,
                PdfDpi = dpi,
                RasterizePdf = RasterizePdf.IsChecked == true
            };
        }
    }

    // ---------------------------------------------------------------- 列表维护

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "可压缩文件|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.docx;*.xlsx;*.pptx|所有文件 (*.*)|*.*",
            Multiselect = true,
            Title = "添加要压缩体积的本地文件",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var added = 0;
        var rejected = 0;
        foreach (var path in dialog.FileNames)
        {
            try
            {
                var full = LocalPathGuard.ExistingFile(path);
                if (_items.Any(item => string.Equals(item.FullPath, full, StringComparison.OrdinalIgnoreCase))) continue;
                if (LocalFileCompressor.Detect(full) == CompressFileKind.Unknown) { rejected++; continue; }
                _items.Add(new CompressItem(full));
                added++;
            }
            catch (PdfToolException) { rejected++; }
        }
        RefreshState();
        StatusText.Text = rejected > 0
            ? $"已添加 {added} 个文件；有 {rejected} 个不是可压缩的本地文件，已跳过。"
            : $"已添加 {added} 个文件，共 {_items.Count} 个待压缩。";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemGrid.SelectedItems.OfType<CompressItem>().ToArray();
        if (selected.Length == 0) { StatusText.Text = "请先在列表中选择要移除的文件。"; return; }
        foreach (var item in selected) _items.Remove(item);
        RefreshState();
        StatusText.Text = $"已移除 {selected.Length} 个文件。";
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        RefreshState();
        StatusText.Text = "已清空待压缩列表。";
    }

    private void RefreshState()
    {
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompressButton.IsEnabled = !_busy && _items.Count > 0 && _outputFolder is not null;
    }

    private void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择压缩结果的保存文件夹" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _outputFolder = LocalPathGuard.ExistingDirectory(dialog.FolderName);
            OutputFolderBox.Text = _outputFolder;
            RefreshState();
            StatusText.Text = "已选择输出文件夹；压缩结果会保存为新文件，文件名带“-压缩”后缀。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    // ---------------------------------------------------------------- 体积压缩

    private async void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _items.Count == 0 || _outputFolder is null) return;
        var options = Options;
        var folder = _outputFolder;
        foreach (var item in _items) item.Reset();
        SetBusy(true, $"正在压缩 {_items.Count} 个文件…");
        var token = _operation!.Token;
        var done = 0;
        var failed = 0;
        long before = 0;
        long after = 0;
        try
        {
            foreach (var item in _items)
            {
                if (token.IsCancellationRequested) break;
                item.StatusText = "正在压缩…";
                try
                {
                    var target = LocalFileCompressor.UniqueOutputPath(folder, item.FileName, "-压缩");
                    var progress = new Progress<string>(message => StatusText.Text = message);
                    var source = item.FullPath;
                    var result = await Task.Run(() => _compressor.Compress(source, target, options, progress, token), token);
                    item.ResultText = FileSizeFormatter.Format(result.CompressedBytes);
                    item.ChangeText = result.SavedPercent > 0 ? $"减少 {result.SavedPercent:0.#}%" : $"增加 {-result.SavedPercent:0.#}%";
                    item.StatusText = result.SavedPercent > 0 ? "已完成" : "未变小，建议保留原文件";
                    item.IsDone = result.SavedPercent > 0;
                    before += result.OriginalBytes;
                    after += result.CompressedBytes;
                    done++;
                }
                catch (OperationCanceledException) { item.StatusText = "已取消"; break; }
                catch (PdfToolException error) { item.StatusText = error.Message; item.HasFailed = true; failed++; }
                catch (Exception) { item.StatusText = "压缩失败，请检查文件是否可读、目标位置是否可写。"; item.HasFailed = true; failed++; }
            }
            StatusText.Text = token.IsCancellationRequested
                ? $"已取消；已完成 {done} 个文件。"
                : before > 0
                    ? $"完成 {done} 个文件：{FileSizeFormatter.Format(before)} → {FileSizeFormatter.Format(after)}，共减少 {(before - after) * 100d / before:0.#}%。"
                      + (failed > 0 ? $" 有 {failed} 个未完成。" : "")
                    : $"没有文件被压缩；有 {failed} 个未完成。";
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    // ---------------------------------------------------------------- 打包与解压

    private async void CreateZip_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) { StatusText.Text = "请先添加要打包的文件。"; return; }
        var dialog = new SaveFileDialog
        {
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = "打包结果.zip",
            Title = "保存为新的本地 ZIP（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var paths = _items.Select(item => item.FullPath).ToArray();
        await RunArchiveAsync($"正在打包 {paths.Length} 个文件…",
            (progress, token) => _archive.CreateZip(dialog.FileName, paths, ArchiveLevel.Optimal, progress, token),
            result => $"已生成 ZIP：{result.EntryCount} 个条目，{FileSizeFormatter.Format(result.ByteCount)}。");
    }

    private async void ExtractZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            Title = "选择要解压的本地 ZIP",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var parent = PickFolder("选择解压到的上级文件夹");
        if (parent is null) return;
        var target = NewFolderName(parent, Path.GetFileNameWithoutExtension(dialog.FileName));
        await RunArchiveAsync("正在解压…",
            (progress, token) => _archive.ExtractZip(dialog.FileName, target, progress, token),
            result => $"已解压 {result.EntryCount} 个文件到新建文件夹：{Path.GetFileName(result.OutputPath)}。");
    }

    private async void CompressGzip_Click(object sender, RoutedEventArgs e)
    {
        var file = SingleFile();
        if (file is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "GZip 压缩文件 (*.gz)|*.gz",
            DefaultExt = ".gz",
            AddExtension = true,
            FileName = Path.GetFileName(file) + ".gz",
            Title = "保存为新的本地 GZip 文件（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var source = file;
        await RunArchiveAsync("正在压缩…",
            (progress, token) => _archive.CompressGzip(dialog.FileName, source, ArchiveLevel.Optimal, progress, token),
            result => $"已生成 GZip 文件，{FileSizeFormatter.Format(result.ByteCount)}。");
    }

    private async void ExtractGzip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "GZip 压缩文件 (*.gz)|*.gz|所有文件 (*.*)|*.*",
            Title = "选择要解压的本地 GZip 文件",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var name = Path.GetFileName(dialog.FileName);
        var suggested = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name + ".out";
        var save = new SaveFileDialog
        {
            Filter = "所有文件 (*.*)|*.*",
            FileName = suggested,
            Title = "还原为新的本地文件（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunArchiveAsync("正在解压…",
            (progress, token) => _archive.ExtractGzip(save.FileName, dialog.FileName, progress, token),
            result => $"已还原 {FileSizeFormatter.Format(result.ByteCount)} 到新文件。");
    }

    private void ListZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            Title = "选择要查看的本地 ZIP",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var entries = _archive.ListZip(dialog.FileName);
            StatusText.Text = entries.Count == 0
                ? "这个 ZIP 是空的。"
                : $"ZIP 内含 {entries.Count} 个条目，共 {FileSizeFormatter.Format(entries.Sum(entry => entry.Length))}（只读取，不修改压缩包）。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "无法读取该 ZIP，请确认文件有效。"; }
    }

    private string? SingleFile()
    {
        var selected = ItemGrid.SelectedItems.OfType<CompressItem>().ToArray();
        var candidate = selected.Length == 1 ? selected[0] : _items.Count == 1 ? _items[0] : null;
        if (candidate is null)
        {
            StatusText.Text = "GZip 一次只压缩一个文件；请在列表中只选中一个文件。";
            return null;
        }
        return candidate.FullPath;
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }

    /// <summary>在所选上级文件夹下取一个尚不存在的子文件夹名，避免覆盖已有内容。</summary>
    private static string NewFolderName(string parent, string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) stem = "解压结果";
        var candidate = Path.Combine(parent, stem);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = Path.Combine(parent, $"{stem} ({suffix})");
        return candidate;
    }

    private async Task RunArchiveAsync(string busyMessage,
        Func<IProgress<string>, CancellationToken, ArchiveResult> action, Func<ArchiveResult, string> done)
    {
        if (_busy) return;
        SetBusy(true, busyMessage);
        var token = _operation!.Token;
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await Task.Run(() => action(progress, token), token);
            StatusText.Text = done(result);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消，未生成结果文件。"; }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "操作失败，请检查目标位置是否可写、磁盘空间是否足够。"; }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
        RefreshState();
        if (busy) _operation = new CancellationTokenSource();
        else { _operation?.Dispose(); _operation = null; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        StatusText.Text = "正在取消，请等待当前文件处理结束…";
    }
}
