using System.IO.Compression;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

/// <summary>仅构造内存数据，验证文本导入限额和外部资源隔离。</summary>
internal static class LocalImporterChecks
{
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string REL = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static void Run()
    {
        var normal = Workbook("<row><c r='A1' t='s'><v>0</v></c><c r='C1'><f>1+2</f><v>3</v></c></row>", "正常文字");
        Check(LocalTextImporter.Read(normal, ".xlsx").Sections.Single() == "合成\n正常文字\t\t3",
            "共享文字、稀疏列位置或公式缓存值读取错误。");
        ExpectRejected(Encoding.UTF8.GetBytes(new string('A', 2_000_001)), ".txt");
        ExpectRejected(Workbook(string.Concat(Enumerable.Range(1, 21)
            .Select(index => $"<row><c r='A{index}' t='s'><v>0</v></c></row>")), new string('X', 100_000)), ".xlsx");
        ExpectRejected(Workbook(string.Concat(Enumerable.Range(1, 123)
            .Select(index => $"<row><c r='XFD{index}'><v>1</v></c></row>")), ""), ".xlsx");
        ExpectRejected(Workbook("<row><c r='A1'/><c r='A1'/></row>", ""), ".xlsx");

        foreach (var target in new[] { "https://invalid.example/sheet.xml", "//invalid.example/sheet.xml", "../../escape.xml", "worksheets/sheet1.xml?token=1" })
            ExpectRejected(Workbook("<row><c r='A1'><v>1</v></c></row>", "", target: target), ".xlsx");
        ExpectRejected(Workbook("<row><c r='A1'><v>1</v></c></row>", "", mode: "External"), ".xlsx");
        ExpectRejected(Workbook("<row><c r='A1'><v>1</v></c></row>", "", duplicateRelation: true), ".xlsx");

        var slide = $"<p:sld xmlns:p='{P}' xmlns:a='{A}'><a:p><a:r><a:t>第一行</a:t></a:r><a:br/><a:r><a:t>第二行</a:t></a:r></a:p></p:sld>";
        Check(LocalTextImporter.Read(Presentation(1, slide), ".pptx").Sections.Single() == "第一行\n第二行",
            "幻灯片段落内换行丢失。");
        ExpectRejected(Presentation(501, slide), ".pptx");
        ExpectRejected(Package(new Dictionary<string, string>
        {
            ["word/document.xml"] = "<!DOCTYPE x [<!ENTITY ext SYSTEM 'https://invalid.example/content'>]><x>&ext;</x>"
        }), ".docx");
        Console.WriteLine("通过：文字导入提前限额、共享字符串/稀疏列放大拒绝、外部关系隔离和幻灯片换行。");
    }

    private static byte[] Workbook(string rows, string shared, string target = "worksheets/sheet1.xml",
        string mode = "Internal", bool duplicateRelation = false)
    {
        var relation = $"<Relationship Id='s1' Target='{target}' TargetMode='{mode}'/>";
        return Package(new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"<workbook xmlns='{S}' xmlns:r='{R}'><sheets><sheet name='合成' r:id='s1'/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns='{REL}'>{relation}{(duplicateRelation ? relation : "")}</Relationships>",
            ["xl/sharedStrings.xml"] = $"<sst xmlns='{S}'><si><t>{shared}</t></si></sst>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns='{S}'><sheetData>{rows}</sheetData></worksheet>"
        });
    }

    private static byte[] Presentation(int count, string slide) => Package(new Dictionary<string, string>
    {
        ["ppt/presentation.xml"] = $"<p:presentation xmlns:p='{P}' xmlns:r='{R}'><p:sldIdLst>"
            + string.Concat(Enumerable.Range(1, count).Select(index => $"<p:sldId id='{index}' r:id='s1'/>"))
            + "</p:sldIdLst></p:presentation>",
        ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns='{REL}'><Relationship Id='s1' Target='slides/slide1.xml'/></Relationships>",
        ["ppt/slides/slide1.xml"] = slide
    });

    private static byte[] Package(Dictionary<string, string> parts)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, value) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(value);
            }
        return output.ToArray();
    }

    private static void ExpectRejected(byte[] bytes, string extension)
    {
        var original = bytes.ToArray();
        try
        {
            LocalTextImporter.Read(bytes, extension);
            throw new InvalidOperationException("未拒绝超限或无效的合成文字文档。");
        }
        catch (PdfToolException) { }
        Check(bytes.AsSpan().SequenceEqual(original), "失败导入修改了调用方的源内容。");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
