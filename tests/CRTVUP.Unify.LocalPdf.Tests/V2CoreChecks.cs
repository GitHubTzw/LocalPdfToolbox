using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using PigPoint = UglyToad.PdfPig.Core.PdfPoint;

internal static class V2CoreChecks
{
    public static void Run()
    {
        MemoryWorkspace();
        TextPdf();
        OfficePackages();
        UntrustedOffice();
        Console.WriteLine("通过：V2 内存事务、中文字版 PDF、Office 文档结构、文本无丢失和外部实体拒绝。");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static byte[] Fixture(string text = "ORIGINAL")
    {
        using var builder = new PdfDocumentBuilder { IncludeDocumentInformation = false };
        var page = builder.AddPage(400, 600);
        page.AddText(text, 24, new PigPoint(50, 500), builder.AddStandard14Font(Standard14Font.Helvetica));
        return builder.Build();
    }

    private static void MemoryWorkspace()
    {
        using var workspace = new PdfWorkspace();
        var input = Fixture();
        var source = workspace.LoadFromMemory("合成.pdf", input);
        var old = workspace.GetContent(source.Id);
        using var editor = PdfiumRuntime.Open(old);
        var text = editor.EnumerateObjects(1).Objects.Single(item => item.Kind == PdfObjectKind.Text);
        var replacement = editor.ApplyEdit(new PdfEditRequest { PageNumber = 1, Kind = PdfEditKind.ReplaceText, ObjectIndex = text.Index, Text = "本地修改 TEST" });
        var updated = workspace.ReplaceContent(source.Id, replacement);
        Check(updated.Id == source.Id, "内容替换改变了来源标识。");
        Check(old.Span.IndexOfAnyExcept((byte)0) == -1, "替换后旧源字节未归零。");
        var result = workspace.ExportToMemory([new PageEdit(source.Id, 1, 90)]);
        using var output = PdfiumRuntime.Open(result);
        Check(output.ExtractText(1).Contains("本地修改 TEST", StringComparison.Ordinal), "编辑后合并导出丢失中文。");
        var size = output.GetPageSize(1);
        Check(Math.Abs(size.Width - 600) < 0.1 && Math.Abs(size.Height - 400) < 0.1, "内容替换影响页序旋转。");
        using var original = PdfiumRuntime.Open(input);
        Check(original.ExtractText(1).Contains("ORIGINAL", StringComparison.Ordinal), "编辑改变了调用方源字节。");
        var before = workspace.GetContent(source.Id).ToArray();
        try { workspace.ReplaceContent(source.Id, new byte[] { 1, 2, 3 }); throw new InvalidOperationException("接受了损坏替换结果。"); }
        catch (PdfToolException) { }
        Check(workspace.GetContent(source.Id).Span.SequenceEqual(before), "失败替换没有保持原子性。");
        CryptographicOperations.ZeroMemory(before);
        CryptographicOperations.ZeroMemory(result);
        CryptographicOperations.ZeroMemory(replacement);
    }

    private static void TextPdf()
    {
        var repeated = string.Concat(Enumerable.Repeat("中文排版测试 LONG-WORD-123 ", 24));
        var pdf = TextPdfBuilder.Build(["纯本地文字解析\nALPHA <>& 123\n" + repeated, "第二节中文\nSECOND SECTION"]);
        using var document = PdfDocument.Open(pdf);
        using var renderer = PdfiumRuntime.Open(pdf);
        Check(document.NumberOfPages >= 2, "多节文字没有分页。");
        var text = string.Concat(Enumerable.Range(1, renderer.PageCount).Select(renderer.ExtractText)).Replace("\r", "").Replace("\n", "");
        Check(text.Contains("纯本地文字解析", StringComparison.Ordinal) && text.Contains("第二节中文", StringComparison.Ordinal), "中文字版 PDF 不可提取。");
        foreach (var page in document.GetPages())
            Check(page.Letters.All(letter => letter.GlyphRectangle.Right <= page.Width - 40 && letter.GlyphRectangle.Bottom >= 35), "长行文字超出页面边界。");
        CryptographicOperations.ZeroMemory(pdf);
    }

    private static void OfficePackages()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHM4AAAAASUVORK5CYII=");
        var value = "中文 <>&\n=HYPERLINK(\"https://invalid.example\",\"not a formula\")\n" + new string('字', 33000);
        var pages = new[] { new ConversionPage(1, 595, 842, value, png), new ConversionPage(2, 842, 595, "SECOND PAGE", png) };
        foreach (var format in new[] { PdfExportFormat.WordText, PdfExportFormat.WordImages, PdfExportFormat.ExcelText, PdfExportFormat.PowerPointImages })
        {
            var bytes = new OpenXmlExporter().Export(pages, format);
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            Check(zip.Entries.All(entry => !entry.FullName.StartsWith("docProps/", StringComparison.Ordinal) && !entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase)), "输出带入了元数据或宏。");
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
            {
                using var relationStream = entry.Open();
                var relations = XDocument.Load(relationStream);
                Check(relations.Descendants().All(item => (string?)item.Attribute("TargetMode") != "External"), "生成了外部关系。");
                foreach (var item in relations.Root!.Elements())
                {
                    var target = (string?)item.Attribute("Target");
                    Check(target is not null && !target.Contains("://", StringComparison.Ordinal), "关系中出现外部地址。");
                }
            }
            if (format == PdfExportFormat.WordText)
            {
                var read = LocalTextImporter.Read(bytes, ".docx");
                Check(read.Sections[0].Contains("中文 <>&", StringComparison.Ordinal) && read.Sections[0].Contains(new string('字', 33000), StringComparison.Ordinal), "Word 文字损失或 XML 转义错误。");
            }
            else if (format == PdfExportFormat.ExcelText)
            {
                using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var xml = XDocument.Load(sheet);
                XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Check(!xml.Descendants(s + "f").Any(), "Excel 文本被写成公式。");
                Check(xml.Descendants(s + "c").All(cell => (string?)cell.Attribute("t") == "inlineStr"), "Excel 单元格不是纯文字。");
                var cells = xml.Descendants(s + "t").Select(item => item.Value).ToArray();
                Check(cells.All(cell => cell.Length <= 32767) && string.Concat(cells).Contains(new string('字', 33000), StringComparison.Ordinal), "Excel 长文字被截断。");
                var read = LocalTextImporter.Read(bytes, ".xlsx");
                Check(read.Sections.Count == 2 && read.Sections[1].Contains("SECOND PAGE", StringComparison.Ordinal), "Excel 文字不能重新解析。");
            }
            else Check(zip.Entries.Count(entry => entry.FullName.EndsWith(".png", StringComparison.Ordinal)) == 2, "图片版丢失页面图像。");
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void UntrustedOffice()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
            writer.Write("<!DOCTYPE doc [<!ENTITY external SYSTEM 'file:///C:/should-never-read.txt'>]><w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>&external;</w:t></w:r></w:p></w:body></w:document>");
        }
        try { LocalTextImporter.Read(memory.ToArray(), ".docx"); throw new InvalidOperationException("接受了 XML 外部实体。"); }
        catch (PdfToolException) { }
        var text = Encoding.UTF8.GetBytes("纯本地 TXT\n第二行");
        Check(LocalTextImporter.Read(text, ".txt").Sections[0] == "纯本地 TXT\n第二行", "UTF-8 中文 TXT 解析错误。");
    }
}
