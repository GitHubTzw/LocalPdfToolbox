using System.IO.Compression;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

public sealed partial class OpenXmlExporter
{
    private static void WritePowerPoint(ZipArchive zip, IReadOnlyList<ConversionPage> pages, CancellationToken token)
    {
        var types = Types();
        using var resource = typeof(OpenXmlExporter).Assembly.GetManifestResourceStream("LocalPdf.blank.pptx") ?? throw new PdfToolException("本地幻灯片模板缺失。");
        using var template = new ZipArchive(resource, ZipArchiveMode.Read);
        // 只复制本地空白模板的母版、版式和主题；不复制作者、缩略图、打印机配置。
        foreach (var entry in template.Entries.Where(entry => entry.FullName.StartsWith("ppt/slideMasters/", StringComparison.Ordinal)
            || entry.FullName.StartsWith("ppt/slideLayouts/", StringComparison.Ordinal) || entry.FullName.StartsWith("ppt/theme/", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            using var input = entry.Open();
            using var output = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
            input.CopyTo(output);
            if (entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            {
                var type = entry.FullName.Contains("slideMasters/", StringComparison.Ordinal) ? "presentationml.slideMaster+xml"
                    : entry.FullName.Contains("slideLayouts/", StringComparison.Ordinal) ? "presentationml.slideLayout+xml" : "theme+xml";
                Override(types, entry.FullName, type);
            }
        }
        Override(types, "ppt/presentation.xml", "presentationml.presentation.main+xml");
        var relations = new XElement(REL + "Relationships", Relationship("master", "slideMaster", "slideMasters/slideMaster1.xml"));
        var slideIds = new XElement(P + "sldIdLst");
        // 所有幻灯片必须共享一个页面尺寸；不同比例的 PDF 页居中放置、完整显示。
        var ratio = Math.Clamp(pages[0].Height / pages[0].Width, 0.2, 5);
        const long SLIDE_WIDTH = 9144000;
        var slideHeight = (long)(SLIDE_WIDTH * ratio);
        for (var index = 0; index < pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var number = index + 1;
            var page = pages[index];
            var scale = Math.Min(SLIDE_WIDTH / page.Width, slideHeight / page.Height);
            var cx = (long)(page.Width * scale);
            var cy = (long)(page.Height * scale);
            var picture = new XElement(P + "pic", new XElement(P + "nvPicPr", new XElement(P + "cNvPr", new XAttribute("id", 2), new XAttribute("name", "Page " + number)),
                new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))), new XElement(P + "nvPr")),
                new XElement(P + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", "image")), new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(P + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", (SLIDE_WIDTH - cx) / 2), new XAttribute("y", (slideHeight - cy) / 2)), new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
            var tree = new XElement(P + "spTree", new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
                new XElement(P + "grpSpPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
                    new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))), picture);
            var slide = new XElement(P + "sld", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XAttribute(XNamespace.Xmlns + "p", P),
                new XElement(P + "cSld", tree), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")));
            WriteXml(zip, $"ppt/slides/slide{number}.xml", slide);
            WriteXml(zip, $"ppt/slides/_rels/slide{number}.xml.rels", new XElement(REL + "Relationships", Relationship("layout", "slideLayout", "../slideLayouts/slideLayout7.xml"), Relationship("image", "image", $"../media/page-{number}.png")));
            WriteBytes(zip, $"ppt/media/page-{number}.png", RequirePng(page));
            Override(types, $"ppt/slides/slide{number}.xml", "presentationml.slide+xml");
            slideIds.Add(new XElement(P + "sldId", new XAttribute("id", 256 + index), new XAttribute(R + "id", "slide" + number)));
            relations.Add(Relationship("slide" + number, "slide", $"slides/slide{number}.xml"));
        }
        WriteXml(zip, "ppt/presentation.xml", new XElement(P + "presentation", new XAttribute(XNamespace.Xmlns + "p", P), new XAttribute(XNamespace.Xmlns + "r", R),
            new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", 2147483648), new XAttribute(R + "id", "master"))), slideIds,
            new XElement(P + "sldSz", new XAttribute("cx", SLIDE_WIDTH), new XAttribute("cy", slideHeight), new XAttribute("type", "custom")),
            new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000))));
        WriteXml(zip, "ppt/_rels/presentation.xml.rels", relations);
        WriteXml(zip, "_rels/.rels", new XElement(REL + "Relationships", Relationship("rId1", "officeDocument", "ppt/presentation.xml")));
        WriteXml(zip, "[Content_Types].xml", types);
    }
}
