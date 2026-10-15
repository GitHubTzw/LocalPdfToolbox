using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.Desktop;

/// <summary>连接本地 PDF 引擎与 Windows 内置图片编码器；不启动其他程序。</summary>
public static class LocalConversionService
{
    public static ImportedText ReadText(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = LocalFileStore.Read(path);
        try
        {
            var imported = LocalTextImporter.Read(bytes, Path.GetExtension(path));
            cancellationToken.ThrowIfCancellationRequested();
            return imported;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static byte[] ExportText(IReadOnlyList<string> sections, PdfExportFormat format, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sections.Count is < 1 or > 500 || sections.Sum(section => (long)section.Length) > 2_000_000)
            throw new PdfToolException("文字转换上限为 500 个分节、200 万个字符，请减少内容。");
        if (sections.All(string.IsNullOrWhiteSpace)) throw new PdfToolException("请至少保留一段文字再导出。");
        if (format == PdfExportFormat.Text)
        {
            var value = string.Join("\r\n\f\r\n", sections);
            var encoding = new UTF8Encoding(true);
            var preamble = encoding.GetPreamble();
            var bytes = new byte[preamble.Length + encoding.GetByteCount(value)];
            preamble.CopyTo(bytes, 0);
            encoding.GetBytes(value, bytes.AsSpan(preamble.Length));
            if (cancellationToken.IsCancellationRequested) { CryptographicOperations.ZeroMemory(bytes); cancellationToken.ThrowIfCancellationRequested(); }
            return bytes;
        }
        if (format is not (PdfExportFormat.WordText or PdfExportFormat.ExcelText))
            throw new PdfToolException("文字编辑后可另存为 TXT、DOCX 或 XLSX 文字版。");
        var pages = sections.Select((text, index) => new ConversionPage(index + 1, 595, 842, text)).ToArray();
        return new OpenXmlExporter().Export(pages, format, cancellationToken);
    }

    public static byte[] ExportPdf(byte[] pdf, PdfExportFormat format, int dpi, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (dpi is not (96 or 150 or 200)) throw new PdfToolException("图片分辨率请选择 96、150 或 200 DPI。");
        using var document = PdfiumRuntime.Open(pdf);
        if (document.PageCount > 500) throw new PdfToolException("格式转换一次最多 500 页，请先拆分页面。");
        var pages = new List<ConversionPage>();
        var hasImages = format is PdfExportFormat.Png or PdfExportFormat.Jpeg or PdfExportFormat.WordImages or PdfExportFormat.PowerPointImages;
        using var imageOutput = new MemoryStream();
        ZipArchive? imageZip = null;
        var noTextPages = 0;
        try
        {
            if (format is PdfExportFormat.Png or PdfExportFormat.Jpeg && document.PageCount > 1)
                imageZip = new ZipArchive(imageOutput, ZipArchiveMode.Create, leaveOpen: true);
            for (var index = 1; index <= document.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在本地转换第 {index} / {document.PageCount} 页…");
                // 枚举只读取尺寸和内容，没有脚本执行环境。
                var pageInfo = document.GetPageSize(index);
                var text = hasImages ? "" : document.ExtractText(index);
                if (!hasImages && string.IsNullOrWhiteSpace(text)) noTextPages++;
                byte[]? image = null;
                if (hasImages)
                {
                    var scale = dpi / 72d;
                    if (pageInfo.Width * scale > 8192 || pageInfo.Height * scale > 8192) throw new PdfToolException("页面在当前分辨率下单边超过 8192 像素，请降低 DPI。");
                    var width = Math.Max(1, (int)Math.Ceiling(pageInfo.Width * scale));
                    var height = Math.Max(1, (int)Math.Ceiling(pageInfo.Height * scale));
                    if ((long)width * height > PdfiumRuntime.MAX_RENDER_PIXELS) throw new PdfToolException("页面在当前分辨率下过大，请降低 DPI 或拆分页面。");
                    var bitmap = document.Render(index, 0, width, height);
                    try { image = LocalImageCodec.Encode(new PdfBgraImage(bitmap.Bgra, bitmap.Width, bitmap.Height, bitmap.Stride), format == PdfExportFormat.Jpeg); }
                    finally { CryptographicOperations.ZeroMemory(bitmap.Bgra); }
                }
                if (format is PdfExportFormat.Png or PdfExportFormat.Jpeg)
                {
                    try
                    {
                        if (imageZip is not null)
                        {
                            using var entry = imageZip.CreateEntry($"page-{index:D4}.{(format == PdfExportFormat.Png ? "png" : "jpg")}", CompressionLevel.NoCompression).Open();
                            entry.Write(image!);
                        }
                        else imageOutput.Write(image!);
                    }
                    finally { CryptographicOperations.ZeroMemory(image!); }
                }
                else pages.Add(new ConversionPage(index, pageInfo.Width, pageInfo.Height, text, image));
            }
            imageZip?.Dispose();
            imageZip = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (format is PdfExportFormat.Png or PdfExportFormat.Jpeg) return imageOutput.ToArray();
            if (!hasImages && noTextPages == document.PageCount) throw new PdfToolException("没有检测到可提取文字，文件可能是扫描图片。可使用图片版转换；本版本不提供 OCR。");
            if (!hasImages && noTextPages > 0) progress?.Report($"有 {noTextPages} 页没有可提取文字；这些页在文字版中为空。其他页面已提取。");
            if (format == PdfExportFormat.Text) return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n\f\r\n", pages.Select(page => page.Text)))).ToArray();
            return new OpenXmlExporter().Export(pages, format, cancellationToken);
        }
        finally
        {
            imageZip?.Dispose();
            foreach (var page in pages) if (page.Png is not null) CryptographicOperations.ZeroMemory(page.Png);
            pages.Clear();
            if (imageOutput.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
    }

    public static byte[] ImagesToPdf(IReadOnlyList<string> paths, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (paths.Count is < 1 or > 500) throw new PdfToolException("请选择 1–500 个本地图片文件。");
        IEnumerable<ConversionPage> Frames()
        {
            var number = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = LocalFileStore.Read(path);
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    var decoder = LocalImageCodec.Decode(stream);
                    foreach (var frame in decoder.Frames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report($"正在本地处理第 {++number} 张图片…");
                        var pixels = LocalImageCodec.Pixels(frame);
                        byte[]? png = null;
                        try
                        {
                            png = LocalImageCodec.Encode(pixels);
                            // 使用固定 96 DPI，不从 EXIF 读取位置、设备或拍摄信息。
                            yield return new ConversionPage(number, Math.Max(1, pixels.Width * 0.75), Math.Max(1, pixels.Height * 0.75), "", png);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(pixels.Bgra);
                            if (png is not null) CryptographicOperations.ZeroMemory(png);
                        }
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        return ImagePdfBuilder.Build(Frames(), cancellationToken);
    }
}
