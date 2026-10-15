using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// PDF 图片化压缩：按目标 DPI 把每页渲染成 JPEG，再重建一个只含页面图片的 PDF。
/// 体积通常能明显变小，代价是丢失可搜索文字层，界面必须提前说明。
/// </summary>
internal sealed class RasterPdfReducer : IFileSizeReducer
{
    public const int MaxPages = 500;

    public CompressFileKind Kind => CompressFileKind.Pdf;

    public CompressResult Reduce(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var destination = LocalPathGuard.NewFile(outputPath);
        var original = new FileInfo(source).Length;
        var bytes = LocalFileStore.Read(source);
        var pages = new List<JpegPdfPage>();
        try
        {
            using var document = PdfiumRuntime.Open(bytes);
            if (document.PageCount is < 1 or > MaxPages)
                throw new PdfToolException($"图片化压缩一次最多处理 {MaxPages} 页，请先拆分文档。");
            var dpi = Math.Clamp(options.PdfDpi, 72, 300);
            var quality = Math.Clamp(options.Quality, 1, 100);
            for (var index = 1; index <= document.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在渲染第 {index}/{document.PageCount} 页…");
                var (width, height) = document.GetPageSize(index);
                var maxWidth = (int)Math.Ceiling(width / 72d * dpi);
                var maxHeight = (int)Math.Ceiling(height / 72d * dpi);
                var bitmap = document.Render(index, 0, maxWidth, maxHeight);
                try { pages.Add(new JpegPdfPage(EncodeJpeg(bitmap, quality), width, height)); }
                finally { CryptographicOperations.ZeroMemory(bitmap.Bgra); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在重建 PDF…");
            var output = ImagePdfBuilder.BuildJpeg(pages, cancellationToken);
            try
            {
                LocalFileStore.WriteNew(destination, output, cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(output); }
        }
        finally
        {
            foreach (var page in pages) CryptographicOperations.ZeroMemory(page.Jpeg);
            CryptographicOperations.ZeroMemory(bytes);
        }
        var size = new FileInfo(destination).Length;
        var action = $"PDF 已转为图片版（{Math.Clamp(options.PdfDpi, 72, 300)} DPI，无文字层）";
        return new CompressResult(destination, original, size, FileSizeFormatter.DescribeChange(original, size, action));
    }

    private static byte[] EncodeJpeg(PdfBitmap bitmap, int quality)
    {
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.Bgra, bitmap.Stride);
        var flattened = LocalImageEncoding.Flatten(source);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        return LocalImageEncoding.Save(encoder);
    }
}
