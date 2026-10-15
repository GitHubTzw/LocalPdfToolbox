using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.Desktop;

/// <summary>仅从内存解码静态图片，重新编码时不带入 EXIF、路径或其他元数据。</summary>
public static class LocalImageCodec
{
    public static PdfBgraImage LoadSingle(string path)
    {
        var bytes = LocalFileStore.Read(path);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = Decode(stream);
            if (decoder.Frames.Count != 1) throw new PdfToolException("替换或插入图片时请选择单帧图片；多页 TIFF 可通过“图片转 PDF”导入。");
            return Pixels(decoder.Frames[0]);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static BitmapDecoder Decode(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder or TiffBitmapDecoder))
            throw new PdfToolException("只支持 PNG、JPEG、BMP 和 TIFF 静态图片。");
        if (decoder.Frames.Count is < 1 or > 200) throw new PdfToolException("单个图片文件最多包含 200 帧。");
        return decoder;
    }

    public static PdfBgraImage Pixels(BitmapSource source)
    {
        if (source.PixelWidth is < 1 or > 8192 || source.PixelHeight is < 1 or > 8192 || (long)source.PixelWidth * source.PixelHeight > 32 * 1024 * 1024)
            throw new PdfToolException("图片单边最多 8192 像素，总像素最多 3200 万，请先缩小图片。");
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        try
        {
            bitmap.CopyPixels(pixels, stride, 0);
            return new PdfBgraImage(pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride);
        }
        catch { CryptographicOperations.ZeroMemory(pixels); throw; }
    }

    public static byte[] Encode(PdfBgraImage pixels, bool jpeg = false)
    {
        // 新建 BitmapSource 而非直接保存 decoder.Frames，避免继承图片属性。
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, pixels.Stride);
        BitmapSource source = bitmap;
        if (jpeg)
        {
            var opaque = pixels.Bgra.ToArray();
            try
            {
                for (var row = 0; row < pixels.Height; row++)
                    for (var col = 0; col < pixels.Width; col++)
                    {
                        var i = row * pixels.Stride + col * 4;
                        var alpha = opaque[i + 3];
                        for (var channel = 0; channel < 3; channel++) opaque[i + channel] = (byte)((opaque[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                        opaque[i + 3] = 255;
                    }
                source = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, opaque, pixels.Stride);
            }
            finally { CryptographicOperations.ZeroMemory(opaque); }
        }
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        try
        {
            encoder.Save(output);
            return output.ToArray();
        }
        finally { if (output.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
    }
}
