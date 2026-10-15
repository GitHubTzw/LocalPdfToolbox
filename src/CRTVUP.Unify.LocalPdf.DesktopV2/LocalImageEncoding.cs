using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 本机图片重新编码：缩放、去掉透明通道并按指定质量编码。
/// 只使用 Windows 自带编码器，不带入 EXIF、路径或其他元数据。
/// </summary>
internal static class LocalImageEncoding
{
    /// <summary>单边和总像素上限，避免一次解码占用过多内存。</summary>
    public const int MaxSide = 8192;
    public const long MaxPixels = 32L * 1024 * 1024;

    public static void RequireReasonableSize(BitmapSource source)
    {
        if (source.PixelWidth < 1 || source.PixelHeight < 1
            || source.PixelWidth > MaxSide || source.PixelHeight > MaxSide
            || (long)source.PixelWidth * source.PixelHeight > MaxPixels)
            throw new PdfToolException("图片单边最多 8192 像素，总像素最多 3200 万，请先缩小图片。");
    }

    /// <summary>按百分比缩小；100% 时原样返回。</summary>
    public static BitmapSource Scale(BitmapSource source, int percent)
    {
        var ratio = Math.Clamp(percent, 10, 100) / 100d;
        if (ratio >= 1) return source;
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * ratio));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * ratio));
        var transformed = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        var result = new WriteableBitmap(transformed);
        result.Freeze();
        return result;
    }

    /// <summary>把透明像素合成到白底，JPEG 不支持透明通道。</summary>
    public static BitmapSource Flatten(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var alpha = pixels[index + 3];
                if (alpha == 255) continue;
                for (var channel = 0; channel < 3; channel++)
                    pixels[index + channel] = (byte)((pixels[index + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                pixels[index + 3] = 255;
            }
            var flattened = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            flattened.Freeze();
            return flattened;
        }
        finally { CryptographicOperations.ZeroMemory(pixels); }
    }

    /// <summary>保存已经添加好帧的编码器，返回内存中的字节。</summary>
    /// <summary>把 BGRA 字节里的透明像素合成到白底，返回新的 BGRA 字节（JPEG 不支持透明通道）。</summary>
    public static byte[] FlattenPixels(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgra[..pixels.Length].CopyTo(pixels);
        var opaque = true;
        for (var index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 255) { opaque = false; break; }
        if (opaque) return pixels;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 255) continue;
            for (var channel = 0; channel < 3; channel++)
                pixels[index + channel] = (byte)((pixels[index + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    /// <summary>保存已经添加好帧的编码器，返回内存中的字节。</summary>
    public static byte[] Save(BitmapEncoder encoder)
    {
        using var buffer = new MemoryStream();
        try
        {
            encoder.Save(buffer);
            return buffer.ToArray();
        }
        finally { if (buffer.TryGetBuffer(out var span)) CryptographicOperations.ZeroMemory(span.AsSpan()); }
    }

    /// <summary>按目标扩展名选择编码器和实际输出扩展名；BMP 统一转成更小的 PNG。</summary>
    public static (BitmapEncoder Encoder, string Extension) CreateEncoder(string extension, int quality, bool multipleFrames)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                return (new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) }, ".jpg");
            case ".tif":
            case ".tiff":
                return (new TiffBitmapEncoder { Compression = TiffCompressOption.Zip }, ".tif");
            case ".bmp":
                return (new PngBitmapEncoder(), ".png");
            default:
                if (multipleFrames) throw new PdfToolException("多帧图片只能保存为 TIFF，请改用 TIFF 输出。");
                return (new PngBitmapEncoder(), ".png");
        }
    }
}
