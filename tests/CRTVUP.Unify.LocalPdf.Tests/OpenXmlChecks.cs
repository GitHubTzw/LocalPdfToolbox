using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

/// <summary>仅使用内存中的合成文字和 PNG，核对 Office 包内关系、页面和文字完整性。</summary>
public static class OpenXmlChecks
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void Run()
    {
        var longText = new string('字', 32766) + "😀" + new string('文', 32768);
        var pages = new[]
        {
            new ConversionPage(8, 595, 842,
                " 中文 <tag> & \"双引号\" '单引号' 😀 \r\n=1+2\n+SUM(1,2)\n-10+20\n@SUM(A1)\n\t=3\n\n" + longText,
                Png(240, 20, 40)),
            new ConversionPage(3, 842, 595, "第二页\nSECOND PAGE", Png(20, 220, 60)),
            new ConversionPage(8, 1800, 2000, "第三页\nTHIRD PAGE", Png(30, 60, 230))
        };
        CheckExport(pages, PdfExportFormat.WordText, zip => CheckWordText(zip, pages));
        CheckExport(pages, PdfExportFormat.WordImages, zip => CheckWordImages(zip, pages));
        CheckExport(pages, PdfExportFormat.ExcelText, zip => CheckExcel(zip, pages, longText));
        CheckExport(pages, PdfExportFormat.PowerPointImages, zip => CheckPowerPoint(zip, pages));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var format in new[] { PdfExportFormat.WordText, PdfExportFormat.WordImages, PdfExportFormat.ExcelText, PdfExportFormat.PowerPointImages })
        {
            try
            {
                new OpenXmlExporter().Export(pages, format, cancellation.Token);
                throw new InvalidOperationException("已取消的 Office 导出仍然返回结果。");
            }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("通过：Office 内部关系闭合、混合尺寸逐页图片、公式形文字和长 Unicode 文字完整性。");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void CheckExport(ConversionPage[] pages, PdfExportFormat format, Action<ZipArchive> verify)
    {
        var originalImages = pages.Select(page => page.Png!.ToArray()).ToArray();
        var bytes = new OpenXmlExporter().Export(pages, format);
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            CheckPackage(zip);
            verify(zip);
            Check(pages.Select((page, index) => page.Png!.SequenceEqual(originalImages[index])).All(value => value),
                "Office 导出改写了调用方的页面图像。");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)?.Open() ?? throw new InvalidOperationException("Office 内部 XML 缺失。");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static byte[] ReadBytes(ZipArchive zip, string path)
    {
        using var input = zip.GetEntry(path)?.Open() ?? throw new InvalidOperationException("Office 内部图片缺失。");
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        return memory.ToArray();
    }

    private static string RelationshipsPart(string part)
    {
        var index = part.LastIndexOf('/');
        return index < 0 ? "_rels/" + part + ".rels" : part[..(index + 1)] + "_rels/" + part[(index + 1)..] + ".rels";
    }

    private static string ResolvePart(string owner, string target)
    {
        Check(!target.Contains(':') && !target.Contains('\\') && !target.Contains('#') && !target.Contains('?'),
            "Office 关系出现外部地址或异常目标。");
        var parts = new List<string>();
        var slash = owner.LastIndexOf('/');
        if (!target.StartsWith('/') && slash >= 0) parts.AddRange(owner[..slash].Split('/'));
        foreach (var component in target.Split('/'))
        {
            if (component is "" or ".") continue;
            if (component == "..")
            {
                Check(parts.Count > 0, "Office 关系越过了包根目录。");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(component);
        }
        return string.Join('/', parts);
    }

    private static Dictionary<string, string> Relationships(ZipArchive zip, string owner)
    {
        var path = owner.Length == 0 ? "_rels/.rels" : RelationshipsPart(owner);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relation in ReadXml(zip, path).Root!.Elements())
        {
            var mode = (string?)relation.Attribute("TargetMode");
            Check(mode is null || string.Equals(mode, "Internal", StringComparison.OrdinalIgnoreCase), "Office 输出包含外部关系。");
            var id = (string?)relation.Attribute("Id");
            var target = (string?)relation.Attribute("Target");
            Check(!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target), "Office 关系缺少标识或目标。");
            var resolved = ResolvePart(owner, target!);
            Check(result.TryAdd(id!, resolved), "Office 关系标识重复。");
            Check(zip.GetEntry(resolved) is not null, "Office 关系指向了未导出的资源。");
        }
        return result;
    }

    private static void CheckPackage(ZipArchive zip)
    {
        var names = zip.Entries.Select(entry => entry.FullName).ToArray();
        Check(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "Office 包包含重复资源。");
        Check(names.All(name => !name.StartsWith("docProps/", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("vba", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("externalLink", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("activeX", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("embeddings/", StringComparison.OrdinalIgnoreCase)),
            "Office 包包含作者元数据、宏或嵌入对象。");
        Check(Relationships(zip, "").Count == 1, "Office 根关系包含额外内容。");

        var types = ReadXml(zip, "[Content_Types].xml");
        var defaults = types.Root!.Elements(CT + "Default").Select(element => (string)element.Attribute("Extension")!).ToHashSet(StringComparer.Ordinal);
        var overrides = types.Root.Elements(CT + "Override").Select(element => ((string)element.Attribute("PartName")!).TrimStart('/')).ToHashSet(StringComparer.Ordinal);
        Check(overrides.All(name => zip.GetEntry(name) is not null), "Office 内容类型指向了缺失资源。");
        Check(types.Descendants().Attributes("ContentType").All(attribute => !attribute.Value.Contains("macro", StringComparison.OrdinalIgnoreCase)),
            "Office 内容类型启用了宏。");

        foreach (var name in names)
        {
            if (name == "[Content_Types].xml") continue;
            Check(overrides.Contains(name) || defaults.Contains(name[(name.LastIndexOf('.') + 1)..]), "Office 资源没有内容类型。");
            if (name.EndsWith(".rels", StringComparison.Ordinal))
            {
                if (name == "_rels/.rels") continue;
                var marker = name.LastIndexOf("/_rels/", StringComparison.Ordinal);
                Check(marker >= 0, "Office 关系资源路径异常。");
                var owner = name[..(marker + 1)] + name[(marker + 7)..^5];
                Check(zip.GetEntry(owner) is not null, "Office 关系没有所属文档。");
                _ = Relationships(zip, owner);
            }
            else if (name.EndsWith(".xml", StringComparison.Ordinal))
            {
                var referenced = ReadXml(zip, name).Descendants().Attributes().Where(attribute => attribute.Name.Namespace == R).ToArray();
                if (referenced.Length == 0) continue;
                var relations = Relationships(zip, name);
                Check(referenced.All(attribute => relations.ContainsKey(attribute.Value)), "Office XML 使用了未声明的关系标识。");
            }
        }
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static void CheckWordText(ZipArchive zip, IReadOnlyList<ConversionPage> pages)
    {
        var document = ReadXml(zip, "word/document.xml");
        Check(document.Descendants(W + "t").Select(element => element.Value).SequenceEqual(pages.SelectMany(page => Lines(page.Text))),
            "Word 文字的 XML 转义、空行、Unicode 或页面顺序不完整。");
        Check(document.Descendants(W + "br").Count(element => (string?)element.Attribute(W + "type") == "page") == pages.Count - 1,
            "Word 文字版没有在源页面之间分页。");
        Check(!document.Descendants(W + "drawing").Any(), "Word 文字版意外变成图片。");
    }

    private static long Number(XElement element, XName attribute) => long.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture);

    private static void CheckWordImages(ZipArchive zip, IReadOnlyList<ConversionPage> pages)
    {
        var document = ReadXml(zip, "word/document.xml");
        var body = document.Root!.Element(W + "body")!;
        var paragraphs = body.Elements(W + "p").ToArray();
        var sections = document.Descendants(W + "sectPr").ToArray();
        var relations = Relationships(zip, "word/document.xml");
        Check(paragraphs.Length == pages.Count && sections.Length == pages.Count && body.Elements(W + "sectPr").Count() == 1,
            "Word 图片版的段落或分节数量与源页面不符。");
        Check(!document.Descendants(W + "t").Any(), "Word 图片版混入了额外正文。");
        for (var index = 0; index < pages.Count; index++)
        {
            var paragraph = paragraphs[index];
            Check(paragraph.Descendants(W + "sectPr").Count() == (index < pages.Count - 1 ? 1 : 0), "Word 图片分节位置不正确。");
            Check((string?)sections[index].Element(W + "type")?.Attribute(W + "val") == "nextPage", "Word 图片没有逐页分节。");
            var extent = paragraph.Descendants(WP + "inline").Single().Element(WP + "extent")!;
            var cx = Number(extent, "cx");
            var cy = Number(extent, "cy");
            var size = sections[index].Element(W + "pgSz")!;
            var margin = sections[index].Element(W + "pgMar")!;
            Check(cx > 0 && cy > 0
                && cx <= (Number(size, W + "w") - Number(margin, W + "left") - Number(margin, W + "right")) * 635
                && cy < (Number(size, W + "h") - Number(margin, W + "top") - Number(margin, W + "bottom")) * 635,
                "Word 整页图片超出了对应节的可用纸张边界。");
            Check(Math.Abs((double)cx / cy - pages[index].Width / pages[index].Height) < 0.000001, "Word 页面图片比例改变。");
            var image = paragraph.Descendants(A + "blip").Single();
            var target = relations[(string)image.Attribute(R + "embed")!];
            Check(ReadBytes(zip, target).SequenceEqual(pages[index].Png!), "Word 图片内容或页面顺序改变。");
            Check(!paragraph.Descendants(A + "srcRect").Any(), "Word 整页图片被裁切。");
        }
    }

    private static void CheckExcel(ZipArchive zip, IReadOnlyList<ConversionPage> pages, string longText)
    {
        var workbook = ReadXml(zip, "xl/workbook.xml");
        var sheets = workbook.Descendants(S + "sheet").ToArray();
        var relations = Relationships(zip, "xl/workbook.xml");
        Check(sheets.Length == pages.Count, "Excel 工作表数量与源页面不符。");
        for (var index = 0; index < pages.Count; index++)
        {
            var sheet = ReadXml(zip, relations[(string)sheets[index].Attribute(R + "id")!]);
            var cells = sheet.Descendants(S + "c").ToArray();
            var values = cells.Select(cell => cell.Element(S + "is")!.Element(S + "t")!.Value).ToArray();
            Check(!sheet.Descendants(S + "f").Any() && cells.All(cell => (string?)cell.Attribute("t") == "inlineStr"),
                "Excel 的公式形文字变成了可执行公式。");
            Check(values.All(value => value.Length <= 32767) && string.Concat(values) == string.Concat(Lines(pages[index].Text)),
                "Excel 文字或超长单元格被截断、重复或重新排序。");
            if (index == 0)
            {
                var sourceLines = Lines(pages[index].Text);
                Check(values.Take(sourceLines.Length - 1).SequenceEqual(sourceLines.Take(sourceLines.Length - 1)), "Excel 的公式形文字或空行发生变化。");
                Check(string.Concat(values.Skip(sourceLines.Length - 1)) == longText, "Excel 在 32767 边界拆散或丢失了 Unicode 代理对。");
            }
        }
    }

    private static void CheckPowerPoint(ZipArchive zip, IReadOnlyList<ConversionPage> pages)
    {
        var presentation = ReadXml(zip, "ppt/presentation.xml");
        var slideIds = presentation.Descendants(P + "sldId").ToArray();
        var relations = Relationships(zip, "ppt/presentation.xml");
        var size = presentation.Root!.Element(P + "sldSz")!;
        var width = Number(size, "cx");
        var height = Number(size, "cy");
        Check(slideIds.Length == pages.Count && presentation.Descendants(P + "sldMasterId").Count() == 1,
            "PowerPoint 幻灯片或母版数量不正确。");
        for (var index = 0; index < pages.Count; index++)
        {
            var part = relations[(string)slideIds[index].Attribute(R + "id")!];
            var slide = ReadXml(zip, part);
            var slideRelations = Relationships(zip, part);
            var picture = slide.Descendants(P + "pic").Single();
            Check(!slide.Descendants(P + "sp").Any() && !slide.Descendants(A + "srcRect").Any(), "PowerPoint 多出正文形状或裁切了图片。");
            var imageId = (string)picture.Descendants(A + "blip").Single().Attribute(R + "embed")!;
            Check(ReadBytes(zip, slideRelations[imageId]).SequenceEqual(pages[index].Png!), "PowerPoint 图片内容或页面顺序改变。");
            var transform = picture.Descendants(A + "xfrm").Single();
            var offset = transform.Element(A + "off")!;
            var extent = transform.Element(A + "ext")!;
            var x = Number(offset, "x");
            var y = Number(offset, "y");
            var cx = Number(extent, "cx");
            var cy = Number(extent, "cy");
            Check(cx > 0 && cy > 0 && x >= 0 && y >= 0 && x + cx <= width && y + cy <= height
                && Math.Abs(2 * x + cx - width) <= 1 && Math.Abs(2 * y + cy - height) <= 1,
                "PowerPoint 图片没有完整居中显示在幻灯片内。");
            Check(Math.Abs((double)cx / cy - pages[index].Width / pages[index].Height) < 0.000001, "PowerPoint 页面图片比例改变。");
            var layoutPart = slideRelations.Values.Single(value => value.StartsWith("ppt/slideLayouts/", StringComparison.Ordinal));
            Check((string?)ReadXml(zip, layoutPart).Root!.Attribute("type") == "blank", "PowerPoint 图片没有使用空白版式。");
        }
    }

    private static byte[] Png(byte red, byte green, byte blue)
    {
        using var memory = new MemoryStream();
        memory.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(memory, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(new byte[] { 0, red, green, blue, 255 });
        WriteChunk(memory, "IDAT", compressed.ToArray());
        WriteChunk(memory, "IEND", Array.Empty<byte>());
        return memory.ToArray();
    }

    private static void WriteChunk(Stream output, string name, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var content = Encoding.ASCII.GetBytes(name).Concat(data).ToArray();
        output.Write(content);
        var crc = uint.MaxValue;
        foreach (var value in content)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }
}
