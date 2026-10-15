using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.DesktopV2.Views;

/// <summary>
/// 文件对比页签：文本逐行并排对比（含行内差异高亮）、二进制字节对比、目录递归对比。
/// 两侧内容只在本机内存中处理，不写日志、不上传。
/// </summary>
public partial class DiffTab : UserControl
{
    private readonly ITextDiffTool _text = new TextDiffTool();
    private readonly IBinaryDiffTool _binary = new BinaryDiffTool();
    private readonly IDirectoryDiffTool _directory = new DirectoryDiffTool();
    private IReadOnlyList<DiffLine> _lines = [];
    private IReadOnlyList<DirectoryDiffEntry> _entries = [];
    private string? _leftPath;
    private string? _rightPath;
    private string? _leftFolder;
    private string? _rightFolder;
    private string _summary = "尚未对比";
    private string _similarity = "";

    /// <summary>对比表格里的一行；行内差异已经切好片段，直接绑定显示。</summary>
    public sealed record DiffRow(int? LeftNumber, int? RightNumber, DiffLineKind Kind,
        IReadOnlyList<InlineSegment> LeftSegments, IReadOnlyList<InlineSegment> RightSegments);

    /// <summary>目录对比表格里的一行。</summary>
    public sealed record DirectoryRow(string RelativePath, DirectoryEntryState State, string StateName,
        string LeftText, string RightText);

    public DiffTab() => InitializeComponent();

    private int Mode => ModeChoice.SelectedIndex;
    private bool BinaryMode => Mode == 1;
    private bool DirectoryMode => Mode == 2;

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 初始化阶段控件树尚未构建完成，此时只依赖 XAML 的默认可见性。
        if (TextInputPanel is null || BinaryPanel is null || DirectoryPanel is null) return;
        TextInputPanel.Visibility = Mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        BinaryPanel.Visibility = BinaryMode ? Visibility.Visible : Visibility.Collapsed;
        DirectoryPanel.Visibility = DirectoryMode ? Visibility.Visible : Visibility.Collapsed;
        DiffGrid.Visibility = DirectoryMode ? Visibility.Collapsed : Visibility.Visible;
        DirectoryGrid.Visibility = DirectoryMode ? Visibility.Visible : Visibility.Collapsed;
        OnlyDifferences.IsEnabled = !BinaryMode;
        InlineDiff.IsEnabled = Mode == 0;
        _lines = [];
        _entries = [];
        DiffGrid.ItemsSource = null;
        DirectoryGrid.ItemsSource = null;
        _summary = "尚未对比";
        _similarity = "";
        SummaryText.Text = _summary;
        SimilarityText.Text = "";
        StatusText.Text = Mode switch
        {
            1 => "二进制模式按字节比较两个本地文件，不解析内容。",
            2 => "目录模式递归比较两个文件夹，可只比大小或按内容摘要比较。",
            _ => "两侧内容只在本机内存中对比，不上传、不写日志。"
        };
    }

    // ---------------------------------------------------------------- 执行对比

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (BinaryMode) CompareBinary();
        else if (DirectoryMode) await CompareDirectoryAsync();
        else CompareText();
    }

    private void CompareText()
    {
        try
        {
            var result = _text.Compare(LeftBox.Text, RightBox.Text, new DiffOptions
            {
                IgnoreCase = IgnoreCase.IsChecked == true,
                IgnoreWhitespace = IgnoreWhitespace.IsChecked == true,
                IgnoreBlankLines = IgnoreBlankLines.IsChecked == true,
                IgnoreTrailingWhitespace = IgnoreTrailing.IsChecked == true,
                IgnoreCommentLines = IgnoreComments.IsChecked == true
            });
            _lines = result.Lines;
            _summary = result.Summary;
            SummaryText.Text = _summary;
            Render();
            var total = result.Lines.Count;
            var same = total - result.Added - result.Removed - result.Changed;
            _similarity = total == 0 ? "相似度：—" : $"相似度：{same * 100d / total:0.#}%";
            SimilarityText.Text = _similarity;
            StatusText.Text = result.Identical ? "两侧文本完全一致。" : "已在本机完成逐行对比。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "对比失败，请检查输入内容。"; }
    }

    private void CompareBinary()
    {
        if (string.IsNullOrEmpty(_leftPath) || string.IsNullOrEmpty(_rightPath))
        {
            StatusText.Text = "请先选择两侧要比较的本地文件。";
            return;
        }
        byte[]? left = null;
        byte[]? right = null;
        try
        {
            left = LocalFileStore.Read(_leftPath);
            right = LocalFileStore.Read(_rightPath);
            var result = _binary.Compare(left, right);
            _lines = [];
            DiffGrid.ItemsSource = null;
            _summary = result.Summary;
            _similarity = result.Identical ? "相似度：100%" : "相似度：0%";
            SummaryText.Text = _summary;
            SimilarityText.Text = _similarity;
            StatusText.Text = result.Identical ? "两个文件完全一致。" : "两个文件不同。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "无法读取所选文件，请检查文件权限。"; }
        finally
        {
            if (left is not null) CryptographicOperations.ZeroMemory(left);
            if (right is not null) CryptographicOperations.ZeroMemory(right);
        }
    }

    private async Task CompareDirectoryAsync()
    {
        if (string.IsNullOrEmpty(_leftFolder) || string.IsNullOrEmpty(_rightFolder))
        {
            StatusText.Text = "请先选择两侧要比较的本地文件夹。";
            return;
        }
        var left = _leftFolder;
        var right = _rightFolder;
        var byContent = CompareContent.IsChecked == true;
        StatusText.Text = "正在本地扫描两个文件夹…";
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await Task.Run(() => _directory.Compare(left, right, byContent, progress));
            _entries = result.Entries;
            _summary = result.Summary;
            SummaryText.Text = _summary;
            var total = result.Entries.Count;
            _similarity = total == 0 ? "相似度：—" : $"相似度：{result.Identical * 100d / total:0.#}%";
            SimilarityText.Text = _similarity;
            Render();
            StatusText.Text = "已在本机完成目录对比。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "目录对比失败，请检查文件夹权限。"; }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (DiffGrid is null) return;
        Render();
    }

    private void Render()
    {
        if (DiffGrid is null) return;
        if (DirectoryMode)
        {
            DirectoryGrid.ItemsSource = _entries.Select(entry => new DirectoryRow(entry.RelativePath, entry.State,
                Describe(entry.State), FileSizeFormatter.Format(entry.LeftBytes), FileSizeFormatter.Format(entry.RightBytes))).ToArray();
            return;
        }
        var source = OnlyDifferences.IsChecked == true
            ? _lines.Where(line => line.Kind != DiffLineKind.Equal)
            : _lines;
        DiffGrid.ItemsSource = source.Select(Build).ToArray();
    }

    /// <summary>把一行对比结果转成可绑定的行；开启行内差异时按词切分，否则整行一个片段。</summary>
    private DiffRow Build(DiffLine line)
    {
        var inline = InlineDiff.IsChecked == true && line.Kind == DiffLineKind.Changed;
        var leftSegments = Segments(line.LeftText, line.RightText, inline);
        var rightSegments = Segments(line.RightText, line.LeftText, inline);
        return new DiffRow(line.LeftNumber, line.RightNumber, line.Kind, leftSegments, rightSegments);
    }

    /// <summary>本侧独有片段标为高亮；底色由表格列自己决定（左侧删除色、右侧新增色）。</summary>
    private IReadOnlyList<InlineSegment> Segments(string? own, string? other, bool inline)
    {
        var text = own ?? "";
        if (text.Length == 0) return [];
        if (!inline) return [new InlineSegment(text, false)];
        return _text.CompareInline(text, other ?? "");
    }

    private static string Describe(DirectoryEntryState state) => state switch
    {
        DirectoryEntryState.Identical => "相同",
        DirectoryEntryState.Modified => "已修改",
        DirectoryEntryState.LeftOnly => "仅左侧",
        _ => "仅右侧"
    };

    // ---------------------------------------------------------------- 报告导出

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0 && _entries.Count == 0)
        {
            StatusText.Text = "还没有对比结果，请先执行一次对比。";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "文本报告 (*.txt)|*.txt|Markdown 报告 (*.md)|*.md",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = "对比报告.txt",
            Title = "另存为新的本地报告（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            LocalTextFile.WriteNew(dialog.FileName, BuildReport());
            StatusText.Text = "报告已保存为新文件；两侧原文件未被修改。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private string BuildReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 本地文件对比报告").AppendLine();
        builder.AppendLine($"- 对比方式：{(DirectoryMode ? "目录对比" : BinaryMode ? "二进制对比" : "文本对比")}");
        builder.AppendLine($"- 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 结果：{_summary}");
        if (_similarity.Length > 0) builder.AppendLine($"- {_similarity}");
        builder.AppendLine();
        if (DirectoryMode)
        {
            builder.AppendLine("| 相对路径 | 状态 | 左侧大小 | 右侧大小 |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var entry in _entries)
                builder.AppendLine($"| {entry.RelativePath} | {Describe(entry.State)} | {FileSizeFormatter.Format(entry.LeftBytes)} | {FileSizeFormatter.Format(entry.RightBytes)} |");
        }
        else
        {
            builder.AppendLine("| 左行 | 左侧内容 | 右行 | 右侧内容 |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var line in _lines.Where(item => item.Kind != DiffLineKind.Equal))
                builder.AppendLine($"| {line.LeftNumber?.ToString() ?? ""} | {Escape(line.LeftText)} | {line.RightNumber?.ToString() ?? ""} | {Escape(line.RightText)} |");
        }
        return builder.ToString();

        static string Escape(string? value) =>
            (value ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
    }

    // ---------------------------------------------------------------- 输入与文件

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftBox.Text, RightBox.Text) = (RightBox.Text, LeftBox.Text);
        (LeftFileName.Text, RightFileName.Text) = (RightFileName.Text, LeftFileName.Text);
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        (LeftFolder.Text, RightFolder.Text) = (RightFolder.Text, LeftFolder.Text);
        (_leftPath, _rightPath) = (_rightPath, _leftPath);
        (_leftFolder, _rightFolder) = (_rightFolder, _leftFolder);
        StatusText.Text = "已交换两侧内容。";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        LeftBox.Clear();
        RightBox.Clear();
        LeftFileName.Text = "";
        RightFileName.Text = "";
        LeftPath.Clear();
        RightPath.Clear();
        LeftFolder.Clear();
        RightFolder.Clear();
        _leftPath = null;
        _rightPath = null;
        _leftFolder = null;
        _rightFolder = null;
        _lines = [];
        _entries = [];
        DiffGrid.ItemsSource = null;
        DirectoryGrid.ItemsSource = null;
        _summary = "尚未对比";
        _similarity = "";
        SummaryText.Text = _summary;
        SimilarityText.Text = "";
        StatusText.Text = "已清空两侧内容。";
    }

    private void LoadLeft_Click(object sender, RoutedEventArgs e) => LoadText(side: true);
    private void LoadRight_Click(object sender, RoutedEventArgs e) => LoadText(side: false);

    private void LoadText(bool side)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "文本文件 (*.txt;*.json;*.xml;*.csv;*.log)|*.txt;*.json;*.xml;*.csv;*.log|所有文件 (*.*)|*.*",
            Title = side ? "读取左侧本地文本" : "读取右侧本地文本",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var text = LocalTextFile.Read(dialog.FileName);
            if (side)
            {
                LeftBox.Text = text;
                LeftFileName.Text = System.IO.Path.GetFileName(dialog.FileName);
            }
            else
            {
                RightBox.Text = text;
                RightFileName.Text = System.IO.Path.GetFileName(dialog.FileName);
            }
            StatusText.Text = "已读取本地文本；内容只在本次窗口中显示。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private void PickLeftBinary_Click(object sender, RoutedEventArgs e) => PickBinary(side: true);
    private void PickRightBinary_Click(object sender, RoutedEventArgs e) => PickBinary(side: false);

    private void PickBinary(bool side)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "所有文件 (*.*)|*.*",
            Title = side ? "选择左侧本地文件" : "选择右侧本地文件",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (side)
        {
            _leftPath = dialog.FileName;
            LeftPath.Text = dialog.FileName;
        }
        else
        {
            _rightPath = dialog.FileName;
            RightPath.Text = dialog.FileName;
        }
        StatusText.Text = "已选择本地文件；点击“对比”开始按字节比较。";
    }

    private void PickLeftFolder_Click(object sender, RoutedEventArgs e) => PickFolder(side: true);
    private void PickRightFolder_Click(object sender, RoutedEventArgs e) => PickFolder(side: false);

    private void PickFolder(bool side)
    {
        var dialog = new OpenFolderDialog { Title = side ? "选择左侧文件夹" : "选择右侧文件夹" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var folder = LocalPathGuard.ExistingDirectory(dialog.FolderName);
            if (side)
            {
                _leftFolder = folder;
                LeftFolder.Text = folder;
            }
            else
            {
                _rightFolder = folder;
                RightFolder.Text = folder;
            }
            StatusText.Text = "已选择本地文件夹；点击“对比”开始递归比较。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }
}
