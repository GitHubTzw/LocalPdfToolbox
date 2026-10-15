using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Desktop;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 图片体积压缩：重新编码为更小的 JPEG 或 PNG，可选按比例缩小分辨率。
/// PNG 走本机托管编码器（自适应滤波 + 最高压缩级别，颜色少时自动转索引色），
/// JPEG 按质量重编码并在需要时逐档降质，直到确实比原文件小为止。
/// 解码和编码都使用本机组件，不联网、不写入原文件。
/// </summary>
internal sealed class ImageSizeReducer : IFileSizeReducer
{
    /// <summary>JPEG 重编码时依次尝试的质量档；先试目标质量，不够小再往下走。</summary>
    private static readonly int[] JpegFallbacks = [90, 82, 75, 68, 60, 52, 45, 38, 30];

    public CompressFileKind Kind => CompressFileKind.Image;

    public CompressResult Reduce(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var original = new FileInfo(source).Length;
        var bytes = LocalFileStore.Read(source);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在重新编码图片…");
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = LocalImageCodec.Decode(stream);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            var multipleFrames = decoder.Frames.Count > 1;

            // 多帧 TIFF 只能存回 TIFF，走原来的整帧重打包路径。
            if (extension is ".tif" or ".tiff" && multipleFrames)
                return ReduceTiff(source, outputPath, decoder, options, original, progress, cancellationToken);

            var frame = decoder.Frames[0];
            LocalImageEncoding.RequireReasonableSize(frame);
            var scaled = LocalImageEncoding.Scale(frame, options.ScalePercent);
            var (bgra, width, height, hasAlpha) = ReadPixels(scaled);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wantsJpeg = extension is ".jpg" or ".jpeg";
                var encoded = wantsJpeg
                    ? EncodeJpeg(bgra, width, height, options.Quality, original, cancellationToken)
                    : EncodePng(bgra, width, height, hasAlpha, options.Quality, original, cancellationToken);
                var targetExtension = wantsJpeg ? ".jpg" : ".png";
                var action = $"图片已重新编码为 {targetExtension.TrimStart('.').ToUpperInvariant()}";

                if (encoded.Length >= original)
                    return new CompressResult("", original, original,
                        $"{action}：重新编码后是 {FileSizeFormatter.Format(encoded.Length)}，没有比原文件更小，已放弃写入，原文件保持不变。")
                    { WroteOutput = false };

                var destination = LocalPathGuard.NewFile(ReplaceExtension(outputPath, targetExtension));
                LocalFileStore.WriteNew(destination, encoded, cancellationToken);
                var size = new FileInfo(destination).Length;
                return new CompressResult(destination, original, size,
                    FileSizeFormatter.DescribeChange(original, size, action));
            }
            finally { CryptographicOperations.ZeroMemory(bgra); }
        }
        catch (NotSupportedException error) { throw new PdfToolException("这个图片格式无法在本机重新编码。", error); }
        catch (IOException error) { throw new PdfToolException("压缩失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>多帧 TIFF：逐帧缩放后按 ZIP 压缩重新打包，保留全部帧。</summary>
    private static CompressResult ReduceTiff(string source, string outputPath, BitmapDecoder decoder,
        CompressOptions options, long original, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        for (var index = 0; index < decoder.Frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = decoder.Frames[index];
            LocalImageEncoding.RequireReasonableSize(frame);
            encoder.Frames.Add(BitmapFrame.Create(LocalImageEncoding.Scale(frame, options.ScalePercent)));
        }
        progress?.Report("正在重新打包多帧 TIFF…");
        var encoded = LocalImageEncoding.Save(encoder);
        try
        {
            if (encoded.Length >= original)
                return new CompressResult("", original, original,
                    $"多帧 TIFF 已重新打包：结果是 {FileSizeFormatter.Format(encoded.Length)}，没有比原文件更小，已放弃写入，原文件保持不变。")
                { WroteOutput = false };
            var destination = LocalPathGuard.NewFile(ReplaceExtension(outputPath, ".tif"));
            LocalFileStore.WriteNew(destination, encoded, cancellationToken);
            var size = new FileInfo(destination).Length;
            return new CompressResult(destination, original, size,
                FileSizeFormatter.DescribeChange(original, size, $"多帧 TIFF 已重新打包（保留 {decoder.Frames.Count} 帧）"));
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    /// <summary>JPEG：从目标质量开始逐档降质，取第一个确实比原文件小的结果。</summary>
    private static byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int quality, long original,
        CancellationToken cancellationToken)
    {
        var flattened = LocalImageEncoding.FlattenPixels(bgra, width, height);
        try
        {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, flattened, width * 4);
            source.Freeze();
            var frame = BitmapFrame.Create(source);
            byte[]? best = null;
            var start = Math.Clamp(quality, 1, 100);
            foreach (var level in Candidates(start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encoder = new JpegBitmapEncoder { QualityLevel = level };
                encoder.Frames.Add(frame);
                var encoded = LocalImageEncoding.Save(encoder);
                if (best is not null && encoded.Length >= best.Length)
                {
                    CryptographicOperations.ZeroMemory(encoded);
                    continue;
                }
                if (best is not null) CryptographicOperations.ZeroMemory(best);
                best = encoded;
                // 已经明显小于原文件就不必继续降质，避免画质无谓损失。
                if (best.Length < original * 0.8) break;
            }
            return best!;
        }
        finally { CryptographicOperations.ZeroMemory(flattened); }

        static IEnumerable<int> Candidates(int start)
        {
            yield return start;
            foreach (var level in JpegFallbacks)
                if (level < start) yield return level;
        }
    }

    /// <summary>
    /// PNG：同时试真彩（自适应滤波）和索引色（中位切分量化），取更小的那个。
    /// 质量越低允许的颜色越少；质量 100 时只做无损重编码。
    /// </summary>
    private static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height, bool hasAlpha, int quality,
        long original, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var truecolor = PngWriter.EncodeTruecolor(bgra, width, height, hasAlpha);
        var best = truecolor;

        var colors = quality switch
        {
            >= 95 => 0,
            >= 85 => 128,
            >= 70 => 96,
            >= 55 => 64,
            >= 40 => 32,
            _ => 16
        };
        if (colors > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quantized = PngQuantizer.Quantize(bgra, width, height, colors, hasAlpha);
            // 颜色少时用更低的位深，每像素只占 1 / 2 / 4 位，体积还能再降一截。
            var bitDepth = quantized.ColorCount <= 2 ? 1 : quantized.ColorCount <= 4 ? 2 : quantized.ColorCount <= 16 ? 4 : 8;
            var indexed = PngWriter.EncodeIndexed(quantized.Indices, width, height, bitDepth, quantized.PaletteRgb,
                hasAlpha ? BuildAlpha(quantized) : null);
            if (indexed.Length < best.Length)
            {
                CryptographicOperations.ZeroMemory(best);
                best = indexed;
            }
            else
            {
                CryptographicOperations.ZeroMemory(indexed);
            }
        }
        return best;

        static byte[] BuildAlpha(PngQuantizer.Result result)
        {
            var alpha = new byte[result.ColorCount];
            for (var index = 0; index < alpha.Length; index++) alpha[index] = 255;
            return alpha;
        }
    }

    /// <summary>把 BitmapSource 读成 BGRA 字节，并判断是否真的有透明像素。</summary>
    private static (byte[] Pixels, int Width, int Height, bool HasAlpha) ReadPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        var hasAlpha = false;
        for (var index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 255) { hasAlpha = true; break; }
        return (pixels, width, height, hasAlpha);
    }

    private static string ReplaceExtension(string path, string extension)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path) + extension;
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }
}
