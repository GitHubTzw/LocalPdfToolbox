using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>只解析开放文档格式中的文字和已保存数值，绝不解析外部资源或执行脚本/公式。</summary>
public static class LocalTextImporter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MAX_TEXT = 2_000_000;

    public static ImportedText Read(ReadOnlyMemory<byte> content, string extension)
    {
        if (content.IsEmpty)
            throw new PdfToolException("文字导入文件为空。");
        if (extension is null || extension.ToLowerInvariant() is not (".txt" or ".docx" or ".xlsx" or ".pptx"))
            throw new PdfToolException("文字导入只支持 TXT、DOCX、XLSX 和 PPTX；不支持旧版或带宏格式。");
        var owned = content.ToArray();
        try { return ReadCore(owned, extension); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(owned); }
    }

    private static ImportedText ReadCore(byte[] content, string extension)
    {
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            using var textStream = new MemoryStream(content, writable: false);
            using var reader = new StreamReader(textStream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            string value;
            try
            {
                var budget = new TextBudget();
                var text = new StringBuilder();
                var buffer = new char[4096];
                try
                {
                    int count;
                    while ((count = reader.Read(buffer)) > 0) budget.Append(text, buffer.AsSpan(0, count));
                    value = text.ToString();
                }
                finally { Array.Clear(buffer); }
            }
            catch (DecoderFallbackException) { throw new PdfToolException("TXT 编码无法识别，请先用记事本另存为 UTF-8 或 UTF-16。"); }
            ValidateText([value]);
            return new ImportedText([value], "按纯文字重新分页，保留换行；字体和页面使用本工具的本地排版。");
        }
        using var memory = new MemoryStream(content, writable: false);
        try
        {
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            if (zip.Entries.Count > 10000 || zip.Entries.Sum(entry => entry.Length) > 300L * 1024 * 1024
                || zip.Entries.Any(entry => entry.Length > 50L * 1024 * 1024)
                || zip.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new PdfToolException("Office 文件展开后过大或包含重复条目，无法安全读取。");
            var budget = new TextBudget();
            IReadOnlyList<string> sections = extension.ToLowerInvariant() switch
            {
                ".docx" => ReadWord(zip, budget), ".xlsx" => ReadExcel(zip, budget), ".pptx" => ReadPowerPoint(zip, budget),
                _ => throw new PdfToolException("文字导入只支持 TXT、DOCX、XLSX 和 PPTX；不支持旧版或带宏格式。")
            };
            ValidateText(sections);
            return new ImportedText(sections, "仅提取文字/已保存的单元格值后重新排版；图片、图表、原版式、公式计算和外部资源不参与转换。");
        }
        catch (InvalidDataException) { throw new PdfToolException("无法打开 Office 文档。请使用未加密的 DOCX、XLSX 或 PPTX 文件。"); }
        catch (XmlException) { throw new PdfToolException("Office 文档的文字结构无效，或包含不允许的外部实体声明。"); }
        finally { if (memory.TryGetBuffer(out var buffer)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
    }

    private static void ValidateText(IReadOnlyList<string> sections)
    {
        if (sections.Count > 500 || sections.Sum(section => (long)section.Length) > MAX_TEXT)
            throw new PdfToolException("文字转换上限为 500 个分节、200 万个字符，请拆分文件后处理。");
        if (sections.All(string.IsNullOrWhiteSpace)) throw new PdfToolException("未找到可转换的文字。扫描图片、图表和加密内容不能通过文字导入转换。");
    }

    private static XDocument Xml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new PdfToolException("Office 文件缺少必要的文字结构。");
        if (entry.Length > 50L * 1024 * 1024) throw new PdfToolException("单项文档内容过大。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50L * 1024 * 1024, IgnoreComments = true });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static IReadOnlyList<string> ReadWord(ZipArchive zip, TextBudget budget)
    {
        var doc = Xml(zip, "word/document.xml");
        var paragraphs = doc.Descendants(W + "body").Elements().SelectMany(item => item.Name == W + "p" ? [item] : item.Descendants(W + "p"));
        var text = new StringBuilder();
        var first = true;
        foreach (var paragraph in paragraphs)
        {
            if (!first) budget.Append(text, "\n");
            first = false;
            foreach (var node in paragraph.Descendants().Where(node => !node.Ancestors(W + "del").Any()))
                budget.Append(text, node.Name == W + "t" ? node.Value : node.Name == W + "tab" ? "\t"
                    : node.Name == W + "br" || node.Name == W + "cr" ? "\n" : "");
        }
        return [text.ToString()];
    }

    private static Dictionary<string, string> InternalRelations(ZipArchive zip, string relationPath, string root)
    {
        var relations = Xml(zip, relationPath);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in relations.Root!.Elements())
        {
            if ((string?)item.Attribute("TargetMode") is string mode && mode != "Internal") continue;
            var id = (string?)item.Attribute("Id");
            var target = (string?)item.Attribute("Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target) || target.Contains('\\') || target.Contains(':')
                || target.Contains('%') || target.Contains('#') || target.Contains('?') || target.StartsWith("//", StringComparison.Ordinal)) continue;
            var components = new List<string>();
            var local = target.StartsWith('/') ? target[1..] : root + "/" + target;
            foreach (var component in local.Split('/'))
            {
                if (component is "" or ".") continue;
                if (component == "..") { if (components.Count == 0) throw new PdfToolException("Office 内部关系路径无效。"); components.RemoveAt(components.Count - 1); }
                else components.Add(component);
            }
            if (!result.TryAdd(id, string.Join('/', components))) throw new PdfToolException("Office 内部关系标识重复，无法确定文档内容。");
        }
        return result;
    }

    private static IReadOnlyList<string> ReadExcel(ZipArchive zip, TextBudget budget)
    {
        var sharedBudget = new TextBudget();
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml(zip, "xl/sharedStrings.xml").Descendants(S + "si")
            .Select(item => CollectText(item.Descendants(S + "t"), sharedBudget)).ToArray();
        var workbook = Xml(zip, "xl/workbook.xml");
        var relations = InternalRelations(zip, "xl/_rels/workbook.xml.rels", "xl");
        var sections = new List<string>();
        foreach (var sheet in workbook.Descendants(S + "sheet"))
        {
            if (sections.Count >= 500) throw new PdfToolException("文字转换一次最多支持 500 个分节。");
            if (!relations.TryGetValue((string?)sheet.Attribute(R + "id") ?? "", out var path)) throw new PdfToolException("有工作表需要外部资源，无法读取其文字。");
            var text = new StringBuilder();
            budget.Append(text, (string?)sheet.Attribute("name") ?? "");
            foreach (var row in Xml(zip, path).Descendants(S + "row"))
            {
                var cells = new SortedDictionary<int, string>();
                var nextColumn = 1;
                foreach (var cell in row.Elements(S + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? "";
                    var letters = Regex.Match(reference, "^[A-Za-z]{1,3}", RegexOptions.CultureInvariant).Value;
                    var column = 0;
                    foreach (var letter in letters) column = column * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
                    if (column == 0) column = nextColumn;
                    if (column > 16384) throw new PdfToolException("工作表列号超出范围。");
                    nextColumn = column + 1;
                    var value = cell.Element(S + "v")?.Value ?? "";
                    value = (string?)cell.Attribute("t") switch
                    {
                        "s" => int.TryParse(value, out var index) && index >= 0 && index < strings.Length ? strings[index] : throw new PdfToolException("工作表文字索引无效。"),
                        "inlineStr" => CollectText(cell.Descendants(S + "t"), new TextBudget()),
                        "b" => value == "1" ? "TRUE" : "FALSE", _ => value
                    };
                    if (!cells.TryAdd(column, value)) throw new PdfToolException("工作表中有重复的单元格列号。");
                }
                if (cells.Count == 0) continue;
                budget.Append(text, "\n");
                var previousColumn = 1;
                foreach (var (column, value) in cells)
                {
                    budget.AppendTabs(text, column - previousColumn);
                    budget.Append(text, value);
                    previousColumn = column;
                }
            }
            sections.Add(text.ToString());
        }
        return sections;
    }

    private static IReadOnlyList<string> ReadPowerPoint(ZipArchive zip, TextBudget budget)
    {
        var presentation = Xml(zip, "ppt/presentation.xml");
        var relations = InternalRelations(zip, "ppt/_rels/presentation.xml.rels", "ppt");
        var sections = new List<string>();
        foreach (var slide in presentation.Descendants(P + "sldId"))
        {
            if (sections.Count >= 500) throw new PdfToolException("文字转换一次最多支持 500 个分节。");
            if (!relations.TryGetValue((string?)slide.Attribute(R + "id") ?? "", out var path)) throw new PdfToolException("幻灯片内部关系缺失。");
            var text = new StringBuilder();
            var first = true;
            foreach (var paragraph in Xml(zip, path).Descendants(A + "p"))
            {
                if (!first) budget.Append(text, "\n");
                first = false;
                foreach (var node in paragraph.Descendants())
                    budget.Append(text, node.Name == A + "t" ? node.Value : node.Name == A + "br" ? "\n" : "");
            }
            sections.Add(text.ToString());
        }
        return sections;
    }

    private static string CollectText(IEnumerable<XElement> nodes, TextBudget budget)
    {
        var text = new StringBuilder();
        foreach (var node in nodes) budget.Append(text, node.Value);
        return text.ToString();
    }

    // 每次追加前检查整个输出的字符数；防止共享字符串、稀疏列号和重复幻灯片放大内存。
    private sealed class TextBudget
    {
        private int _length;

        private void Reserve(int length)
        {
            if (length > MAX_TEXT - _length)
                throw new PdfToolException("文字转换上限为 200 万个字符，请拆分文件后处理。");
            _length += length;
        }

        public void Append(StringBuilder output, ReadOnlySpan<char> value)
        {
            Reserve(value.Length);
            output.Append(value);
        }

        public void AppendTabs(StringBuilder output, int count)
        {
            Reserve(count);
            output.Append('\t', count);
        }
    }
}
