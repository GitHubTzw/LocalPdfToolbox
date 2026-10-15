using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class ContentEditorWindow : Window
{
    private PdfiumDocument _document;
    private byte[] _bytes;
    private readonly int _pageNumber;
    private readonly int _rotation;
    private readonly List<byte[]> _undo = [];
    private PdfPageObjectCollection? _objects;
    private PdfPageObjectInfo? _selected;
    private bool _busy;
    private bool _dirty;
    private bool _accepted;
    private bool _released;
    private int _editRevision;
    private PdfBgraImage? _pendingImage;
    public byte[]? EditedContent { get; private set; }

    public ContentEditorWindow(ReadOnlyMemory<byte> content, int pageNumber, int rotation)
    {
        _bytes = content.ToArray();
        try { _document = PdfiumRuntime.Open(_bytes); }
        catch { CryptographicOperations.ZeroMemory(_bytes); throw; }
        _pageNumber = pageNumber;
        _rotation = rotation;
        InitializeComponent();
        PageLabel.Text = $"原文档第 {pageNumber} 页 · 保存会更新此原页在工作区的所有引用";
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => ReleaseResources();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) { SelectMode_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await RefreshCoreAsync(); EditorStatus.Text = "选择页面中的文字、图片或图形进行编辑；保存后还需在主窗口导出新 PDF。"; }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async Task RefreshCoreAsync()
    {
        var view = await Task.Run(() =>
        {
            var objects = _document.EnumerateObjects(_pageNumber, _rotation);
            var image = PdfPreviewRenderer.ToBitmapSource(_document.Render(_pageNumber, _rotation, 1350, 1750));
            return (objects, image);
        });
        _objects = view.objects;
        PageSurface.Width = view.image.PixelWidth;
        PageSurface.Height = view.image.PixelHeight;
        PageImage.Source = view.image;
        _selected = null;
        PageWarnings.Text = string.Join("\n", _objects.Warnings);
        SelectionLayer.Children.Clear();
        UpdateSelectionHint();
        UpdateButtons();
    }

    public async Task ApplyEditAsync(PdfEditRequest request)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => _document.ApplyEdit(request));
            _undo.Add(_bytes);
            _bytes = result;
            while (_undo.Count > 10 || _undo.Sum(bytes => (long)bytes.Length) > 128L * 1024 * 1024)
            {
                CryptographicOperations.ZeroMemory(_undo[0]);
                _undo.RemoveAt(0);
            }
            _dirty = true;
            _editRevision++;
            SetMode(EditMode.Select);
            await RefreshCoreAsync();
            EditorStatus.Text = "已修改。请检查效果，然后保存到工作区。";
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void UndoEdit_Click(object sender, RoutedEventArgs e) => await UndoEditAsync();

    public async Task UndoEditAsync()
    {
        if (_busy || _undo.Count == 0) return;
        SetBusy(true);
        try
        {
            var snapshot = _undo[^1];
            var replacement = await Task.Run(() => PdfiumRuntime.Open(snapshot));
            _document.Dispose();
            _document = replacement;
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = snapshot;
            _undo.RemoveAt(_undo.Count - 1);
            _dirty = --_editRevision > 0;
            SetMode(EditMode.Select);
            await RefreshCoreAsync();
            EditorStatus.Text = "已撤销最近一次内容修改。";
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private PdfPageObjectInfo? SelectedObject => _selected;

    /// <summary>
    /// 选中页面上的一个对象（或传 null 取消选择）。
    /// 文字对象会把原文和字号回填到右侧面板，方便直接改。
    /// </summary>
    private void SelectObject(PdfPageObjectInfo? item)
    {
        _selected = item;
        if (item is not null)
        {
            if (item.Kind == PdfObjectKind.Text)
            {
                ContentTextBox.Text = item.Text;
                FontSizeInput.Text = item.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
            }
            DrawSelection(item.PreviewBounds);
        }
        else
        {
            SelectionLayer.Children.Clear();
        }
        UpdateSelectionHint();
        UpdateButtons();
    }

    /// <summary>右侧“当前选中”说明：说清选到了什么、能不能改、有什么限制。</summary>
    private void UpdateSelectionHint()
    {
        if (ObjectHint is null) return;
        if (SelectedObject is not { } item)
        {
            ObjectHint.Text = "尚未选择对象。在左侧页面上单击文字、图片或图形即可选中。";
            return;
        }
        ObjectHint.Text = item.Limitation ?? (item.Kind == PdfObjectKind.Text
            ? "已选中文字。PDF 文字可能按词或字拆分；替换保留原起点，较长文字可能与相邻内容重叠。"
            : $"已选中：{ObjectRow.KindLabel(item.Kind)}。");
    }

    private void UpdateButtons()
    {
        if (ReplaceTextButton is null) return;
        ReplaceTextButton.IsEnabled = !_busy && SelectedObject is { CanEdit: true, Kind: PdfObjectKind.Text };
        ReplaceImageButton.IsEnabled = !_busy && SelectedObject is { CanEdit: true, Kind: PdfObjectKind.Image };
        DeleteObjectButton.IsEnabled = !_busy && SelectedObject is { CanEdit: true };
        EditorUndo.IsEnabled = !_busy && _undo.Count > 0;
    }

    private PdfEditRequest TextRequest(PdfEditKind kind)
    {
        if (!double.TryParse(FontSizeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size) || size < 1 || size > 1000)
            throw new PdfToolException("字号请输入 1–1000 之间的数字。");
        if (string.IsNullOrWhiteSpace(ContentTextBox.Text)) throw new PdfToolException("请先输入文字内容。");
        return new PdfEditRequest { PageNumber = _pageNumber, Kind = kind, ObjectIndex = SelectedObject?.Index ?? -1, Text = ContentTextBox.Text, FontSize = size, Font = (PdfTextFont)FontChoice.SelectedIndex, Color = CurrentColor() };
    }

    private PdfColor? CurrentColor() => ColorChoice.SelectedIndex switch
    {
        1 => new PdfColor(0, 0, 0), 2 => new PdfColor(200, 45, 35),
        3 => new PdfColor(35, 90, 200), 4 => new PdfColor(24, 106, 87), _ => null
    };

    private async void ReplaceText_Click(object sender, RoutedEventArgs e)
    {
        try { await ApplyEditAsync(TextRequest(PdfEditKind.ReplaceText)); }
        catch (Exception error) { ShowError(error); }
    }

    private void InsertText_Click(object sender, RoutedEventArgs e)
    {
        try { _ = TextRequest(PdfEditKind.InsertText); SetMode(EditMode.InsertText); }
        catch (Exception error) { ShowError(error); }
    }

    private async void DeleteObject_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedObject is { } item) await ApplyEditAsync(new PdfEditRequest { PageNumber = _pageNumber, Kind = PdfEditKind.DeleteObject, ObjectIndex = item.Index });
    }

    private PdfBgraImage? ChooseImage()
    {
        var dialog = new OpenFileDialog { Filter = "本地图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", Title = "选择本地图片", AddToRecent = false, DereferenceLinks = false, CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? LocalImageCodec.LoadSingle(dialog.FileName) : null;
    }

    private async void ReplaceImage_Click(object sender, RoutedEventArgs e)
    {
        PdfBgraImage? image = null;
        try
        {
            image = ChooseImage();
            if (image is not null && SelectedObject is { } item)
                await ApplyEditAsync(new PdfEditRequest { PageNumber = _pageNumber, Kind = PdfEditKind.ReplaceImage, ObjectIndex = item.Index, Image = image });
        }
        catch (Exception error) { ShowError(error); }
        finally { if (image is not null) CryptographicOperations.ZeroMemory(image.Bgra); }
    }

    private void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var image = ChooseImage();
            if (image is null) return;
            ClearPendingImage();
            _pendingImage = image;
            SetMode(EditMode.InsertImage);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void Highlight_Click(object sender, RoutedEventArgs e) => SetMode(EditMode.Highlight);
    private void Rectangle_Click(object sender, RoutedEventArgs e) => SetMode(EditMode.Rectangle);
    private void SelectMode_Click(object sender, RoutedEventArgs e) => ToolSelect.IsChecked = true;

    // 顶部工具组：每个单选按钮对应一种编辑模式，切换时同步提示文字和光标。
    // XAML 里“选择”默认勾选，解析阶段就会触发一次 Checked，此时控件树尚未建好，需要跳过。
    private void ToolSelect_Checked(object sender, RoutedEventArgs e) => SetModeIfReady(EditMode.Select);
    private void ToolText_Checked(object sender, RoutedEventArgs e) => SetModeIfReady(EditMode.InsertText);
    private void ToolImage_Checked(object sender, RoutedEventArgs e) => SetModeIfReady(EditMode.InsertImage);
    private void ToolHighlight_Checked(object sender, RoutedEventArgs e) => SetModeIfReady(EditMode.Highlight);
    private void ToolRect_Checked(object sender, RoutedEventArgs e) => SetModeIfReady(EditMode.Rectangle);

    private void SetModeIfReady(EditMode mode)
    {
        if (SelectionLayer is null || ModeHint is null) return;
        SetMode(mode);
    }
    private void CancelEdit_Click(object sender, RoutedEventArgs e) => Close();
    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_dirty) EditedContent = _bytes.ToArray();
        _accepted = true;
        DialogResult = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        EditorArea.IsEnabled = !busy;
        SaveEdit.IsEnabled = !busy;
        CancelEdit.IsEnabled = !busy;
        if (busy) EditorStatus.Text = "正在本地处理，请稍候…";
        UpdateButtons();
    }

    private void ShowError(Exception error) => EditorStatus.Text = error is PdfToolException ? error.Message : "无法完成此操作，当前修改已保留，请检查输入或换用较小的图片。";
    private void ClearPendingImage() { if (_pendingImage is not null) CryptographicOperations.ZeroMemory(_pendingImage.Bgra); _pendingImage = null; }

    private void Editor_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (_dirty && !_accepted && MessageBox.Show(this, "放弃本次内容修改？工作区和原文件将保持不变。", "关闭内容编辑", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
    }

    private void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        _document.Dispose();
        CryptographicOperations.ZeroMemory(_bytes);
        foreach (var bytes in _undo) CryptographicOperations.ZeroMemory(bytes);
        _undo.Clear();
        ClearPendingImage();
        ContentTextBox.Clear();
        ContentTextBox.IsUndoEnabled = false;
        _selected = null;
        _objects = null;
        PageImage.Source = null;
    }

    /// <summary>对象类型的中文名，只在提示文字里用。</summary>
    private static class ObjectRow
    {
        public static string KindLabel(PdfObjectKind kind) => kind switch { PdfObjectKind.Text => "文字", PdfObjectKind.Image => "图片", PdfObjectKind.Path => "图形", PdfObjectKind.Form => "组合图形", _ => "其他" };
    }
}
