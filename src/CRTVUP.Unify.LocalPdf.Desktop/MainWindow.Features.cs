using System.Security.Cryptography;
using System.Windows;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class MainWindow
{
    private async void EditContent_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (PageList.SelectedItems.Count != 1) { StatusText.Text = "请只选中一页，再点击“编辑内容”。"; return; }
        var page = (PageItem)PageList.SelectedItems[0]!;
        _previewVersion++;
        try
        {
            var editor = new ContentEditorWindow(_workspace.GetContent(page.SourceId), page.PageNumber, page.Rotation) { Owner = this };
            if (editor.ShowDialog() != true || editor.EditedContent is not { } bytes) return;
            SetBusy(true, "正在把内容修改保存到工作区…");
            try
            {
                var updated = await Task.Run(() => _workspace.ReplaceContent(page.SourceId, bytes));
                await Task.Run(() => _renderer.Add(updated.Id, _workspace.GetContent(updated.Id)));
                var source = Sources.First(item => item.Id == updated.Id);
                Sources[Sources.IndexOf(source)] = new SourceItem(updated.Id, updated.FileName, updated.Pages.Count, updated.ByteCount);
                StatusText.Text = "内容修改已保存到工作区。点击“合并并导出 PDF”保存新文件。";
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) { StatusText.Text = error is PdfToolException ? error.Message : "无法完成此页面的内容编辑，请重新打开页面检查。"; }
        finally
        {
            if (_busy) SetBusy(false, StatusText.Text);
            if (!_closing && Pages.Contains(page)) await ShowPreviewAsync(page);
        }
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var edits = Pages.Select(page => page.ToEdit()).ToArray();
        var dialog = new ConversionWindow(edits.Length, token => _workspace.ExportToMemory(edits, token)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ImportedPdf is not { } bytes) return;
        try { await ImportGeneratedPdfAsync(dialog.ImportedName, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task ImportGeneratedPdfAsync(string name, ReadOnlyMemory<byte> bytes)
    {
        if (_busy || _closing) return;
        SetBusy(true, "正在把转换结果加入工作区…");
        PdfSource? source = null;
        try
        {
            source = await Task.Run(() => _workspace.LoadFromMemory(name, bytes));
            var loaded = source;
            await Task.Run(() => _renderer.Add(loaded.Id, _workspace.GetContent(loaded.Id)));
            Sources.Add(new SourceItem(source.Id, source.FileName, source.Pages.Count, source.ByteCount));
            foreach (var page in source.Pages) Pages.Add(new PageItem(source.Id, source.FileName, page.PageNumber));
            _undo.Clear();
            StatusText.Text = $"已加入转换结果：{source.Pages.Count} 页。可继续整理或导出新的 PDF。";
        }
        catch (Exception error)
        {
            if (source is not null) { _renderer.Remove(source.Id); _workspace.Remove(source.Id); }
            StatusText.Text = error is PdfToolException ? error.Message : "转换结果未能加入工作区，当前文档保持不变。";
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            RefreshPages();
            if (source is not null) PageList.SelectedItem = Pages.FirstOrDefault(page => page.SourceId == source.Id);
        }
    }
}
