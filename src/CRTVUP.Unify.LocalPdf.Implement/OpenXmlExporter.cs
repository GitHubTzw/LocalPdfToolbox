using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>直接生成本地 Office 开放格式，不启动 Office、不生成外部关系或可执行内容。</summary>
public sealed partial class OpenXmlExporter : IOfficeExporter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace REL = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    public byte[] Export(IReadOnlyList<ConversionPage> pages, PdfExportFormat format, CancellationToken cancellationToken = default)
    {
        if (pages.Count is < 1 or > 500) throw new PdfToolException("格式转换一次支持 1–500 页，请在工作区拆分后再转换。");
        if (pages.Any(page => !double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0))
            throw new PdfToolException("页面尺寸无效。");
        using var memory = new MemoryStream();
        try
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                switch (format)
                {
                    case PdfExportFormat.WordText: WriteWord(zip, pages, false, cancellationToken); break;
                    case PdfExportFormat.WordImages: WriteWord(zip, pages, true, cancellationToken); break;
                    case PdfExportFormat.ExcelText: WriteExcel(zip, pages, cancellationToken); break;
                    case PdfExportFormat.PowerPointImages: WritePowerPoint(zip, pages, cancellationToken); break;
                    default: throw new PdfToolException("此 Office 输出格式不受支持。");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return memory.ToArray();
        }
        finally { if (memory.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
    }

    private static XElement Types() => new(CT + "Types",
        new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
        new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
        new XElement(CT + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")));

    private static void Override(XElement types, string part, string type) => types.Add(new XElement(CT + "Override", new XAttribute("PartName", "/" + part), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument." + type)));
    private static XElement Relationship(string id, string type, string target) => new(REL + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + type), new XAttribute("Target", target));

    private static void WriteXml(ZipArchive zip, string path, XElement element)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false, CheckCharacters = true });
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), element).Save(writer);
    }

    private static void WriteBytes(ZipArchive zip, string path, byte[] bytes)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
    }

    private static string XmlText(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c)) text.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { text.Append(c).Append(value[++i]); }
            else text.Append('\uFFFD');
        }
        return text.ToString();
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    private static byte[] RequirePng(ConversionPage page) => page.Png is { Length: > 0 } image ? image : throw new PdfToolException("图片版转换缺少页面图像。");

    private static XElement WordSection(ConversionPage page)
    {
        // Word 单页最大 22 英寸；超大 PDF 等比例缩小，内容图像完整保留。
        var scale = Math.Min(1, 1500d / Math.Max(page.Width, page.Height));
        var width = Math.Max(1440, (int)Math.Round(page.Width * scale * 20));
        var height = Math.Max(1440, (int)Math.Round(page.Height * scale * 20));
        return new XElement(W + "sectPr", new XElement(W + "type", new XAttribute(W + "val", "nextPage")),
            new XElement(W + "pgSz", new XAttribute(W + "w", width), new XAttribute(W + "h", height)),
            new XElement(W + "pgMar", new XAttribute(W + "top", 360), new XAttribute(W + "right", 360), new XAttribute(W + "bottom", 360), new XAttribute(W + "left", 360), new XAttribute(W + "header", 0), new XAttribute(W + "footer", 0), new XAttribute(W + "gutter", 0)));
    }

    private static void WriteWord(ZipArchive zip, IReadOnlyList<ConversionPage> pages, bool images, CancellationToken token)
    {
        var types = Types();
        Override(types, "word/document.xml", "wordprocessingml.document.main+xml");
        Override(types, "word/styles.xml", "wordprocessingml.styles+xml");
        WriteXml(zip, "_rels/.rels", new XElement(REL + "Relationships", Relationship("rId1", "officeDocument", "word/document.xml")));
        var relations = new XElement(REL + "Relationships", Relationship("styles", "styles", "styles.xml"));
        var body = new XElement(W + "body");
        for (var index = 0; index < pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = pages[index];
            if (images)
            {
                var name = $"page-{index + 1}.png";
                WriteBytes(zip, "word/media/" + name, RequirePng(page));
                relations.Add(Relationship("image" + index, "image", "media/" + name));
                var scale = Math.Min(1, 1500d / Math.Max(page.Width, page.Height));
                var availableWidth = Math.Max(36, page.Width * scale - 36);
                var availableHeight = Math.Max(30, page.Height * scale - 44);
                var fit = Math.Min(availableWidth / page.Width, availableHeight / page.Height);
                var cx = (long)(page.Width * fit * 12700);
                var cy = (long)(page.Height * fit * 12700);
                XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
                XNamespace pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
                var graphic = PictureGraphic(pic, "image" + index, cx, cy, index + 1);
                var paragraph = new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "before", 0), new XAttribute(W + "after", 0))),
                    new XElement(W + "r", new XElement(W + "drawing", new XElement(wp + "inline", new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                    new XElement(wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                    new XElement(wp + "docPr", new XAttribute("id", index + 1), new XAttribute("name", "Page " + (index + 1))), graphic))));
                if (index < pages.Count - 1) paragraph.Element(W + "pPr")!.Add(WordSection(page));
                body.Add(paragraph);
            }
            else
            {
                if (index > 0) body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
                foreach (var line in Lines(page.Text)) body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), XmlText(line)))));
            }
        }
        body.Add(WordSection(images ? pages[^1] : new ConversionPage(1, 595, 842, "")));
        WriteXml(zip, "word/document.xml", new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R), body));
        WriteXml(zip, "word/styles.xml", new XElement(W + "styles", new XElement(W + "docDefaults",
            new XElement(W + "rPrDefault", new XElement(W + "rPr", new XElement(W + "rFonts", new XAttribute(W + "ascii", "Arial"), new XAttribute(W + "hAnsi", "Arial"), new XAttribute(W + "eastAsia", "黑体")), new XElement(W + "sz", new XAttribute(W + "val", 22)))),
            new XElement(W + "pPrDefault", new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "after", 60)))))));
        WriteXml(zip, "word/_rels/document.xml.rels", relations);
        WriteXml(zip, "[Content_Types].xml", types);
    }

    private static XElement PictureGraphic(XNamespace pic, string imageId, long cx, long cy, int id) =>
        new(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", pic.NamespaceName),
            new XElement(pic + "pic", new XElement(pic + "nvPicPr", new XElement(pic + "cNvPr", new XAttribute("id", id), new XAttribute("name", "Page " + id)), new XElement(pic + "cNvPicPr")),
                new XElement(pic + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", imageId)), new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(pic + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))))));

    private static void WriteExcel(ZipArchive zip, IReadOnlyList<ConversionPage> pages, CancellationToken token)
    {
        var types = Types();
        Override(types, "xl/workbook.xml", "spreadsheetml.sheet.main+xml");
        var sheets = new XElement(S + "sheets");
        var relations = new XElement(REL + "Relationships");
        for (var index = 0; index < pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var id = index + 1;
            sheets.Add(new XElement(S + "sheet", new XAttribute("name", "第" + id + "页"), new XAttribute("sheetId", id), new XAttribute(R + "id", "rId" + id)));
            relations.Add(Relationship("rId" + id, "worksheet", "worksheets/sheet" + id + ".xml"));
            Override(types, "xl/worksheets/sheet" + id + ".xml", "spreadsheetml.worksheet+xml");
            var data = new XElement(S + "sheetData");
            var row = 0;
            foreach (var line in Lines(pages[index].Text))
            {
                var safe = XmlText(line);
                var offset = 0;
                do
                {
                    var length = Math.Min(32767, safe.Length - offset);
                    if (length > 0 && offset + length < safe.Length && char.IsHighSurrogate(safe[offset + length - 1])) length--;
                    if (++row > 1_048_576) throw new PdfToolException("Excel 行数超过上限。");
                    data.Add(new XElement(S + "row", new XAttribute("r", row), new XElement(S + "c", new XAttribute("r", "A" + row), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), safe.Substring(offset, length))))));
                    offset += length;
                } while (offset < safe.Length);
            }
            WriteXml(zip, "xl/worksheets/sheet" + id + ".xml", new XElement(S + "worksheet", new XElement(S + "cols", new XElement(S + "col", new XAttribute("min", 1), new XAttribute("max", 1), new XAttribute("width", 100), new XAttribute("customWidth", 1))), data));
        }
        WriteXml(zip, "xl/workbook.xml", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R), sheets));
        WriteXml(zip, "xl/_rels/workbook.xml.rels", relations);
        WriteXml(zip, "_rels/.rels", new XElement(REL + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")));
        WriteXml(zip, "[Content_Types].xml", types);
    }
}
