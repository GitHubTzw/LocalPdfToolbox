using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class MainWindow : Window
{
    private readonly IPdfWorkspace _workspace = new PdfWorkspace();
    private readonly PdfPreviewRenderer _renderer = new();
    private readonly List<PageItem[]> _undo = [];
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closing;
    private bool _suppressSelectionPreview;
    private int _previewVersion;
    private string? _initialDirectory;
    private PageItem? _previewPage;
    public ObservableCollection<PageItem> Pages { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        PreviewKeyDown += (_, e) =>
        {
            if (_busy || e.OriginalSource is TextBox) return;
            if (e.Key == Key.Delete) { Delete_Click(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { PageList.SelectAll(); e.Handled = true; }
        };
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF 文档 (*.pdf)|*.pdf", Multiselect = true, Title = "添加本地 PDF", CheckFileExists = true, AddToRecent = false, DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) await ImportFilesAsync(dialog.FileNames);
    }

    public async Task ImportFilesAsync(IEnumerable<string> files)
    {
        if (_busy || _closing) return;
        SetBusy(true, "正在读取本地文件…");
        var imported = 0;
        var failures = new List<string>();
        foreach (var path in files)
        {
            if (_operation!.IsCancellationRequested) break;
            PdfSource? source = null;
            try
            {
                source = await Task.Run(() => _workspace.Load(path));
                var loaded = source;
                await Task.Run(() => _renderer.Add(loaded.Id, _workspace.GetContent(loaded.Id)));
                Sources.Add(new SourceItem(source.Id, source.FileName, source.Pages.Count, source.ByteCount));
                foreach (var page in source.Pages) Pages.Add(new PageItem(source.Id, source.FileName, page.PageNumber));
                _initialDirectory ??= Path.GetDirectoryName(Path.GetFullPath(path));
                imported++;
            }
            catch (Exception error)
            {
                if (source is not null) { _renderer.Remove(source.Id); _workspace.Remove(source.Id); }
                // 错误仅显示在本次窗口中，不写文件路径、异常堆栈或文档内容到日志。
                failures.Add(error is PdfToolException ? error.Message : "有文件无法读取或预览，请确认它是有效的普通 PDF。");
            }
        }
        _undo.Clear();
        SetBusy(false, imported > 0 ? $"已添加 {imported} 个文档，共 {Pages.Count} 页。" : "未添加文档。");
        RefreshPages();
        if (Pages.Count > 0) PageList.SelectedItem = Pages[0];
        if (failures.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", failures.Distinct().Take(5)), "部分文件未导入", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportFilesAsync(files);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItems.Count == Pages.Count) PageList.UnselectAll(); else PageList.SelectAll();
    }

    private void SelectRange_Click(object sender, RoutedEventArgs e) => SelectRange();
    private void PageRange_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SelectRange(); e.Handled = true; }
    }

    private void SelectRange()
    {
        try
        {
            var positions = ParsePageRange(PageRangeBox.Text, Pages.Count);
            _suppressSelectionPreview = true;
            try
            {
                PageList.UnselectAll();
                foreach (var position in positions) PageList.SelectedItems.Add(Pages[position - 1]);
            }
            finally { _suppressSelectionPreview = false; }
            if (positions.Count > 0) _ = ShowPreviewAsync(Pages[positions[0] - 1]);
            if (positions.Count > 0) PageList.ScrollIntoView(Pages[positions[0] - 1]);
            StatusText.Text = $"已选中 {positions.Count} 页；可旋转、删除或仅保留这些页面。";
        }
        catch (ArgumentException error) { StatusText.Text = error.Message; }
    }

    public static IReadOnlyList<int> ParsePageRange(string text, int pageCount)
    {
        if (pageCount == 0) throw new ArgumentException("请先添加 PDF。");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入页码，例如 1-3,5。");
        var positions = new SortedSet<int>();
        foreach (var part in text.Replace('，', ',').Split(','))
        {
            var range = part.Trim().Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0].Trim(), out var start))
                throw new ArgumentException("页码格式有误，请使用 1-3,5 这样的格式。");
            var end = start;
            if (range.Length == 2 && !int.TryParse(range[1].Trim(), out end))
                throw new ArgumentException("范围的结束页码无效。");
            if (start < 1 || end < start || end > pageCount)
                throw new ArgumentException($"页码应在 1–{pageCount} 之间，范围需从小到大。");
            for (var page = start; page <= end; page++) positions.Add(page);
        }
        return positions.ToArray();
    }

    private HashSet<PageItem> SelectedPages() => PageList.SelectedItems.Cast<PageItem>().ToHashSet();

    private bool HasSelection()
    {
        if (PageList.SelectedItems.Count > 0) return true;
        StatusText.Text = "请先勾选需要操作的页面。";
        return false;
    }

    private void Remember()
    {
        _undo.Add(Pages.Select(p => p.Clone()).ToArray());
        if (_undo.Count > 20) _undo.RemoveAt(0);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelection(1);

    private void MoveSelection(int direction)
    {
        if (!HasSelection()) return;
        var selected = SelectedPages();
        Remember();
        if (direction < 0)
        {
            for (var i = 1; i < Pages.Count; i++)
                if (selected.Contains(Pages[i]) && !selected.Contains(Pages[i - 1])) Pages.Move(i, i - 1);
        }
        else
        {
            for (var i = Pages.Count - 2; i >= 0; i--)
                if (selected.Contains(Pages[i]) && !selected.Contains(Pages[i + 1])) Pages.Move(i, i + 1);
        }
        RefreshPages();
        StatusText.Text = "已调整输出页序；可以撤销。";
    }

    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        Remember();
        foreach (var page in SelectedPages()) page.Rotation += 90;
        RefreshPages();
        StatusText.Text = "所选页面已顺时针旋转 90°。";
        if (_previewPage is not null) await ShowPreviewAsync(_previewPage);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        var selected = SelectedPages();
        Remember();
        foreach (var page in selected) Pages.Remove(page);
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = $"已从输出列表移除 {selected.Count} 页。原文件未修改。";
    }

    private void KeepSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        var selected = SelectedPages();
        Remember();
        foreach (var page in Pages.Where(p => !selected.Contains(p)).ToArray()) Pages.Remove(page);
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = $"仅保留选中的 {Pages.Count} 页，可单独导出。";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Pages.Clear();
        foreach (var page in snapshot) Pages.Add(page);
        RefreshPages();
        if (Pages.Count > 0) PageList.SelectedItem = Pages[0]; else ClearPreview();
        StatusText.Text = "已撤销上一步页面操作。";
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (_previewPage?.SourceId == id) _previewVersion++;
        foreach (var page in Pages.Where(p => p.SourceId == id).ToArray()) Pages.Remove(page);
        var source = Sources.FirstOrDefault(s => s.Id == id);
        if (source is not null) Sources.Remove(source);
        _renderer.Remove(id);
        _workspace.Remove(id);
        _undo.Clear();
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = "已移除此文档。原文件未修改。";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Sources.Count > 0 && MessageBox.Show(this, "清空本次导入及页面调整？原文件不会改变。", "清空工作区", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ClearPreview();
        Pages.Clear();
        Sources.Clear();
        _undo.Clear();
        _renderer.Clear();
        _workspace.Clear();
        _initialDirectory = null;
        PageRangeBox.Clear();
        RefreshPages();
        StatusText.Text = "本次会话已清空，请添加新的 PDF。";
    }

    private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSummary();
        if (_suppressSelectionPreview) return;
        var page = e.AddedItems.OfType<PageItem>().LastOrDefault() ?? PageList.SelectedItems.OfType<PageItem>().FirstOrDefault();
        if (page is not null) await ShowPreviewAsync(page);
        else if (!_busy) ClearPreview();
    }

    private async Task ShowPreviewAsync(PageItem page)
    {
        if (_closing || !Pages.Contains(page)) return;
        var version = ++_previewVersion;
        _previewPage = page;
        PreviewPlaceholder.Text = "正在生成本地预览…";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        try
        {
            // 连续多选或快速切换时只渲染最后一页，避免排队解析大量过时的预览。
            await Task.Delay(60);
            if (version != _previewVersion || _closing) return;
            var image = await Task.Run(() => _renderer.Render(page.SourceId, page.PageNumber, page.Rotation, 600, 850));
            if (version != _previewVersion || _closing) return;
            PreviewImage.Source = image;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewPosition.Text = $"第 {page.Position} 页";
            PreviewCaption.Text = $"{page.FileName} · 原第 {page.PageNumber} 页";
        }
        catch (Exception)
        {
            if (version != _previewVersion || _closing) return;
            PreviewImage.Source = null;
            PreviewPlaceholder.Text = "此页暂时无法生成预览。";
        }
    }

    private void ClearPreview()
    {
        _previewVersion++;
        _previewPage = null;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "选择一页，即可查看预览";
        PreviewPosition.Text = "";
        PreviewCaption.Text = "";
    }

    private async void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _previewPage is null || _busy) return;
        var page = _previewPage;
        try
        {
            var image = await Task.Run(() => _renderer.Render(page.SourceId, page.PageNumber, page.Rotation, 1200, 1600));
            if (_closing) return;
            var window = new Window
            {
                Owner = this, Title = $"页面预览 · {page.FileName} · 原第 {page.PageNumber} 页",
                Width = 950, Height = 820, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 241, 237)),
                Content = new Image { Source = image, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(20) }
            };
            window.ShowDialog();
        }
        catch (Exception) { StatusText.Text = "此页暂时无法放大预览。"; }
    }

    private void ResetPreviewIfRemoved()
    {
        if (_previewPage is null || Pages.Contains(_previewPage)) return;
        ClearPreview();
        if (Pages.Count > 0) PageList.SelectedItem = Pages[0];
    }

    private void RefreshPages()
    {
        for (var i = 0; i < Pages.Count; i++) Pages[i].Position = i + 1;
        EmptyState.Visibility = Pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !_busy && Pages.Count > 0;
        UndoButton.IsEnabled = _undo.Count > 0;
        if (_previewPage is not null) PreviewPosition.Text = $"第 {_previewPage.Position} 页";
        UpdateSummary();
    }

    private void UpdateSummary() => PageSummary.Text = $"{Pages.Count} 页 · 已选 {PageList.SelectedItems.Count} 页";

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Pages.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "PDF 文档 (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true,
            FileName = Sources.Count == 1 ? "整理后的文档.pdf" : "合并后的文档.pdf",
            Title = "另存为新的本地 PDF（不覆盖已有文件）", OverwritePrompt = false,
            AddToRecent = false, DereferenceLinks = false,
            InitialDirectory = _initialDirectory ?? ""
        };
        if (dialog.ShowDialog(this) != true) return;
        var pages = Pages.Select(p => p.ToEdit()).ToArray();
        SetBusy(true, $"正在本地生成 {pages.Length} 页 PDF…");
        try
        {
            var result = await Task.Run(() => _workspace.Export(pages, dialog.FileName, _operation!.Token));
            StatusText.Text = $"导出完成：{result.PageCount} 页，{result.ByteCount / 1024d / 1024d:0.##} MB。";
            MessageBox.Show(this, $"已保存 {result.PageCount} 页 PDF。\n\n{dialog.FileName}\n\n原文件未修改，未添加水印。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导出，原文件未修改。"; }
        catch (PdfToolException error) { StatusText.Text = "未导出。"; MessageBox.Show(this, error.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception) { StatusText.Text = "导出失败。"; MessageBox.Show(this, "请确认保存位置是可写的本地文件夹，且文件名尚未被占用。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        WorkspaceArea.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy && Pages.Count > 0;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
        if (busy) _operation = new CancellationTokenSource();
        else { _operation?.Dispose(); _operation = null; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        StatusText.Text = "正在取消，请等待当前页面处理结束…";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            _operation?.Cancel();
            StatusText.Text = "正在结束处理，请稍后关闭窗口。";
            e.Cancel = true;
            return;
        }
        _closing = true;
        ClearPreview();
        Pages.Clear();
        Sources.Clear();
        _undo.Clear();
        _renderer.Dispose();
        _workspace.Dispose();
    }
}
