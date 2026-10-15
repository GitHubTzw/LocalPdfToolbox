using System.ComponentModel;
using System.Runtime.CompilerServices;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public sealed class PageItem : INotifyPropertyChanged
{
    private int _position;
    private int _rotation;
    public string SourceId { get; }
    public string FileName { get; }
    public int PageNumber { get; }
    public string OriginalPageLabel => $"原文档第 {PageNumber} 页";
    public string RotationLabel => Rotation == 0 ? "" : $"↻ {Rotation}°";
    public int Position { get => _position; set { _position = value; Changed(); } }
    public int Rotation
    {
        get => _rotation;
        set { _rotation = ((value % 360) + 360) % 360; Changed(); Changed(nameof(RotationLabel)); }
    }
    public PageItem(string sourceId, string fileName, int pageNumber, int rotation = 0)
    {
        SourceId = sourceId;
        FileName = fileName;
        PageNumber = pageNumber;
        Rotation = rotation;
    }
    public PageEdit ToEdit() => new(SourceId, PageNumber, Rotation);
    public PageItem Clone() => new(SourceId, FileName, PageNumber, Rotation);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SourceItem(string Id, string FileName, int PageCount, long ByteCount)
{
    public string Description => $"{PageCount} 页 · {ByteCount / 1024d / 1024d:0.##} MB";
}
