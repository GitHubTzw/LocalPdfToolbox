using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class ConversionWindow : Window
{
    private readonly Func<CancellationToken, byte[]> _pdfProvider;
    private readonly int _pageCount;
    private readonly List<string> _inputPaths = [];
    private CancellationTokenSource? _operation;
    private bool _busy;
    private string _conversionWarning = "";
    public byte[]? ImportedPdf { get; private set; }
    public string ImportedName { get; private set; } = "转换后的文档.pdf";

    public ConversionWindow(int pageCount, Func<CancellationToken, byte[]> pdfProvider)
    {
        _pdfProvider = pdfProvider;
        _pageCount = pageCount;
        InitializeComponent();
        PdfSourceLabel.Text = pageCount > 0 ? $"当前工作区：{pageCount} 页 · 按主窗口的页面顺序转换" : "请先在主窗口添加 PDF，或使用“图片 / 文字转 PDF”。";
        FormatList.ItemsSource = new[]
        {
            new FormatOption(PdfExportFormat.Png, "PNG 图片", "每页一张图片；多页保存为 ZIP，便于一起取出。", "png"),
            new FormatOption(PdfExportFormat.Jpeg, "JPEG 图片", "每页一张图片；多页保存为 ZIP，便于一起取出。", "jpg"),
            new FormatOption(PdfExportFormat.Text, "TXT 纯文字", "提取已有文字，保留换行；不包含扫描图片里的字。", "txt"),
            new FormatOption(PdfExportFormat.WordText, "Word · 可编辑文字版", "文字可继续编辑；重新排版，不还原复杂表格和原版式。", "docx"),
            new FormatOption(PdfExportFormat.WordImages, "Word · 页面图片版", "完整显示页面外观；页面中的文字保留在图片中。", "docx"),
            new FormatOption(PdfExportFormat.ExcelText, "Excel · 文字行版", "每页一个工作表，每行一格文字；不识别表格结构。", "xlsx"),
            new FormatOption(PdfExportFormat.PowerPointImages, "PowerPoint · 页面图片版", "每页一张幻灯片，保留外观；页面内容是一张图片。", "pptx")
        };
        FormatList.SelectedIndex = 0;
        ConvertExport.IsEnabled = pageCount > 0;
        if (pageCount == 0) ConversionTabs.SelectedIndex = 1;
        UpdateImportDescription();
        Closed += (_, _) => { _inputPaths.Clear(); InputList.ItemsSource = null; };
    }

    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FormatList.SelectedItem is not FormatOption option || DpiChoice is null) return;
        DpiChoice.IsEnabled = option.Format is PdfExportFormat.Png or PdfExportFormat.Jpeg or PdfExportFormat.WordImages or PdfExportFormat.PowerPointImages;
        ExportDetail.Text = option.Format is PdfExportFormat.Text or PdfExportFormat.WordText or PdfExportFormat.ExcelText
            ? "文字版只处理 PDF 已有的文字层。扫描页可能为空，阅读顺序取决于原文件；请核对转换结果。"
            : "图片版保留页面外观，文字会随页面一起变为图片。可在主工作区先选页、排序和旋转。";
    }

    private void ImportKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        _inputPaths.Clear();
        if (InputList is null) return;
        InputList.ItemsSource = null;
        UpdateImportDescription();
    }

    private void UpdateImportDescription()
    {
        if (ImportDetail is null) return;
        ImportDetail.Text = ImportKind.SelectedIndex == 0
            ? "支持多张图片和多页 TIFF。图片保持原比例；加入工作区后可以调整页序。"
            : "仅提取文字或已保存的单元格值，再生成 PDF。不会保留图片、图表、原排版或数字格式，不执行公式，也不读取外部资源。旧版 DOC / XLS / PPT 不支持。";
        if (ImportConvert is null) return;
        ImportConvert.Content = ImportKind.SelectedIndex == 0 ? "转换并加入工作区" : "读取文字并编辑 / 转换";
        ImportActionHint.Text = ImportKind.SelectedIndex == 0
            ? "转换后加入主工作区，可继续排序、编辑和合并，再导出 PDF。"
            : "先检查和编辑提取的文字，再生成 PDF，或直接另存为 TXT / DOCX / XLSX 文字版。";
    }

    private void ChooseInput_Click(object sender, RoutedEventArgs e)
    {
        var images = ImportKind.SelectedIndex == 0;
        var dialog = new OpenFileDialog
        {
            Filter = images ? "本地图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" : "本地文字文档|*.docx;*.xlsx;*.pptx;*.txt",
            Multiselect = images, Title = images ? "选择本地图片" : "选择要提取文字的本地文档", CheckFileExists = true, AddToRecent = false, DereferenceLinks = false
        };
        if (dialog.ShowDialog(this) != true) return;
        _inputPaths.Clear();
        _inputPaths.AddRange(dialog.FileNames);
        InputList.ItemsSource = _inputPaths.Select(path => new { Name = Path.GetFileName(path) }).ToArray();
    }

    private async void ExportFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pageCount == 0 || FormatList.SelectedItem is not FormatOption option) return;
        var extension = _pageCount > 1 && option.Format is PdfExportFormat.Png or PdfExportFormat.Jpeg ? "zip" : option.Extension;
        var dialog = new SaveFileDialog { Filter = $"{option.Label} (*.{extension})|*.{extension}", DefaultExt = "." + extension, AddExtension = true,
            FileName = "转换结果." + extension, Title = "另存本地转换结果（使用新文件名）", OverwritePrompt = false, AddToRecent = false, DereferenceLinks = false };
        if (dialog.ShowDialog(this) != true) return;
        var dpi = DpiChoice.SelectedIndex switch { 0 => 96, 2 => 200, _ => 150 };
        SetBusy(true);
        _conversionWarning = "";
        var progress = Progress();
        byte[]? pdf = null;
        byte[]? output = null;
        try
        {
            var token = _operation!.Token;
            pdf = await Task.Run(() => _pdfProvider(token));
            output = await Task.Run(() => LocalConversionService.ExportPdf(pdf, option.Format, dpi, progress, token));
            await Task.Run(() => LocalFileStore.WriteNew(dialog.FileName, output, token));
            ConversionStatus.Text = "转换完成，已保存新文件。" + _conversionWarning;
            MessageBox.Show(this, $"已保存：\n{dialog.FileName}" + (_conversionWarning.Length > 0 ? "\n\n" + _conversionWarning : ""), "本地转换完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { ConversionStatus.Text = "已取消，原文件未修改。"; }
        catch (Exception error) { ShowError(error); }
        finally
        {
            if (pdf is not null) CryptographicOperations.ZeroMemory(pdf);
            if (output is not null) CryptographicOperations.ZeroMemory(output);
            SetBusy(false);
        }
    }

    private async void ImportConvert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_inputPaths.Count == 0) { ConversionStatus.Text = "请先选择本地文件。"; return; }
        var images = ImportKind.SelectedIndex == 0;
        var paths = _inputPaths.ToArray();
        SetBusy(true);
        var progress = Progress();
        try
        {
            var token = _operation!.Token;
            if (images) ImportedPdf = await Task.Run(() => LocalConversionService.ImagesToPdf(paths, progress, token));
            else
            {
                var text = await Task.Run(() => LocalConversionService.ReadText(paths[0], token));
                token.ThrowIfCancellationRequested();
                SetBusy(false);
                ConversionStatus.Text = "已读取文字。可在编辑窗口另存文字版文件，或生成 PDF。";
                var editor = new TextContentEditorWindow(text) { Owner = this };
                if (editor.ShowDialog() != true || editor.GeneratedPdf is not { } editedPdf) return;
                ImportedPdf = editedPdf;
            }
            ImportedName = images ? "图片转换.pdf" : Path.GetFileNameWithoutExtension(paths[0]) + "_文字版.pdf";
            if (_busy) SetBusy(false);
            DialogResult = true;
        }
        catch (OperationCanceledException) { ConversionStatus.Text = "已取消，原文件未修改。"; }
        catch (Exception error) { ShowError(error); }
        finally { if (_busy) SetBusy(false); }
    }

    private IProgress<string> Progress()
    {
        var operation = _operation;
        return new Progress<string>(message =>
        {
            if (!_busy || !ReferenceEquals(_operation, operation)) return;
            ConversionStatus.Text = message;
            if (message.Contains("没有可提取文字", StringComparison.Ordinal)) _conversionWarning = message;
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConversionTabs.IsEnabled = !busy;
        ConversionProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelConversion.Content = busy ? "取消处理" : "关闭";
        if (busy) { _operation = new CancellationTokenSource(); ConversionStatus.Text = "正在本地处理…"; }
        else { _operation?.Dispose(); _operation = null; }
    }

    private void ShowError(Exception error) => ConversionStatus.Text = error is PdfToolException ? error.Message : "此文件无法完成转换，请检查格式、编码或尝试较小的文件。";
    private void CancelConversion_Click(object sender, RoutedEventArgs e) { if (_busy) _operation?.Cancel(); else Close(); }
    private void Conversion_Closing(object? sender, CancelEventArgs e) { if (_busy) { _operation?.Cancel(); e.Cancel = true; ConversionStatus.Text = "正在结束当前本地处理，请稍候再关闭。"; } }
    private sealed record FormatOption(PdfExportFormat Format, string Label, string Detail, string Extension);
}
