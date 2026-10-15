using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Desktop;
using CRTVUP.Unify.LocalPdf.Implement;

internal static class ConversionIntegrationChecks
{
    internal static void Run(byte[] pdf, string directory)
    {
        var hash = SHA256.HashData(pdf);
        using var original = PdfiumRuntime.Open(pdf);
        foreach (var format in Enum.GetValues<PdfExportFormat>())
        {
            var output = LocalConversionService.ExportPdf(pdf, format, 96, null, CancellationToken.None);
            try
            {
                if (format == PdfExportFormat.Text)
                    Check(Encoding.UTF8.GetString(output).Contains("PROJECT BRIEF", StringComparison.Ordinal), "TXT 未包含源文字");
                else if (format is PdfExportFormat.WordText or PdfExportFormat.ExcelText)
                {
                    var text = LocalTextImporter.Read(output, format == PdfExportFormat.WordText ? ".docx" : ".xlsx");
                    Check(text.Sections.Any(section => section.Contains("PROJECT BRIEF", StringComparison.Ordinal)), "Office 文字导出未包含源文字");
                }
                else
                {
                    using var memory = new MemoryStream(output);
                    using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
                    var images = zip.Entries.Where(entry => entry.FullName.EndsWith(".png", StringComparison.Ordinal) || entry.FullName.EndsWith(".jpg", StringComparison.Ordinal)).ToArray();
                    Check(images.Length == original.PageCount, "转换结果的图片数量与页数不一致");
                    foreach (var entry in images)
                    {
                        using var input = entry.Open();
                        using var buffer = new MemoryStream();
                        input.CopyTo(buffer);
                        buffer.Position = 0;
                        var frame = BitmapDecoder.Create(buffer, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                        Check(frame.PixelWidth > 100 && frame.PixelHeight > 100, "转换图片损坏或大小错误");
                    }
                }
            }
            finally { CryptographicOperations.ZeroMemory(output); }
        }
        Check(hash.AsSpan().SequenceEqual(SHA256.HashData(pdf)), "转换改写了源字节");

        var pixels = Enumerable.Repeat((byte)180, 32 * 24 * 4).ToArray();
        var bitmap = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        var inputs = new List<string>();
        foreach (var (encoder, suffix) in new (BitmapEncoder, string)[] { (new PngBitmapEncoder(), "png"), (new JpegBitmapEncoder(), "jpg"), (new BmpBitmapEncoder(), "bmp"), (new TiffBitmapEncoder(), "tiff") })
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            if (suffix == "tiff") encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var path = Path.Combine(directory, "synthetic-image." + suffix);
            LocalFileStore.WriteNew(path, stream.ToArray());
            inputs.Add(path);
        }
        var converted = LocalConversionService.ImagesToPdf(inputs, null, CancellationToken.None);
        try
        {
            using var result = PdfiumRuntime.Open(converted);
            Check(result.PageCount == 5, "图片转换遗漏了多页 TIFF");
            Check(Math.Abs(result.GetPageSize(1).Width / result.GetPageSize(1).Height - 4d / 3) < .01, "图片转换改变了宽高比");
            try
            {
                LocalConversionService.ExportPdf(converted, PdfExportFormat.Text, 96, null, CancellationToken.None);
                throw new InvalidOperationException("无文字图片版应拒绝文字转换");
            }
            catch (PdfToolException) { }
        }
        finally { CryptographicOperations.ZeroMemory(converted); CryptographicOperations.ZeroMemory(pixels); }
        Console.WriteLine("通过：七种 PDF 导出回读、源字节不变、四种图片导入、多页 TIFF 和无文字提示。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
