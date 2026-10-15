using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

internal static class ToolkitChecks
{
    public static void Run(string directory)
    {
        StructuredText();
        Codec();
        Hashes();
        Diff();
        Archive(directory);
        SizeReduction(directory);
        Console.WriteLine("通过：JSON/XML 工具、编码转换、哈希、文本与二进制对比、ZIP/GZip 压缩解压。");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"预期出现 {typeof(T).Name}，操作却成功。");
    }

    private static void StructuredText()
    {
        var tool = new StructuredTextTool();
        const string source = "{\"b\":1,\"a\":{\"c\":[1,2,3]},\"中文\":\"值\"}";
        var formatted = tool.Format(source, StructuredFormat.Json, new FormatOptions());
        Check(formatted.Output.Contains('\n') && !formatted.Output.Contains("\\u4E2D", StringComparison.OrdinalIgnoreCase), "JSON 格式化换行或中文处理不正确。");
        var minified = tool.Minify(source, StructuredFormat.Json);
        Check(!minified.Output.Contains('\n') && minified.Output.Contains("\"b\":1", StringComparison.Ordinal), "JSON 压缩结果不正确。");
        var sorted = tool.Format(source, StructuredFormat.Json, new FormatOptions { SortKeys = true, IndentSize = 4 });
        Check(sorted.Output.IndexOf("\"a\"", StringComparison.Ordinal) < sorted.Output.IndexOf("\"b\"", StringComparison.Ordinal), "JSON 未按属性名排序。");
        Check(sorted.Output.Contains("    \"b\"", StringComparison.Ordinal), "JSON 缩进宽度未生效。");
        Check(tool.Validate(source, StructuredFormat.Json).IsValid, "有效 JSON 被判为无效。");
        var invalid = tool.Validate("{\"a\":1,}", StructuredFormat.Json);
        Check(!invalid.IsValid && invalid.Line > 0, "无效 JSON 未给出位置。");
        var escaped = tool.EscapeJsonString("中文 \"引号\"\n换行");
        Check(escaped.Contains("\\u4E2D", StringComparison.OrdinalIgnoreCase) && escaped.Contains("\\n", StringComparison.Ordinal), "JSON 转义结果不正确。");
        Check(tool.UnescapeJsonString(escaped) == "中文 \"引号\"\n换行", "JSON 反转义未还原原文。");

        var xml = tool.Format("<root><a>1</a><b/></root>", StructuredFormat.Xml, new FormatOptions());
        Check(xml.Output.Contains("<a>1</a>", StringComparison.Ordinal), "XML 格式化结果不正确。");
        Check(!tool.Validate("<root><a></root>", StructuredFormat.Xml).IsValid, "无效 XML 未报错。");
        Check(!tool.Validate("<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///C:/secret.txt\">]><r>&x;</r>", StructuredFormat.Xml).IsValid, "XML 外部实体未被拒绝。");

        // 默认按 CRTVUP.BrightBook 项目的模型风格输出：块级命名空间、Model 后缀、逐成员注释。
        var classes = tool.ToCSharpClasses(
            "{\"user_name\":\"a\",\"age\":3,\"tags\":[\"x\"],\"items\":[{\"id\":1}],\"class\":true}", new CSharpClassOptions());
        Check(classes.Contains("namespace CRTVUP.BrightBook.BrightBookService.Abstractions.Models", StringComparison.Ordinal)
            && classes.Contains("{" + Environment.NewLine, StringComparison.Ordinal), "未生成块级命名空间。");
        Check(classes.Contains("    public class RootModel", StringComparison.Ordinal), "未生成带 Model 后缀的根类。");
        Check(classes.Contains("        /// <summary>", StringComparison.Ordinal), "未生成成员注释。");
        Check(classes.Contains("public string UserName", StringComparison.Ordinal), "未生成字符串属性。");
        Check(classes.Contains("[JsonPropertyName(\"user_name\")]", StringComparison.Ordinal), "未生成属性名映射。");
        Check(classes.Contains("public long Age", StringComparison.Ordinal), "未生成整数属性。");
        Check(classes.Contains("public List<string> Tags", StringComparison.Ordinal), "未生成字符串列表属性。");
        Check(classes.Contains("public List<ItemsModel> Items", StringComparison.Ordinal), "未生成嵌套对象列表。");
        Check(classes.Contains("public bool _Class", StringComparison.Ordinal), "关键字属性名未处理。");
        Check(!classes.Contains("List<object> Items", StringComparison.Ordinal), "嵌套对象被降级为 object。");
        Check(classes.Contains("using System.Collections.Generic;", StringComparison.Ordinal)
            && classes.Contains("using System.Text.Json.Serialization;", StringComparison.Ordinal), "未生成所需 using。");
        Check(!classes.Contains("using System.ComponentModel.DataAnnotations;", StringComparison.Ordinal), "未用到 DataAnnotations 时不应引用。");

        var custom = tool.ToCSharpClasses("{\"Name\":\"a\",\"Count\":1}",
            new CSharpClassOptions { RootName = "Book", ClassSuffix = "Value", Namespace = "Demo.Models", StringLength = 50 });
        Check(custom.Contains("namespace Demo.Models", StringComparison.Ordinal), "自定义命名空间未生效。");
        Check(custom.Contains("public class BookValue", StringComparison.Ordinal), "自定义类名后缀未生效。");
        Check(custom.Contains("[StringLength(50, ErrorMessage = \"Name不能超过50个字符\")]", StringComparison.Ordinal), "StringLength 特性未生成。");
        Check(custom.Contains("using System.ComponentModel.DataAnnotations;", StringComparison.Ordinal), "未生成 DataAnnotations 引用。");
        Check(!custom.Contains("JsonPropertyName", StringComparison.Ordinal), "名称完全一致时不应生成 JsonPropertyName。");
        Check(custom.Contains("public long Count", StringComparison.Ordinal), "整数字段不应带 StringLength。");
        var noNamespace = tool.ToCSharpClasses("{\"name\":\"a\"}", new CSharpClassOptions { Namespace = "" });
        Check(!noNamespace.Contains("namespace ", StringComparison.Ordinal), "留空命名空间时不应生成 namespace。");
        Check(noNamespace.Contains("public class RootModel", StringComparison.Ordinal), "无命名空间时根类名不正确。");
        Expect<PdfToolException>(() => tool.ToCSharpClasses("{\"a\":1}", new CSharpClassOptions { Namespace = "Demo..Models" }));
    }

    private static void Codec()
    {
        var codec = new TextCodec();
        Check(codec.Apply("中文ABC", TextCodecOperation.Base64Encode) == Convert.ToBase64String(Encoding.UTF8.GetBytes("中文ABC")), "Base64 编码不正确。");
        Check(codec.Apply(codec.Apply("中文ABC", TextCodecOperation.Base64Encode), TextCodecOperation.Base64Decode) == "中文ABC", "Base64 未还原。");
        Check(codec.Apply("a b&c", TextCodecOperation.UrlEncode) == "a%20b%26c", "URL 编码不正确。");
        Check(codec.Apply("a%20b%26c", TextCodecOperation.UrlDecode) == "a b&c", "URL 解码不正确。");
        Check(codec.Apply("中文", TextCodecOperation.UnicodeEncode) == "\\u4E2D\\u6587", "Unicode 转义不正确。");
        Check(codec.Apply("\\u4E2D\\u6587", TextCodecOperation.UnicodeDecode) == "中文", "Unicode 还原不正确。");
        Check(codec.Apply("<a href=\"x\">A&B</a>", TextCodecOperation.HtmlEncode) == "&lt;a href=&quot;x&quot;&gt;A&amp;B&lt;/a&gt;", "HTML 转义不正确。");
        Check(codec.Apply("&lt;a&gt;&amp;&#20013;&#x6587;", TextCodecOperation.HtmlDecode) == "<a>&中文", "HTML 反转义不正确。");
        Check(codec.Apply("AB", TextCodecOperation.HexEncode) == "4142", "十六进制编码不正确。");
        Check(codec.Apply("0x41 42", TextCodecOperation.HexDecode) == "AB", "十六进制解码不正确。");
        var gbk = codec.Encode("中文", TextCharset.Gbk);
        Check(gbk.Length == 4 && gbk[0] == 0xD6 && gbk[2] == 0xCE, "GBK 编码不正确。");
        Check(codec.Apply(codec.Apply("中文", TextCodecOperation.Base64Encode, TextCharset.Gbk), TextCodecOperation.Base64Decode, TextCharset.Gbk) == "中文", "GBK Base64 未还原。");
        Expect<PdfToolException>(() => codec.Apply("!!!!", TextCodecOperation.Base64Decode));
        Expect<PdfToolException>(() => codec.Apply("ABC", TextCodecOperation.HexDecode));
        Check(UnixTime.ToUnix(UnixTime.FromUnix(1700000000, false), false) == 1700000000, "时间戳互转不正确。");
    }

    private static void Hashes()
    {
        var tool = new HashTool();
        Check(tool.Compute("abc", HashKind.Md5) == "900150983cd24fb0d6963f7d28e17f72", "MD5 结果不正确。");
        Check(tool.Compute("abc", HashKind.Sha1) == "a9993e364706816aba3e25717850c26c9cd0d89d", "SHA1 结果不正确。");
        Check(tool.Compute("abc", HashKind.Sha256) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA256 结果不正确。");
        Check(tool.Compute("abc", HashKind.Sha512).StartsWith("ddaf35a193617aba", StringComparison.Ordinal), "SHA512 结果不正确。");
    }

    private static void Diff()
    {
        var tool = new TextDiffTool();
        var same = tool.Compare("a\nb\nc", "a\nb\nc", new DiffOptions());
        Check(same.Identical && same.Changed == 0 && same.Lines.Count == 3, "相同文本被判定为有差异。");
        var changed = tool.Compare("a\nb\nc", "a\nB\nc", new DiffOptions());
        Check(!changed.Identical && changed.Changed == 1 && changed.Lines.Count == 3, "修改行未被识别。");
        Check(changed.Lines[1].Kind == DiffLineKind.Changed && changed.Lines[1].LeftText == "b" && changed.Lines[1].RightText == "B", "修改行内容不对齐。");
        var inserted = tool.Compare("a\nc", "a\nb\nc", new DiffOptions());
        Check(inserted.Added == 1 && inserted.Lines.Count == 3 && inserted.Lines[1].Kind == DiffLineKind.Added, "新增行未被识别。");
        var removed = tool.Compare("a\nb\nc", "a\nc", new DiffOptions());
        Check(removed.Removed == 1 && removed.Lines.Count == 3 && removed.Lines[1].Kind == DiffLineKind.Removed, "删除行未被识别。");
        Check(tool.Compare("ABC", "abc", new DiffOptions { IgnoreCase = true }).Identical, "忽略大小写未生效。");
        Check(tool.Compare("a\n\nb", "a\nb", new DiffOptions { IgnoreBlankLines = true }).Identical, "忽略空行未生效。");
        Check(tool.Compare("a b", "ab", new DiffOptions { IgnoreWhitespace = true }).Identical, "忽略空白未生效。");
        Check(tool.Compare("", "", new DiffOptions()).Identical, "空文本对比结果不正确。");

        var left = string.Join("\n", Enumerable.Range(0, 3000).Select(index => "L" + index));
        var right = string.Join("\n", Enumerable.Range(0, 3000).Select(index => "R" + index));
        var coarse = tool.Compare(left, right, new DiffOptions());
        Check(coarse.Changed == 3000 && coarse.Summary.Contains("整块", StringComparison.Ordinal), "整块差异回退结果不正确。");

        var binary = new BinaryDiffTool();
        Check(binary.Compare(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }).Identical, "相同字节被判为不同。");
        var different = binary.Compare(new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3 });
        Check(!different.Identical && different.FirstDifferenceOffset == 1, "二进制差异位置不正确。");
        Check(binary.Compare(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }).FirstDifferenceOffset == 2, "长度不同的二进制对比不正确。");
    }

    private static void Archive(string directory)
    {
        var tool = new ArchiveTool();
        var source = Path.Combine(directory, "archive-source");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "ALPHA");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "BETA");
        var zip = Path.Combine(directory, "archive.zip");
        var result = tool.CreateZip(zip, [source], ArchiveLevel.Optimal);
        Check(result.EntryCount == 2 && File.Exists(zip), "ZIP 未生成预期条目。");
        var entries = tool.ListZip(zip);
        Check(entries.Any(entry => entry.Path == "archive-source/a.txt")
            && entries.Any(entry => entry.Path == "archive-source/sub/b.txt"), "ZIP 条目名称不正确。");
        Expect<PdfToolException>(() => tool.CreateZip(zip, [source], ArchiveLevel.Optimal));
        var target = Path.Combine(directory, "archive-out");
        var extracted = tool.ExtractZip(zip, target);
        Check(extracted.EntryCount == 2 && File.ReadAllText(Path.Combine(target, "archive-source", "a.txt")) == "ALPHA", "解压内容不正确。");
        Expect<PdfToolException>(() => tool.ExtractZip(zip, target));

        var evil = Path.Combine(directory, "evil.zip");
        WriteRawZip(evil, "../evil.txt", "EVIL");
        Expect<PdfToolException>(() => tool.ExtractZip(evil, Path.Combine(directory, "evil-out")));
        Check(!File.Exists(Path.Combine(directory, "evil.txt")), "越界条目写出了目标目录。");

        var text = Path.Combine(directory, "gzip-source.txt");
        File.WriteAllText(text, new string('Z', 4096));
        var gzip = Path.Combine(directory, "gzip-source.txt.gz");
        tool.CompressGzip(gzip, text, ArchiveLevel.Optimal);
        Check(new FileInfo(gzip).Length < 4096, "GZip 未压缩。");
        var restored = Path.Combine(directory, "gzip-restored.txt");
        tool.ExtractGzip(restored, gzip);
        Check(File.ReadAllText(restored) == new string('Z', 4096), "GZip 解压内容不一致。");

        Expect<PdfToolException>(() => tool.ListZip(@"\\server.invalid\share\x.zip"));
        Expect<PdfToolException>(() => tool.ExtractZip(zip, @"\\server.invalid\share\out"));
    }

    /// <summary>手工构造仅含一个存储条目的最小 ZIP，用于验证越界条目会被拒绝。</summary>
    private static void SizeReduction(string directory)
    {
        Check(CompressKindDetector.Detect("a.pdf") == CompressFileKind.Pdf
            && CompressKindDetector.Detect("b.PNG") == CompressFileKind.Image
            && CompressKindDetector.Detect("c.docx") == CompressFileKind.Office
            && CompressKindDetector.Detect("d.txt") == CompressFileKind.Unknown, "文件类别识别不正确。");

        // PDF 体积压缩：完整重写结构，页数和文字保持不变，源文件不变。
        var pdfPath = Path.Combine(directory, "reduce-source.pdf");
        var pdfBytes = TextPdfBuilder.Build(["REDUCE ME", "SECOND PAGE"]);
        File.WriteAllBytes(pdfPath, pdfBytes);
        var pdfHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath)));
        var pdfTarget = Path.Combine(directory, "reduce-out.pdf");
        var pdfResult = new PdfSizeReducer().Reduce(pdfPath, pdfTarget, new CompressOptions());
        Check(File.Exists(pdfTarget) && pdfResult.OriginalBytes == pdfBytes.Length, "PDF 压缩未生成新文件。");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))) == pdfHash, "PDF 压缩改动了源文件。");
        using (var document = PdfiumRuntime.Open(File.ReadAllBytes(pdfTarget)))
        {
            Check(document.PageCount == 2, "PDF 压缩后页数发生变化。");
            Check(document.ExtractText(1).Contains("REDUCE ME", StringComparison.Ordinal), "PDF 压缩后文字丢失。");
        }
        Expect<PdfToolException>(() => new PdfSizeReducer().Reduce(pdfPath, pdfTarget, new CompressOptions()));
        Expect<PdfToolException>(() => new PdfSizeReducer().Reduce(pdfPath, @"\\server.invalid\share\x.pdf", new CompressOptions()));

        // Office 体积压缩：重新打包内部 ZIP，条目内容保持一致。
        var docxPath = Path.Combine(directory, "reduce-source.docx");
        WriteOfficePackage(docxPath);
        var officeTarget = Path.Combine(directory, "reduce-out.docx");
        var officeResult = new OfficeSizeReducer().Reduce(docxPath, officeTarget, new CompressOptions());
        Check(File.Exists(officeTarget) && officeResult.CompressedBytes > 0, "Office 压缩未生成新文件。");
        using (var archive = ZipFile.OpenRead(officeTarget))
        {
            Check(archive.Entries.Count == 2, "Office 压缩后条目数量变化。");
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            Check(reader.ReadToEnd().Contains("HELLO", StringComparison.Ordinal), "Office 压缩后内容变化。");
        }
        var notOffice = Path.Combine(directory, "not-office.docx");
        File.WriteAllText(notOffice, "NOT A PACKAGE");
        Expect<PdfToolException>(() => new OfficeSizeReducer().Reduce(notOffice, Path.Combine(directory, "bad.docx"), new CompressOptions()));

        Check(FileSizeFormatter.Format(2048) == "2 KB", "体积文本格式化不正确。");
        Check(FileSizeFormatter.DescribeChange(100, 40, "测试").Contains("减少 60%", StringComparison.Ordinal), "减少比例说明不正确。");
        Check(FileSizeFormatter.DescribeChange(100, 120, "测试").Contains("建议保留原文件", StringComparison.Ordinal), "变大时未提示保留原文件。");
    }

    /// <summary>写一个最小的 docx 结构（ZIP + 两个条目），用于验证 Office 重新打包。</summary>
    private static void WriteOfficePackage(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
            writer.Write("<Types/>");
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
            writer.Write("<w:document>HELLO</w:document>");
    }

    private static void WriteRawZip(string path, string entryName, string content)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        var data = Encoding.UTF8.GetBytes(content);
        var crc = Crc32(data);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(file);
        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0x0800);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(crc);
        writer.Write((uint)data.Length);
        writer.Write((uint)data.Length);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        writer.Write(name);
        writer.Write(data);
        var centralOffset = file.Position;
        writer.Write(0x02014b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0x0800);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(crc);
        writer.Write((uint)data.Length);
        writer.Write((uint)data.Length);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(name);
        var centralSize = file.Position - centralOffset;
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)centralSize);
        writer.Write((uint)centralOffset);
        writer.Write((ushort)0);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        var crc = 0xFFFFFFFFu;
        foreach (var item in data) crc = table[(crc ^ item) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
