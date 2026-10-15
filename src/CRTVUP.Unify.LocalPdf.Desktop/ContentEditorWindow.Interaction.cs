using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class ContentEditorWindow
{
    private enum EditMode { Select, InsertText, InsertImage, Highlight, Rectangle }
    private EditMode _mode;
    private Point? _dragStart;

    private void SetMode(EditMode mode)
    {
        _mode = mode;
        _dragStart = null;
        SelectionLayer.ReleaseMouseCapture();
        SelectionLayer.Children.Clear();
        if (mode != EditMode.InsertImage) ClearPendingImage();
        SelectionLayer.Cursor = mode == EditMode.Select ? Cursors.Arrow : Cursors.Cross;
        ModeHint.Text = mode switch
        {
            EditMode.InsertText => "插入文字：在页面中单击文字起点（第一行基线）。",
            EditMode.InsertImage => "插入图片：拖出放置范围，图片保持比例。",
            EditMode.Highlight => "高亮：在页面中拖出矩形范围。",
            EditMode.Rectangle => "画框：在页面中拖出矩形范围。",
            _ => "选择对象：单击文字、图片或图形。"
        };
    }

    private Point SurfacePoint(MouseEventArgs e)
    {
        var point = e.GetPosition(SelectionLayer);
        return new Point(Math.Clamp(point.X, 0, PageSurface.Width), Math.Clamp(point.Y, 0, PageSurface.Height));
    }

    private async void Surface_Down(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _objects is null) return;
        var point = SurfacePoint(e);
        e.Handled = true;
        if (_mode == EditMode.Select)
        {
            var x = point.X / PageSurface.Width;
            var y = point.Y / PageSurface.Height;
            var item = _objects.Objects.Where(item =>
                x >= item.PreviewBounds.Left && x <= item.PreviewBounds.Left + item.PreviewBounds.Width &&
                y >= item.PreviewBounds.Top && y <= item.PreviewBounds.Top + item.PreviewBounds.Height)
                .OrderBy(item => item.PreviewBounds.Width * item.PreviewBounds.Height).ThenByDescending(item => item.Index).FirstOrDefault();
            ObjectList.SelectedItem = ObjectList.Items.Cast<ObjectRow>().FirstOrDefault(row => row.Item == item);
            if (ObjectList.SelectedItem is not null) ObjectList.ScrollIntoView(ObjectList.SelectedItem);
            else SelectionLayer.Children.Clear();
            return;
        }
        if (_mode == EditMode.InsertText)
        {
            try
            {
                var position = _document.DeviceToPdf(_pageNumber, _rotation, (int)PageSurface.Width, (int)PageSurface.Height, point.X, point.Y);
                await ApplyEditAsync(TextRequest(PdfEditKind.InsertText) with { Position = position, ViewRotation = _rotation });
            }
            catch (Exception error) { ShowError(error); }
            return;
        }
        _dragStart = point;
        SelectionLayer.CaptureMouse();
        DrawDrag(point, point);
    }

    private void Surface_Move(object sender, MouseEventArgs e)
    {
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed) DrawDrag(start, SurfacePoint(e));
    }

    private async void Surface_Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start || _busy) return;
        var end = SurfacePoint(e);
        _dragStart = null;
        SelectionLayer.ReleaseMouseCapture();
        if (Math.Abs(start.X - end.X) < 5 || Math.Abs(start.Y - end.Y) < 5) { EditorStatus.Text = "范围太小，请重新拖动。"; return; }
        try
        {
            var left = Math.Min(start.X, end.X);
            var top = Math.Min(start.Y, end.Y);
            var width = Math.Abs(start.X - end.X);
            var height = Math.Abs(start.Y - end.Y);
            if (_mode == EditMode.InsertImage && _pendingImage is { } image)
            {
                var ratio = image.Width / (double)image.Height;
                if (width / height > ratio) width = height * ratio; else height = width / ratio;
            }
            var a = _document.DeviceToPdf(_pageNumber, _rotation, (int)PageSurface.Width, (int)PageSurface.Height, left, top);
            var b = _document.DeviceToPdf(_pageNumber, _rotation, (int)PageSurface.Width, (int)PageSurface.Height, left + width, top + height);
            var bounds = new PdfBounds(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            var kind = _mode switch { EditMode.InsertImage => PdfEditKind.InsertImage, EditMode.Highlight => PdfEditKind.AddHighlight, _ => PdfEditKind.AddRectangle };
            await ApplyEditAsync(new PdfEditRequest { PageNumber = _pageNumber, Kind = kind, Bounds = bounds, Image = _mode == EditMode.InsertImage ? _pendingImage : null, Color = kind == PdfEditKind.AddRectangle ? CurrentColor() : null, ViewRotation = _rotation });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void DrawSelection(PdfNormalizedBounds bounds) => DrawDrag(
        new Point(bounds.Left * PageSurface.Width, bounds.Top * PageSurface.Height),
        new Point((bounds.Left + bounds.Width) * PageSurface.Width, (bounds.Top + bounds.Height) * PageSurface.Height));

    private void DrawDrag(Point first, Point second)
    {
        SelectionLayer.Children.Clear();
        var rectangle = new Rectangle
        {
            Width = Math.Max(1, Math.Abs(first.X - second.X)), Height = Math.Max(1, Math.Abs(first.Y - second.Y)),
            Stroke = new SolidColorBrush(Color.FromRgb(24, 106, 87)), StrokeThickness = Math.Max(2, PageSurface.Width / 500),
            Fill = new SolidColorBrush(Color.FromArgb(24, 24, 106, 87)), IsHitTestVisible = false
        };
        Canvas.SetLeft(rectangle, Math.Min(first.X, second.X));
        Canvas.SetTop(rectangle, Math.Min(first.Y, second.Y));
        SelectionLayer.Children.Add(rectangle);
    }
}
