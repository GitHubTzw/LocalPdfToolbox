using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class TextContentEditorWindow : Window
{
    private readonly List<string> _sections;
    private int _selectedSection = -1;
    private bool _changingSection;
    private bool _busy;
    private bool _dirty;
    private bool _accepted;
    private CancellationTokenSource? _operation;
    public byte[]? GeneratedPdf { get; private set; }

    public TextContentEditorWindow(ImportedText text)
    {
        _sections = text.Sections.ToList();
        if (_sections.Count is < 1 or > 500) throw new PdfToolException("文字分节数量无效。");
        InitializeComponent();
        LimitationText.Text = text.Limitation;
        SectionList.ItemsSource = Enumerable.Range(1, _sections.Count).Select(index => $"第 {index} 节").ToArray();
        SectionList.SelectedIndex = 0;
        Closed += (_, _) =>
        {
            _changingSection = true;
            SectionText.Clear();
            SectionText.IsUndoEnabled = false;
            SectionList.ItemsSource = null;
            _sections.Clear();
            _operation?.Dispose();
        };
    }

    private void Section_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SectionText is null || SectionList.SelectedIndex < 0) return;
        SaveCurrentSection();
        _selectedSection = SectionList.SelectedIndex;
        _changingSection = true;
        try
        {
            // 换节时清空文本框撤销缓存，避免把另一节的文字撤销到当前节。
            SectionText.IsUndoEnabled = false;
            SectionText.Text = _sections[_selectedSection];
            SectionText.IsUndoEnabled = true;
            SectionText.ScrollToHome();
            SectionLabel.Text = $"编辑第 {_selectedSection + 1} / {_sections.Count} 节";
        }
        finally { _changingSection = false; }
    }

    private void SectionText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_changingSection && _selectedSection >= 0) _dirty = true;
    }

    private void SaveCurrentSection()
    {
        if (_selectedSection >= 0) _sections[_selectedSection] = SectionText.Text;
    }

    private string[] Snapshot()
    {
        SaveCurrentSection();
        if (_sections.Sum(section => (long)section.Length) > 2_000_000) throw new PdfToolException("文字总数超过 200 万字符，请减少内容。");
        if (_sections.All(string.IsNullOrWhiteSpace)) throw new PdfToolException("请至少保留一段文字再转换。");
        return _sections.ToArray();
    }

    public async Task<byte[]> BuildPdfAsync()
    {
        if (_busy) throw new PdfToolException("当前正在处理，请稍候。");
        var sections = Snapshot();
        SetBusy(true);
        try
        {
            var token = _operation!.Token;
            var result = await Task.Run(() => TextPdfBuilder.Build(sections, token));
            if (token.IsCancellationRequested) { CryptographicOperations.ZeroMemory(result); token.ThrowIfCancellationRequested(); }
            EditorStatus.Text = "已在本地生成 PDF，文字内容可继续检查。";
            return result;
        }
        finally { Array.Clear(sections); SetBusy(false); }
    }

    private async void BuildPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            GeneratedPdf = await BuildPdfAsync();
            _accepted = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) { EditorStatus.Text = "已取消，编辑的文字仍保留在当前窗口。"; }
        catch (Exception error) { ShowError(error); }
    }

    private async void ExportText_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var format = OutputFormat.SelectedIndex switch { 1 => PdfExportFormat.WordText, 2 => PdfExportFormat.ExcelText, _ => PdfExportFormat.Text };
        var extension = format switch { PdfExportFormat.WordText => "docx", PdfExportFormat.ExcelText => "xlsx", _ => "txt" };
        var dialog = new SaveFileDialog
        {
            Filter = $"文字版文档 (*.{extension})|*.{extension}", DefaultExt = "." + extension, AddExtension = true,
            FileName = "编辑后的文字." + extension, Title = "另存文字新文件", OverwritePrompt = false, AddToRecent = false, DereferenceLinks = false
        };
        if (dialog.ShowDialog(this) != true) return;
        byte[]? output = null;
        string[]? sections = null;
        try
        {
            sections = Snapshot();
            SetBusy(true);
            var token = _operation!.Token;
            output = await Task.Run(() => LocalConversionService.ExportText(sections, format, token));
            await Task.Run(() => LocalFileStore.WriteNew(dialog.FileName, output, token));
            _dirty = false;
            EditorStatus.Text = "文字版新文件已保存。可继续编辑或生成 PDF。";
        }
        catch (OperationCanceledException) { EditorStatus.Text = "已取消，编辑的文字仍保留在当前窗口。"; }
        catch (Exception error) { ShowError(error); }
        finally
        {
            if (output is not null) CryptographicOperations.ZeroMemory(output);
            if (sections is not null) Array.Clear(sections);
            if (_busy) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        EditingArea.IsEnabled = !busy;
        OutputFormat.IsEnabled = !busy;
        ExportTextButton.IsEnabled = !busy;
        BuildPdfButton.IsEnabled = !busy;
        CloseEditor.Content = busy ? "取消处理" : "关闭";
        if (busy) { _operation = new CancellationTokenSource(); EditorStatus.Text = "正在本地处理文字…"; }
        else { _operation?.Dispose(); _operation = null; }
    }

    private void ShowError(Exception error) => EditorStatus.Text = error is PdfToolException ? error.Message : "无法完成文字转换，请检查内容或减少文字量。";
    private void CloseEditor_Click(object sender, RoutedEventArgs e) { if (_busy) _operation?.Cancel(); else Close(); }
    private void Editor_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { _operation?.Cancel(); e.Cancel = true; return; }
        if (_dirty && !_accepted && MessageBox.Show(this, "放弃尚未导出的文字修改？", "关闭文字编辑", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
