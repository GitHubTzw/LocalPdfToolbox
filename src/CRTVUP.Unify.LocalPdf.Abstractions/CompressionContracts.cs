namespace CRTVUP.Unify.LocalPdf.Abstractions;

// ---------------------------------------------------------------------------
// 文件体积压缩（瘦身）：把本地文件重新编码得更小，而不是打包成 ZIP。
// 全部在本机完成；输出只新建文件，源文件保持不变。
// ---------------------------------------------------------------------------

/// <summary>按扩展名识别的文件类别，只用于选择本机压缩方式。</summary>
public enum CompressFileKind { Pdf, Image, Office, Unknown }

/// <summary>文件体积压缩选项。</summary>
public sealed record CompressOptions
{
    /// <summary>重新编码图片时的 JPEG 质量，1–100；越低体积越小。</summary>
    public int Quality { get; init; } = 70;

    /// <summary>图片缩放百分比，10–100；100 表示保持原分辨率。</summary>
    public int ScalePercent { get; init; } = 100;

    /// <summary>PDF 是否转成图片版：体积最小，但会丢失可搜索文字层。</summary>
    public bool RasterizePdf { get; init; }

    /// <summary>PDF 转图片版时的渲染 DPI，72–300。</summary>
    public int PdfDpi { get; init; } = 120;
}

/// <summary>一次体积压缩的结果；OutputPath 始终是新建文件。</summary>
public sealed record CompressResult(string OutputPath, long OriginalBytes, long CompressedBytes, string Summary)
{
    /// <summary>体积变化百分比；正数表示变小，负数表示反而变大。</summary>
    public double SavedPercent => OriginalBytes <= 0 ? 0 : (OriginalBytes - CompressedBytes) * 100d / OriginalBytes;
}

/// <summary>把一类本地文件的体积压小；实现各自处理，不访问网络。</summary>
public interface IFileSizeReducer
{
    CompressFileKind Kind { get; }

    /// <summary>压缩到新建的本地文件；源文件保持不变，失败不留下半成品。</summary>
    CompressResult Reduce(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>已重新编码的整页 JPEG，用于生成图片版 PDF；尺寸以点为单位。</summary>
public sealed record JpegPdfPage(byte[] Jpeg, double Width, double Height);
