using System.IO;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 文件体积压缩的统一入口：按扩展名分派到 PDF、图片或 Office 的本地压缩实现。
/// 输出文件名由调用方指定，且必须是尚不存在的新文件。
/// </summary>
public sealed class LocalFileCompressor
{
    private readonly PdfSizeReducer _pdf = new();
    private readonly OfficeSizeReducer _office = new();
    private readonly ImageSizeReducer _image = new();
    private readonly RasterPdfReducer _raster = new();

    public static CompressFileKind Detect(string path) => CompressKindDetector.Detect(path);

    public CompressResult Compress(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var kind = Detect(inputPath);
        return kind switch
        {
            // PDF 默认无损重写；勾选图片化后改用渲染重建，体积更小但丢文字层。
            CompressFileKind.Pdf => options.RasterizePdf
                ? _raster.Reduce(inputPath, outputPath, options, progress, cancellationToken)
                : _pdf.Reduce(inputPath, outputPath, options, progress, cancellationToken),
            CompressFileKind.Image => _image.Reduce(inputPath, outputPath, options, progress, cancellationToken),
            CompressFileKind.Office => _office.Reduce(inputPath, outputPath, options, progress, cancellationToken),
            _ => throw new PdfToolException("只支持 PDF、图片（PNG/JPEG/BMP/TIFF）和 Office 文档（DOCX/XLSX/PPTX）。")
        };
    }

    /// <summary>在输出文件夹里取一个尚不存在的新文件名，绝不覆盖已有内容。</summary>
    public static string UniqueOutputPath(string directory, string inputFileName, string suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(inputFileName);
        var extension = Path.GetExtension(inputFileName);
        var candidate = Path.Combine(directory, stem + suffix + extension);
        for (var index = 2; File.Exists(candidate) || Directory.Exists(candidate); index++)
            candidate = Path.Combine(directory, $"{stem}{suffix} ({index}){extension}");
        return candidate;
    }
}
