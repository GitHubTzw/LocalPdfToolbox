using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using System.Xml;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// JSON / XML / CSV 的本地互转、路径查询与结构统计。
/// 解析全部在本机内存中完成；XML 禁止 DTD 与外部实体，不访问网络。
/// </summary>
public sealed class StructuredTextConverter : IStructuredTextConverter
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 宽松编码器：中文与引号按原样输出，只做 JSON 必需的最小转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>单次转换允许的最大 JSON 节点数，避免异常输入拖垮界面。</summary>
    private const int MaxNodes = 200_000;

    public ConversionText JsonToXml(string json)
    {
        var root = ParseNode(json);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null));
        document.Add(ToXml("root", root));
        return new ConversionText(document.ToString(),
            "已把 JSON 转成 XML；对象属性用同名元素，数组元素统一命名为 item。");
    }

    public ConversionText XmlToJson(string xml)
    {
        var document = LoadXml(xml);
        var element = document.Root ?? throw new PdfToolException("XML 没有根元素，无法转换。");
        var node = ToJson(element);
        return new ConversionText(Serialize(node), $"已把 XML 根元素 <{element.Name.LocalName}> 转成 JSON。");
    }

    public ConversionText JsonToCsv(string json)
    {
        var root = ParseNode(json);
        if (root is not JsonArray array) throw new PdfToolException("只有顶层是数组的 JSON 才能转成 CSV。");
        var rows = array.OfType<JsonObject>().ToList();
        if (rows.Count == 0) throw new PdfToolException("数组里没有对象，无法转成 CSV。");

        var headers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            foreach (var pair in row)
                if (seen.Add(pair.Key)) headers.Add(pair.Key);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            for (var index = 0; index < headers.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(EscapeCsv(ScalarText(row[headers[index]])));
            }
            builder.AppendLine();
        }
        return new ConversionText(builder.ToString(),
            $"已把 {rows.Count} 行对象转成 CSV，共 {headers.Count} 列；嵌套结构按原样文本写出。");
    }

    public ConversionText CsvToJson(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new PdfToolException("请先输入 CSV 内容。");
        var rows = ParseCsv(csv);
        if (rows.Count == 0) throw new PdfToolException("CSV 内容为空，无法转换。");
        var headers = rows[0];
        if (headers.Count == 0) throw new PdfToolException("CSV 第一行是表头，不能为空。");

        var array = new JsonArray();
        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            var item = new JsonObject();
            for (var column = 0; column < headers.Count; column++)
                item[headers[column]] = column < row.Count ? JsonValue.Create(row[column]) : null;
            array.Add(item);
        }
        return new ConversionText(Serialize(array),
            $"已把 {rows.Count - 1} 行 CSV 转成 JSON 数组；所有值都按字符串处理，请按需改类型。");
    }

    public ConversionText Query(string json, string path)
    {
        var node = ParseNode(json);
        var tokens = ParsePath(path);
        if (tokens.Count == 0) return new ConversionText(Serialize(node), "路径为空，已返回完整文档。");

        var results = new List<JsonNode?> { node };
        foreach (var token in tokens)
        {
            var next = new List<JsonNode?>();
            foreach (var current in results)
            {
                switch (token)
                {
                    case PathToken.Property property when current is JsonObject container:
                        if (property.Name == "*")
                            next.AddRange(container.Select(pair => pair.Value));
                        else if (container.TryGetPropertyValue(property.Name, out var value))
                            next.Add(value);
                        break;
                    case PathToken.Index index when current is JsonArray sequence:
                        var position = index.Value < 0 ? sequence.Count + index.Value : index.Value;
                        if (position >= 0 && position < sequence.Count) next.Add(sequence[position]);
                        break;
                }
            }
            results = next;
            if (results.Count == 0) throw new PdfToolException($"路径 {path} 在当前位置没有匹配的节点。");
        }

        var output = results.Count == 1 ? Serialize(results[0]) : Serialize(new JsonArray(results.ToArray()));
        return new ConversionText(output, $"路径 {path} 命中 {results.Count} 个节点。");
    }

    public ConversionText Describe(string json)
    {
        var node = ParseNode(json);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodes = 0;
        var depth = 0;
        Walk(node, 1);
        var text = new StringBuilder();
        text.AppendLine($"节点总数：{nodes}");
        text.AppendLine($"最大深度：{depth}");
        text.AppendLine($"文本长度：{json.Length} 字符");
        text.AppendLine("类型分布：");
        foreach (var pair in counts.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal))
            text.AppendLine($"  {pair.Key}：{pair.Value}");
        return new ConversionText(text.ToString(), $"已统计 {nodes} 个节点，最大深度 {depth}。");

        void Walk(JsonNode? current, int level)
        {
            if (++nodes > MaxNodes) throw new PdfToolException($"节点数超过 {MaxNodes}，请先裁剪内容再统计。");
            depth = Math.Max(depth, level);
            switch (current)
            {
                case JsonObject container:
                    Bump("对象");
                    foreach (var pair in container) Walk(pair.Value, level + 1);
                    break;
                case JsonArray sequence:
                    Bump("数组");
                    foreach (var item in sequence) Walk(item, level + 1);
                    break;
                case null:
                    Bump("null");
                    break;
                default:
                    Bump(ScalarKind(current));
                    break;
            }
        }

        void Bump(string kind) => counts[kind] = counts.TryGetValue(kind, out var value) ? value + 1 : 1;
    }

    public ConversionText DeduplicateArray(string json, bool ignoreCase)
    {
        var array = RequireArray(json);
        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var item in array)
        {
            // 标量按文本比较，这样“忽略大小写”才有效；对象与数组仍按完整 JSON 文本比较。
            var key = item is null or JsonValue ? ScalarText(item) : item.ToJsonString();
            if (seen.Add(key)) result.Add(item?.DeepClone());
        }
        var removed = array.Count - result.Count;
        return new ConversionText(Serialize(result),
            removed == 0 ? "数组没有重复项。" : $"已去掉 {removed} 个重复项，剩余 {result.Count} 项。");
    }

    public ConversionText SortArray(string json, bool descending)
    {
        var array = RequireArray(json);
        var items = array.ToList();
        items.Sort(CompareNodes);
        if (descending) items.Reverse();
        var result = new JsonArray();
        foreach (var item in items) result.Add(item?.DeepClone());
        return new ConversionText(Serialize(result), $"已按值排序 {result.Count} 项（数字优先，其次文本）。");
    }

    /// <summary>排序规则：null 最小，数字按数值，其余按不区分大小写的文本。</summary>
    private static int CompareNodes(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null ? (right is null ? 0 : -1) : 1;
        if (left is JsonValue leftValue && right is JsonValue rightValue)
        {
            if (leftValue.TryGetValue<double>(out var leftNumber) && rightValue.TryGetValue<double>(out var rightNumber))
                return leftNumber.CompareTo(rightNumber);
        }
        return string.Compare(ScalarText(left), ScalarText(right), StringComparison.OrdinalIgnoreCase);
    }

    private JsonArray RequireArray(string json)
    {
        var root = ParseNode(json);
        return root as JsonArray ?? throw new PdfToolException("只有顶层是数组的 JSON 才能做这项整理。");
    }

    private static JsonNode ParseNode(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new PdfToolException("请先输入 JSON 内容。");
        try
        {
            return JsonNode.Parse(json, nodeOptions: null, documentOptions: DocumentOptions)
                ?? throw new PdfToolException("JSON 内容为空。");
        }
        catch (JsonException error) { throw new PdfToolException("JSON 语法错误：" + error.Message, error); }
        catch (ArgumentException) { throw new PdfToolException("JSON 中存在重复的属性名，无法处理。"); }
    }

    private static XDocument LoadXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new PdfToolException("请先输入 XML 内容。");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            CloseInput = true
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException error)
        {
            throw new PdfToolException($"XML 语法错误（第 {error.LineNumber} 行第 {error.LinePosition} 列）：{error.Message}", error);
        }
    }

    private static string Serialize(JsonNode? node) =>
        (node?.ToJsonString(WriteOptions) ?? "null") + Environment.NewLine;

    private static XElement ToXml(string name, JsonNode? node)
    {
        var element = new XElement(SafeName(name));
        switch (node)
        {
            case JsonObject container:
                foreach (var pair in container) element.Add(ToXml(pair.Key, pair.Value));
                break;
            case JsonArray sequence:
                foreach (var item in sequence) element.Add(ToXml("item", item));
                break;
            case null:
                element.SetAttributeValue("nil", "true");
                break;
            default:
                element.Value = ScalarText(node);
                break;
        }
        return element;
    }

    private static JsonNode? ToJson(XElement element)
    {
        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            if (element.Attribute("nil")?.Value == "true") return null;
            return JsonValue.Create(Scalar(element.Value));
        }

        // 同名子元素重复出现时视为数组，这是 XML 表达列表的常见写法。
        var names = children.Select(child => child.Name).Distinct().ToList();
        if (names.Count == 1 && children.Count > 1)
        {
            var array = new JsonArray();
            foreach (var child in children) array.Add(ToJson(child));
            return array;
        }

        var result = new JsonObject();
        foreach (var child in children)
        {
            var name = child.Name.LocalName;
            var value = ToJson(child);
            if (result.TryGetPropertyValue(name, out var existing))
            {
                if (existing is JsonArray list) list.Add(value);
                else result[name] = new JsonArray(existing?.DeepClone(), value);
            }
            else result[name] = value;
        }
        return result;
    }

    /// <summary>XML 文本回读时尝试还原数字与布尔值，其余保持字符串。</summary>
    private static object Scalar(string value)
    {
        if (value.Length == 0) return "";
        if (bool.TryParse(value, out var flag)) return flag;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return value;
    }

    private static string ScalarText(JsonNode? node)
    {
        if (node is not JsonValue value) return node?.ToJsonString() ?? "";
        if (value.TryGetValue<string>(out var text)) return text;
        return node.ToJsonString();
    }

    private static string ScalarKind(JsonNode node)
    {
        if (node is not JsonValue value) return "其他";
        if (value.TryGetValue<bool>(out _)) return "布尔值";
        if (value.TryGetValue<string>(out _)) return "字符串";
        if (value.TryGetValue<long>(out _) || value.TryGetValue<double>(out _)) return "数字";
        return "其他";
    }

    private static string SafeName(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        var name = builder.ToString();
        if (name.Length == 0) return "item";
        // XML 名称必须以字母或下划线开头，数字开头的键名前面补一个下划线。
        return char.IsAsciiLetter(name[0]) || name[0] == '_' ? name : "_" + name;
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var ch = csv[index];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"') { field.Append('"'); index++; }
                    else quoted = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        // 去掉末尾完全空的一行，兼容以换行结尾的文件。
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static List<PathToken> ParsePath(string path)
    {
        var tokens = new List<PathToken>();
        var text = (path ?? "").Trim().TrimStart('$').TrimStart('.');
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == '.') { index++; continue; }
            if (ch == '[')
            {
                var end = text.IndexOf(']', index);
                if (end < 0) throw new PdfToolException("路径里的方括号没有闭合。");
                var inner = text[(index + 1)..end].Trim().Trim('\'', '"');
                if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
                    tokens.Add(new PathToken.Index(position));
                else tokens.Add(new PathToken.Property(inner));
                index = end + 1;
                continue;
            }
            var stop = text.IndexOfAny(['.', '['], index);
            var name = stop < 0 ? text[index..] : text[index..stop];
            if (name.Length > 0) tokens.Add(new PathToken.Property(name));
            index = stop < 0 ? text.Length : stop;
        }
        return tokens;
    }

    private abstract record PathToken
    {
        public sealed record Property(string Name) : PathToken;
        public sealed record Index(int Value) : PathToken;
    }
}
