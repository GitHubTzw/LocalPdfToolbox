namespace CRTVUP.Unify.LocalPdf.Abstractions;

public enum PdfObjectKind { Text, Image, Path, Form, Other }

public enum PdfEditKind
{
    ReplaceText,
    DeleteObject,
    InsertText,
    ReplaceImage,
    InsertImage,
    AddHighlight,
    AddRectangle
}

/// <summary>Auto 为中文选本机黑体，其余文字选本机 Arial；字体嵌入 PDF，不随程序分发。</summary>
public enum PdfTextFont { Auto, SimHei, Arial }

public readonly record struct PdfPoint(double X, double Y);

/// <summary>PDF 原始坐标，原点通常在页面左下；单位为 PDF 点。</summary>
public readonly record struct PdfBounds(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>与 Render 相同旋转后的预览坐标；原点在左上，宽高按 0..1 归一化。</summary>
public readonly record struct PdfNormalizedBounds(double Left, double Top, double Width, double Height);

public readonly record struct PdfColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

/// <summary>从上到下的非预乘 BGRA32 像素。调用方拥有数组，使用完毕可主动清零。</summary>
public sealed record PdfBitmap(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>已在本机解码的非预乘 BGRA32 图片；Stride 为正，不能传入压缩 PNG/JPEG 字节。</summary>
public sealed record PdfBgraImage(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>Index 为页面顶层对象的原始索引。任何修改后均须重新枚举，不能复用旧索引。</summary>
public sealed record PdfPageObjectInfo(
    int Index,
    PdfObjectKind Kind,
    string Text,
    double FontSize,
    PdfBounds Bounds,
    PdfNormalizedBounds PreviewBounds,
    bool CanEdit,
    string? Limitation);

public sealed record PdfPageObjectCollection(
    int PageNumber,
    double Width,
    double Height,
    IReadOnlyList<PdfPageObjectInfo> Objects,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 页码从 1 开始。替换/删除使用 ObjectIndex；插入文字使用 Position（第一行基线）；
/// 插入图片和矩形标记使用 Bounds。替换默认保留原矩阵、字号和颜色，FontSize/Color 可显式覆盖。
/// 高亮、边框为可再次选择和删除的页面矢量标记，不会盖白块模拟删除。
/// </summary>
public sealed record PdfEditRequest
{
    public required int PageNumber { get; init; }
    public required PdfEditKind Kind { get; init; }
    public int ObjectIndex { get; init; } = -1;
    public string? Text { get; init; }
    public double? FontSize { get; init; }
    public PdfTextFont Font { get; init; } = PdfTextFont.Auto;
    public PdfPoint? Position { get; init; }
    public PdfBounds? Bounds { get; init; }
    public PdfBgraImage? Image { get; init; }
    public PdfColor? Color { get; init; }
    public double StrokeWidth { get; init; } = 1.5;
    /// <summary>插入文字/图片时使用的预览额外旋转；插入内容保持在预览中正向显示。</summary>
    public int ViewRotation { get; init; }
}

public interface IPdfEditableDocument : IDisposable
{
    int PageCount { get; }
    (double Width, double Height) GetPageSize(int pageNumber, int rotation = 0);
    PdfBitmap Render(int pageNumber, int rotation = 0, int maxWidth = 1400, int maxHeight = 1800);
    PdfPageObjectCollection EnumerateObjects(int pageNumber, int rotation = 0);
    string ExtractText(int pageNumber);
    PdfPoint DeviceToPdf(int pageNumber, int rotation, int pixelWidth, int pixelHeight, double x, double y);
    /// <summary>事务式修改并生成完整新 PDF；失败时此实例的内容不变，返回数组由调用方拥有。</summary>
    byte[] ApplyEdit(PdfEditRequest request);
    /// <summary>非增量写出到内存；返回数组由调用方拥有。</summary>
    byte[] SaveAsCopy();
}
