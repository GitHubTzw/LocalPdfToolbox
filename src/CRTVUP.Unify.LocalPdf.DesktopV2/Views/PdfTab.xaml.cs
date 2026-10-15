using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Desktop;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.DesktopV2.Views;

/// <summary>
/// PDF 工作台页签：缩略图列表、拖拽排序、行内操作和双击直接编辑内容。
/// 会话、路径策略和导出规则与 V1 完全一致，只重做交互方式。
/// </summary>
public partial class PdfTab : UserControl
{
    private readonly IPdfWorkspace _workspace = new PdfWorkspace();
    private readonly PdfPreviewRenderer _renderer = new();
    private readonly IPdfExtrasTool _extras = new PdfExtrasTool();
    private readonly ThumbnailQueue _thumbnails;
    private readonly List<PageItemV2[]> _undo = [];
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closing;
    private bool _suppressSelectionPreview;
    private int _previewVersion;
    private string? _initialDirectory;
    private PageItemV2? _previewPage;
    private Point _dragOrigin;
    private PageItemV2? _dragItem;
    private bool _dragFromAction;

    public ObservableCollection<PageItemV2> Pages { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public bool HasWork => Sources.Count > 0;
    public bool IsBusy => _busy;

    public PdfTab()
    {
        InitializeComponent();
        DataContext = this;
        _thumbnails = new ThumbnailQueue(_renderer, Dispatcher);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy || e.OriginalSource is TextBox) return;
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SelectAll_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    // ---------------------------------------------------------------- 导入

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF 文档 (*.pdf)|*.pdf",
            Multiselect = true,
            Title = "添加本地 PDF",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ImportFilesAsync(dialog.FileNames);
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
                foreach (var page in source.Pages) Pages.Add(new PageItemV2(source.Id, source.FileName, page.PageNumber));
                _initialDirectory ??= Path.GetDirectoryName(Path.GetFullPath(path));
                imported++;
            }
            catch (Exception error)
            {
                if (source is not null) { _renderer.Remove(source.Id); _workspace.Remove(source.Id); }
                // 错误只显示在本次窗口中，不写文件路径、异常堆栈或文档内容到日志。
                failures.Add(error is PdfToolException ? error.Message : "有文件无法读取或预览，请确认它是有效的普通 PDF。");
            }
        }
        _undo.Clear();
        SetBusy(false, imported > 0 ? $"已添加 {imported} 个文档，共 {Pages.Count} 页。" : "未添加文档。");
        RefreshPages();
        if (Pages.Count > 0) PageList.SelectedItem = Pages[0];
        if (failures.Count > 0)
            MessageBox.Show(Window.GetWindow(this), string.Join("\n\n", failures.Distinct().Take(5)), "部分文件未导入",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            foreach (var page in source.Pages) Pages.Add(new PageItemV2(source.Id, source.FileName, page.PageNumber));
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

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Tab_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportFilesAsync(files);
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var edits = Pages.Select(page => page.ToEdit()).ToArray();
        var dialog = new ConversionWindow(edits.Length, token => _workspace.ExportToMemory(edits, token)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.ImportedPdf is not { } bytes) return;
        try { await ImportGeneratedPdfAsync(dialog.ImportedName, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (Sources.Count > 0 && MessageBox.Show(owner, "清空本次导入及页面调整？原文件不会改变。", "清空工作区",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        if (_previewPage?.SourceId == id) _previewVersion++;
        foreach (var page in Pages.Where(page => page.SourceId == id).ToArray()) Pages.Remove(page);
        var source = Sources.FirstOrDefault(item => item.Id == id);
        if (source is not null) Sources.Remove(source);
        _thumbnails.InvalidateSource(id);
        _renderer.Remove(id);
        _workspace.Remove(id);
        _undo.Clear();
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = "已移除此文档。原文件未修改。";
    }

    // ---------------------------------------------------------------- 选择

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItems.Count == Pages.Count) PageList.UnselectAll();
        else PageList.SelectAll();
        UpdateSummary();
    }

    private void SelectRange_Click(object sender, RoutedEventArgs e) => SelectRange();

    private void PageRange_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SelectRange();
        e.Handled = true;
    }

    private void SelectRange()
    {
        try
        {
            var positions = PageRange.Parse(PageRangeBox.Text, Pages.Count);
            _suppressSelectionPreview = true;
            try
            {
                PageList.UnselectAll();
                foreach (var position in positions) PageList.SelectedItems.Add(Pages[position - 1]);
            }
            finally { _suppressSelectionPreview = false; }
            if (positions.Count > 0)
            {
                _ = ShowPreviewAsync(Pages[positions[0] - 1]);
                PageList.ScrollIntoView(Pages[positions[0] - 1]);
            }
            UpdateSummary();
            StatusText.Text = $"已选中 {positions.Count} 页；可旋转、删除或仅保留这些页面。";
        }
        catch (ArgumentException error) { StatusText.Text = error.Message; }
    }

    private void KeepSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection()) return;
        var selected = SelectedPages();
        Remember();
        foreach (var page in Pages.Where(page => !selected.Contains(page)).ToArray()) Pages.Remove(page);
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = $"仅保留选中的 {Pages.Count} 页，可单独导出。";
    }

    private HashSet<PageItemV2> SelectedPages() => PageList.SelectedItems.OfType<PageItemV2>().ToHashSet();

    private bool HasSelection()
    {
        if (PageList.SelectedItems.Count > 0) return true;
        StatusText.Text = "请先选择需要操作的页面。";
        return false;
    }

    // ---------------------------------------------------------------- 页面操作

    private void Remember()
    {
        _undo.Add(Pages.Select(page => page.Clone()).ToArray());
        if (_undo.Count > 30) _undo.RemoveAt(0);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _suppressSelectionPreview = true;
        try
        {
            Pages.Clear();
            foreach (var page in snapshot) Pages.Add(page);
        }
        finally { _suppressSelectionPreview = false; }
        RefreshPages();
        if (Pages.Count > 0) PageList.SelectedItem = Pages[0];
        else ClearPreview();
        StatusText.Text = "已撤销上一步页面操作。";
    }

    private async void RotateLeft_Click(object sender, RoutedEventArgs e) => await RotateSelectedAsync(-90);
    private async void RotateRight_Click(object sender, RoutedEventArgs e) => await RotateSelectedAsync(90);

    private async Task RotateSelectedAsync(int delta)
    {
        if (_busy || !HasSelection()) return;
        var selected = SelectedPages();
        Remember();
        foreach (var page in selected)
        {
            page.Rotation += delta;
            page.Thumbnail = null;
            if (page.IsThumbnailVisible) _thumbnails.Request(page);
        }
        RefreshPages();
        StatusText.Text = $"所选 {selected.Count} 页已旋转 {Math.Abs(delta)}°。";
        if (_previewPage is not null) await ShowPreviewAsync(_previewPage);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void DuplicateSelected()
    {
        if (_busy || !HasSelection()) return;
        var selected = SelectedPages();
        var ordered = Pages.Where(selected.Contains).ToList();
        Remember();
        foreach (var page in ordered)
        {
            var index = Pages.IndexOf(page);
            if (index >= 0) Pages.Insert(index + 1, page.Clone());
        }
        RefreshPages();
        StatusText.Text = $"已复制 {ordered.Count} 页，副本紧跟原页。";
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_busy || !HasSelection()) return;
        var selected = SelectedPages();
        Remember();
        foreach (var page in Pages.Where(selected.Contains).ToArray()) Pages.Remove(page);
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = $"已从输出列表移除 {selected.Count} 页。原文件未修改。";
    }

    private void RowMoveUp_Click(object sender, RoutedEventArgs e) => MoveRow(sender, -1);
    private void RowMoveDown_Click(object sender, RoutedEventArgs e) => MoveRow(sender, 1);

    private void MoveRow(object sender, int direction)
    {
        if (_busy || sender is not Button { Tag: PageItemV2 page }) return;
        var index = Pages.IndexOf(page);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Pages.Count) return;
        Remember();
        Pages.Move(index, target);
        RefreshPages();
        PageList.SelectedItem = page;
        StatusText.Text = "已调整输出页序；可以撤销。";
    }

    private async void RowRotateLeft_Click(object sender, RoutedEventArgs e) => await RotateRowAsync(sender, -90);
    private async void RowRotateRight_Click(object sender, RoutedEventArgs e) => await RotateRowAsync(sender, 90);

    private async Task RotateRowAsync(object sender, int delta)
    {
        if (_busy || sender is not Button { Tag: PageItemV2 page }) return;
        Remember();
        page.Rotation += delta;
        page.Thumbnail = null;
        if (page.IsThumbnailVisible) _thumbnails.Request(page);
        RefreshPages();
        StatusText.Text = $"此页已旋转 {Math.Abs(delta)}°。";
        if (ReferenceEquals(_previewPage, page)) await ShowPreviewAsync(page);
    }

    private void RowDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: PageItemV2 page }) return;
        var index = Pages.IndexOf(page);
        if (index < 0) return;
        Remember();
        Pages.Insert(index + 1, page.Clone());
        RefreshPages();
        StatusText.Text = "已复制此页，副本紧跟原页。";
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: PageItemV2 page }) return;
        Remember();
        Pages.Remove(page);
        RefreshPages();
        ResetPreviewIfRemoved();
        StatusText.Text = "已从输出列表移除此页。原文件未修改。";
    }

    // ---------------------------------------------------------------- 拖拽排序

    private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        var element = e.OriginalSource as DependencyObject;
        _dragFromAction = FindAncestor<Button>(element) is not null;
        if (_dragFromAction || _busy) return;
        _dragOrigin = e.GetPosition(PageList);
        _dragItem = RowAt(_dragOrigin);
    }

    private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_busy || _dragItem is null || _dragFromAction || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(PageList);
        if (Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(PageList, new DataObject(typeof(PageItemV2), item), DragDropEffects.Move);
    }

    private void PageList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(typeof(PageItemV2)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PageList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || e.Data.GetData(typeof(PageItemV2)) is not PageItemV2 dragged) return;
        var point = e.GetPosition(PageList);
        var target = RowAt(point) ?? Pages.LastOrDefault();
        if (target is null || ReferenceEquals(target, dragged)) return;
        var insertAfter = false;
        if (PageList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container)
        {
            var origin = container.TransformToAncestor(PageList).Transform(new Point(0, 0));
            insertAfter = point.Y > origin.Y + container.ActualHeight / 2;
        }
        var selected = SelectedPages();
        var moving = selected.Contains(dragged) ? Pages.Where(selected.Contains).ToList() : [dragged];
        if (moving.Contains(target)) return;
        MovePages(moving, target, insertAfter);
    }

    private void MovePages(IReadOnlyList<PageItemV2> moving, PageItemV2 target, bool insertAfter)
    {
        Remember();
        var remaining = Pages.Where(page => !moving.Contains(page)).ToList();
        var anchor = Pages.IndexOf(target);
        var before = Pages.Take(anchor).Count(page => !moving.Contains(page));
        var index = insertAfter ? before + 1 : before;
        foreach (var page in moving) remaining.Insert(Math.Min(index++, remaining.Count), page);
        _suppressSelectionPreview = true;
        try
        {
            Pages.Clear();
            foreach (var page in remaining) Pages.Add(page);
            RefreshPages();
            foreach (var page in moving) PageList.SelectedItems.Add(page);
        }
        finally { _suppressSelectionPreview = false; }
        PageList.ScrollIntoView(moving[0]);
        StatusText.Text = $"已移动 {moving.Count} 页；可以撤销。";
    }

    private PageItemV2? RowAt(Point point)
    {
        if (PageList.InputHitTest(point) is not DependencyObject element) return null;
        return ItemsControl.ContainerFromElement(PageList, element) is ListBoxItem row ? row.DataContext as PageItemV2 : null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void PageRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageItemV2 page }) _thumbnails.Request(page);
    }

    private async void PageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _closing || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        var page = RowAt(e.GetPosition(PageList));
        if (page is null) return;
        e.Handled = true;
        await EditContentAsync(page);
    }

    private async void EditContent_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || PageList.SelectedItems.Count != 1) { StatusText.Text = "请只选中一页，再点击“编辑内容”。"; return; }
        if (PageList.SelectedItems[0] is PageItemV2 page) await EditContentAsync(page);
    }

    private async Task EditContentAsync(PageItemV2 page)
    {
        if (_busy || _closing || !Pages.Contains(page)) return;
        _previewVersion++;
        try
        {
            var editor = new ContentEditorWindow(_workspace.GetContent(page.SourceId), page.PageNumber, page.Rotation)
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true || editor.EditedContent is not { } bytes) return;
            SetBusy(true, "正在把内容修改保存到工作区…");
            try
            {
                var updated = await Task.Run(() => _workspace.ReplaceContent(page.SourceId, bytes));
                await Task.Run(() => _renderer.Add(updated.Id, _workspace.GetContent(updated.Id)));
                _thumbnails.InvalidateSource(updated.Id);
                var source = Sources.First(item => item.Id == updated.Id);
                Sources[Sources.IndexOf(source)] = new SourceItem(updated.Id, updated.FileName, updated.Pages.Count, updated.ByteCount);
                foreach (var item in Pages.Where(item => item.SourceId == updated.Id))
                {
                    item.Thumbnail = null;
                    if (item.IsThumbnailVisible) _thumbnails.Request(item);
                }
                StatusText.Text = "内容修改已保存到工作区。点击“导出新 PDF”保存新文件。";
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error)
        {
            StatusText.Text = error is PdfToolException ? error.Message : "无法完成此页面的内容编辑，请重新打开页面检查。";
        }
        finally
        {
            if (_busy) SetBusy(false, StatusText.Text);
            if (!_closing && Pages.Contains(page)) await ShowPreviewAsync(page);
        }
    }

    private void PageRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageItemV2 page }) _thumbnails.Release(page);
    }

    // ---------------------------------------------------------------- 预览

    private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSummary();
        if (_suppressSelectionPreview) return;
        var page = e.AddedItems.OfType<PageItemV2>().LastOrDefault() ?? PageList.SelectedItems.OfType<PageItemV2>().FirstOrDefault();
        if (page is not null) await ShowPreviewAsync(page);
        else if (!_busy) ClearPreview();
    }

    private async Task ShowPreviewAsync(PageItemV2 page)
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
            var image = await Task.Run(() => _renderer.Render(page.SourceId, page.PageNumber, page.Rotation, 620, 880));
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
                Owner = Window.GetWindow(this),
                Title = $"页面预览 · {page.FileName} · 原第 {page.PageNumber} 页",
                Width = 950,
                Height = 820,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(235, 241, 237)),
                Content = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(20) }
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

    // ---------------------------------------------------------------- 导出

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Pages.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "PDF 文档 (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = Sources.Count == 1 ? "整理后的文档.pdf" : "合并后的文档.pdf",
            Title = "另存为新的本地 PDF（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false,
            InitialDirectory = _initialDirectory ?? ""
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var pages = Pages.Select(page => page.ToEdit()).ToArray();
        SetBusy(true, $"正在本地生成 {pages.Length} 页 PDF…");
        try
        {
            var result = await Task.Run(() => _workspace.Export(pages, dialog.FileName, _operation!.Token));
            StatusText.Text = $"导出完成：{result.PageCount} 页，{result.ByteCount / 1024d / 1024d:0.##} MB。";
            MessageBox.Show(Window.GetWindow(this), $"已保存 {result.PageCount} 页 PDF。\n\n{dialog.FileName}\n\n原文件未修改，未添加水印。",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导出，原文件未修改。"; }
        catch (PdfToolException error)
        {
            StatusText.Text = "未导出。";
            MessageBox.Show(Window.GetWindow(this), error.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception)
        {
            StatusText.Text = "导出失败。";
            MessageBox.Show(Window.GetWindow(this), "请确认保存位置是可写的本地文件夹，且文件名尚未被占用。", "无法导出",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    // ---------------------------------------------------------------- 状态

    // ---------------------------------------------------------------- PDF 工具

    /// <summary>当前操作的文档：优先用列表里选中的那个，只有一个文档时直接用它。</summary>
    private SourceItem? SelectedSource() =>
        SourceList.SelectedItem as SourceItem ?? (Sources.Count == 1 ? Sources[0] : null);

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var source = SelectedSource();
        if (source is null) { StatusText.Text = "请先在左侧列表里选择一个文档。"; return; }
        try
        {
            var info = _extras.Inspect(_workspace.GetContent(source.Id));
            var builder = new StringBuilder();
            builder.AppendLine($"文件：{source.FileName}");
            builder.AppendLine(info.Summary);
            builder.AppendLine();
            builder.AppendLine("每页尺寸（点）：");
            foreach (var page in info.Pages)
                builder.AppendLine($"  第 {page.PageNumber} 页：{page.Width:0.#} × {page.Height:0.#}");
            ShowText("文档信息", builder.ToString());
            StatusText.Text = info.Summary;
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "无法读取这份文档的信息。"; }
    }

    private async void ExtractText_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var source = SelectedSource();
        if (source is null) { StatusText.Text = "请先在左侧列表里选择一个文档。"; return; }
        var dialog = new SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(source.FileName) + "-文字.txt",
            Title = "把文字层另存为新的本地文本文件（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SetBusy(true, "正在本机提取文字层…");
        try
        {
            var content = _workspace.GetContent(source.Id);
            var text = await Task.Run(() => _extras.ExtractText(content, _operation!.Token));
            LocalTextFile.WriteNew(dialog.FileName, text);
            StatusText.Text = $"已提取文字并保存为新文件（{text.Length} 字符）；源文件未修改。";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消提取，未生成文件。"; }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "提取文字失败，请确认保存位置是可写的本地文件夹。"; }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var source = SelectedSource();
        if (source is null) { StatusText.Text = "请先在左侧列表里选择一个文档。"; return; }
        var request = ShowSplitDialog();
        if (request is null) return;
        var (mode, value, folder) = request.Value;
        SetBusy(true, "正在本机拆分…");
        try
        {
            var content = _workspace.GetContent(source.Id);
            var name = source.FileName;
            var token = _operation!.Token;
            var outputs = await Task.Run(() => mode == 0
                ? _extras.SplitEvery(content, name, folder, int.Parse(value, CultureInfo.InvariantCulture), token)
                : _extras.SplitByGroups(content, name, folder, value, token), token);
            StatusText.Text = $"已拆分为 {outputs.Count} 个新文件，保存在所选文件夹；源文件未修改。";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消拆分，未生成文件。"; }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "拆分失败，请确认输出文件夹可写、磁盘空间足够。"; }
        finally { SetBusy(false, StatusText.Text); }
    }

    /// <summary>拆分参数对话框：选拆分方式、填参数、选输出文件夹。取消返回 null。</summary>
    private (int Mode, string Value, string Folder)? ShowSplitDialog()
    {
        var window = new Window
        {
            Owner = Window.GetWindow(this),
            Title = "拆分 PDF",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("CanvasBrush")
        };
        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var index = 0; index < 4; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var mode = new ComboBox { SelectedIndex = 0, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add("每 N 页拆成一个文件");
        mode.Items.Add("按页码范围拆（分号分隔，例如 1-3;4-6;7）");
        var value = new TextBox { Text = "1", Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var folder = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 0, 8, 0) };

        var modeLabel = new TextBlock { Text = "拆分方式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var valueLabel = new TextBlock { Text = "参数", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var folderLabel = new TextBlock { Text = "输出文件夹", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(modeLabel, 0); Grid.SetColumn(modeLabel, 0);
        Grid.SetRow(mode, 0); Grid.SetColumn(mode, 1); Grid.SetColumnSpan(mode, 2);
        Grid.SetRow(valueLabel, 1); Grid.SetColumn(valueLabel, 0); valueLabel.Margin = new Thickness(0, 12, 12, 0);
        Grid.SetRow(value, 1); Grid.SetColumn(value, 1); Grid.SetColumnSpan(value, 2); value.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(folderLabel, 2); Grid.SetColumn(folderLabel, 0); folderLabel.Margin = new Thickness(0, 12, 12, 0);
        Grid.SetRow(folder, 2); Grid.SetColumn(folder, 1); folder.Margin = new Thickness(0, 12, 8, 0);

        var browse = new Button { Content = "浏览…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 12, 0, 0) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择拆分结果的保存文件夹" };
            if (picker.ShowDialog(window) == true) folder.Text = picker.FolderName;
        };
        Grid.SetRow(browse, 2); Grid.SetColumn(browse, 2);

        var confirm = new Button { Content = "开始拆分", Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        Grid.SetRow(actions, 3); Grid.SetColumn(actions, 0); Grid.SetColumnSpan(actions, 3);

        grid.Children.Add(modeLabel); grid.Children.Add(mode);
        grid.Children.Add(valueLabel); grid.Children.Add(value);
        grid.Children.Add(folderLabel); grid.Children.Add(folder); grid.Children.Add(browse);
        grid.Children.Add(actions);
        window.Content = grid;

        (int, string, string)? result = null;
        confirm.Click += (_, _) =>
        {
            if (folder.Text.Length == 0) { StatusText.Text = "请先选择输出文件夹。"; return; }
            result = (mode.SelectedIndex, value.Text.Trim(), folder.Text);
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>用只读窗口显示本地生成的文本；不写盘、不调用外部查看器。</summary>
    private void ShowText(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 12.5,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(0),
            Background = Brushes.White,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        new Window
        {
            Owner = Window.GetWindow(this),
            Title = title,
            Width = 660,
            Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = box
        }.ShowDialog();
    }

    private void RefreshPages()
    {
        for (var index = 0; index < Pages.Count; index++) Pages[index].Position = index + 1;
        EmptyState.Visibility = Pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !_busy && Pages.Count > 0;
        UndoButton.IsEnabled = _undo.Count > 0;
        if (_previewPage is not null) PreviewPosition.Text = $"第 {_previewPage.Position} 页";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        PageSummary.Text = $"{Pages.Count} 页 · 已选 {PageList.SelectedItems.Count} 页";
        EditButton.IsEnabled = !_busy && PageList.SelectedItems.Count == 1;
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

    /// <summary>由主窗口在关闭时调用；释放本地会话和预览句柄。</summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        _operation?.Cancel();
        _thumbnails.Dispose();
        ClearPreview();
        Pages.Clear();
        Sources.Clear();
        _undo.Clear();
        _renderer.Dispose();
        _workspace.Dispose();
        _operation?.Dispose();
        _operation = null;
    }
}
