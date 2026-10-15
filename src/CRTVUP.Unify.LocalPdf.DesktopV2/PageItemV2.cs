using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Desktop;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>页面列表项：带缩略图、旋转状态和拖拽排序所需的标识。</summary>
public sealed class PageItemV2 : INotifyPropertyChanged
{
    private int _position;
    private int _rotation;
    private BitmapSource? _thumbnail;

    public string SourceId { get; }
    public string FileName { get; }
    public int PageNumber { get; }
    public string OriginalPageLabel => $"原第 {PageNumber} 页";
    public string RotationLabel => Rotation == 0 ? "" : $"↻ {Rotation}°";
    public string ThumbnailKey => $"{SourceId}:{PageNumber}:{Rotation}";
    public bool IsThumbnailVisible { get; set; }

    public int Position
    {
        get => _position;
        set { if (_position == value) return; _position = value; Changed(); }
    }

    public int Rotation
    {
        get => _rotation;
        set
        {
            var normalized = ((value % 360) + 360) % 360;
            if (_rotation == normalized) return;
            _rotation = normalized;
            Changed();
            Changed(nameof(RotationLabel));
            Changed(nameof(ThumbnailKey));
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            Changed();
            Changed(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public PageItemV2(string sourceId, string fileName, int pageNumber, int rotation = 0)
    {
        SourceId = sourceId;
        FileName = fileName;
        PageNumber = pageNumber;
        Rotation = rotation;
    }

    public PageEdit ToEdit() => new(SourceId, PageNumber, Rotation);

    public PageItemV2 Clone() => new(SourceId, FileName, PageNumber, Rotation) { Position = Position };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
