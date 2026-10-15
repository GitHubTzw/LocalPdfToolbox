using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Xml;
using System.Xml.Linq;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>JSON / XML 的本地格式化、压缩与校验；不访问网络，XML 禁止 DTD 和外部实体。</summary>
public sealed class StructuredTextTool : IStructuredTextTool
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };
    // 宽松编码器：中文与引号按原样输出，只做 JSON 必需的最小转义。
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public FormatResult Format(string input, StructuredFormat format, FormatOptions options) => format switch
    {
        StructuredFormat.Json => FormatJson(input, options, indented: true),
        _ => FormatXml(input, indented: true)
    };

    public FormatResult Minify(string input, StructuredFormat format) => format switch
    {
        StructuredFormat.Json => FormatJson(input, new FormatOptions(), indented: false),
        _ => FormatXml(input, indented: false)
    };

    public ValidationResult Validate(string input, StructuredFormat format)
    {
        if (string.IsNullOrWhiteSpace(input)) return new ValidationResult(false, "内容为空。");
        if (format == StructuredFormat.Json)
        {
            try
            {
                using var document = JsonDocument.Parse(input, DocumentOptions);
                return new ValidationResult(true, "JSON 语法有效；根节点：" + Describe(document.RootElement) + "。");
            }
            catch (JsonException error)
            {
                return new ValidationResult(false, "JSON 语法错误：" + error.Message,
                    (int)(error.LineNumber ?? 0) + 1, (int)(error.BytePositionInLine ?? 0) + 1);
            }
        }
        try
        {
            var document = LoadXml(input);
            return new ValidationResult(true, "XML 语法有效；根元素：" + (document.Root?.Name.LocalName ?? "（空）") + "。");
        }
        catch (PdfToolException error) { return new ValidationResult(false, error.Message); }
    }

    public string EscapeJsonString(string input)
    {
        var encoded = JsonSerializer.Serialize(input ?? "");
        return encoded.Length >= 2 ? encoded[1..^1] : encoded;
    }

    public string UnescapeJsonString(string input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return "";
        var quoted = value.Length >= 2 && value[0] == '"' && value[^1] == '"';
        try { return JsonSerializer.Deserialize<string>(quoted ? value : "\"" + value + "\"") ?? ""; }
        catch (JsonException error) { throw new PdfToolException("转义文本无法还原为普通字符串：" + error.Message, error); }
    }

    /// <summary>
    /// 按 CRTVUP.BrightBook 项目的模型风格生成 C# 类：块级命名空间、Model 后缀、
    /// 类和属性都带 summary 注释、字符串属性可选 StringLength 特性。
    /// </summary>
    public string ToCSharpClasses(string json, CSharpClassOptions options)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new PdfToolException("请先输入 JSON 内容。");
        var settings = options ?? new CSharpClassOptions();
        var suffix = NormalizeSuffix(settings.ClassSuffix);
        var namespaceName = NormalizeNamespace(settings.Namespace);
        var rootBase = ToIdentifier(settings.RootName, "Root");

        JsonNode? root;
        try { root = JsonNode.Parse(json, nodeOptions: null, documentOptions: DocumentOptions); }
        catch (JsonException error) { throw new PdfToolException("JSON 语法错误：" + error.Message, error); }
        catch (ArgumentException) { throw new PdfToolException("JSON 中存在重复的属性名，无法生成类。"); }
        if (root is null) throw new PdfToolException("JSON 内容为空，无法生成类。");

        var graph = new ClassGraph(suffix);
        var rootClassName = graph.Register(rootBase, rootBase, root switch
        {
            JsonObject value => value,
            JsonArray array => MergeObjects(array.OfType<JsonObject>().ToList())
                ?? throw new PdfToolException("顶层数组中没有对象，无法生成类。"),
            _ => throw new PdfToolException("顶层不是对象或数组，无法生成类。")
        });

        // 命名空间内的成员整体缩进一级；类体再缩进一级。
        var indent = namespaceName is null ? "" : "    ";
        var classes = new StringBuilder();
        var needsList = false;
        var needsStringLength = false;
        var needsJsonName = false;
        for (var index = 0; index < graph.Count; index++)
        {
            var info = graph.At(index);
            if (index > 0) classes.AppendLine();
            classes.Append(indent).Append("/// <summary>").AppendLine();
            classes.Append(indent).Append("/// ").AppendLine(info.JsonKey);
            classes.Append(indent).AppendLine("/// </summary>");
            classes.Append(indent).Append("public class ").Append(info.Name).AppendLine();
            classes.Append(indent).AppendLine("{");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            var first = true;
            foreach (var pair in graph.Body(info.Name))
            {
                var propertyName = Unique(ToIdentifier(pair.Key, "Item"), properties);
                var type = graph.TypeOf(pair.Value, propertyName, pair.Key);
                if (!first) classes.AppendLine();
                first = false;
                if (type.StartsWith("List<", StringComparison.Ordinal)) needsList = true;
                classes.Append(indent).Append("    /// <summary>").AppendLine();
                classes.Append(indent).Append("    /// ").AppendLine(pair.Key);
                classes.Append(indent).AppendLine("    /// </summary>");
                if (pair.Key != propertyName)
                {
                    needsJsonName = true;
                    classes.Append(indent).Append("    [JsonPropertyName(\"").Append(EscapeAttribute(pair.Key)).AppendLine("\")]");
                }
                if (settings.StringLength > 0 && type == "string")
                {
                    needsStringLength = true;
                    var length = settings.StringLength.ToString(CultureInfo.InvariantCulture);
                    classes.Append(indent).Append("    [StringLength(").Append(length)
                        .Append(", ErrorMessage = \"").Append(propertyName).Append("不能超过").Append(length).AppendLine("个字符\")]");
                }
                classes.Append(indent).Append("    public ").Append(type).Append(' ').Append(propertyName).AppendLine(" { get; set; }");
            }
            classes.Append(indent).AppendLine("}");
        }

        var builder = new StringBuilder();
        if (needsList) builder.AppendLine("using System.Collections.Generic;");
        if (needsStringLength) builder.AppendLine("using System.ComponentModel.DataAnnotations;");
        if (needsJsonName) builder.AppendLine("using System.Text.Json.Serialization;");
        if (builder.Length > 0) builder.AppendLine();
        if (root is JsonArray) builder.AppendLine($"// 顶层是数组：请使用 List<{rootClassName}>。");
        if (namespaceName is null)
        {
            builder.Append(classes);
        }
        else
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine();
            builder.AppendLine("{");
            builder.Append(classes);
            builder.AppendLine("}");
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>类名后缀；留空表示不加后缀，非法字符按分词规则处理。</summary>
    private static string NormalizeSuffix(string? raw)
    {
        var value = (raw ?? "").Trim();
        return value.Length == 0 ? "" : ToIdentifier(value, "");
    }

    /// <summary>命名空间；留空表示不生成命名空间，格式错误时给出明确提示。</summary>
    private static string? NormalizeNamespace(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return null;
        foreach (var part in value.Split('.'))
            if (!IsIdentifier(part))
                throw new PdfToolException("命名空间格式不正确，请使用类似 Company.Product.Models 的点分名称，或留空。");
        return value;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0) return false;
        if (!char.IsLetter(value[0]) && value[0] != '_') return false;
        return value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>追加类名后缀；已经带后缀时不再重复追加。</summary>
    private static string WithSuffix(string name, string suffix) =>
        suffix.Length == 0 || name.EndsWith(suffix, StringComparison.Ordinal) ? name : name + suffix;

    private static FormatResult FormatJson(string input, FormatOptions options, bool indented)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new PdfToolException("请先输入 JSON 内容。");
        var writerOptions = new JsonWriterOptions
        {
            Indented = indented,
            IndentCharacter = options.UseTabs ? '\t' : ' ',
            IndentSize = Math.Clamp(options.IndentSize, 1, 8),
            Encoder = Encoder,
            SkipValidation = true
        };
        using var buffer = new MemoryStream();
        if (options.SortKeys && indented)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(input, nodeOptions: null, documentOptions: DocumentOptions); }
            catch (JsonException error) { throw new PdfToolException("JSON 语法错误：" + error.Message, error); }
            catch (ArgumentException) { throw new PdfToolException("JSON 中存在重复的属性名，无法排序输出。"); }
            if (node is null) throw new PdfToolException("JSON 内容为空。");
            using (var writer = new Utf8JsonWriter(buffer, writerOptions)) SortNode(node).WriteTo(writer);
            return new FormatResult(Encoding.UTF8.GetString(buffer.ToArray()), "已按属性名排序并格式化。");
        }
        using (var document = ParseJson(input))
        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
            document.WriteTo(writer);
        return new FormatResult(Encoding.UTF8.GetString(buffer.ToArray()), indented ? "已格式化。" : "已压缩为单行。");
    }

    private static FormatResult FormatXml(string input, bool indented)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new PdfToolException("请先输入 XML 内容。");
        var document = LoadXml(input);
        var text = indented ? document.ToString() : document.ToString(SaveOptions.DisableFormatting);
        return new FormatResult(text, indented ? "已格式化（XML 固定使用 2 空格缩进）。" : "已压缩为单行。");
    }

    private static JsonDocument ParseJson(string input)
    {
        try { return JsonDocument.Parse(input, DocumentOptions); }
        catch (JsonException error) { throw new PdfToolException("JSON 语法错误：" + error.Message, error); }
    }

    private static XDocument LoadXml(string input)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            CloseInput = true
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(input), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException error)
        {
            throw new PdfToolException($"XML 语法错误（第 {error.LineNumber} 行第 {error.LinePosition} 列）：{error.Message}", error);
        }
    }

    private static JsonNode SortNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject source:
                var sorted = new JsonObject();
                foreach (var pair in source.OrderBy(item => item.Key, StringComparer.Ordinal))
                    sorted[pair.Key] = pair.Value is null ? null : SortNode(pair.Value);
                return sorted;
            case JsonArray sequence:
                var result = new JsonArray();
                foreach (var item in sequence) result.Add(item is null ? null : SortNode(item));
                return result;
            default:
                return node.DeepClone();
        }
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => $"对象（{element.EnumerateObject().Count()} 个属性）",
        JsonValueKind.Array => $"数组（{element.GetArrayLength()} 项）",
        JsonValueKind.String => "字符串",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔值",
        _ => "null"
    };

    private static JsonObject? MergeObjects(IReadOnlyList<JsonObject> objects)
    {
        if (objects.Count == 0) return null;
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in objects)
            foreach (var pair in item)
                if (seen.Add(pair.Key)) keys.Add(pair.Key);
        var merged = new JsonObject();
        foreach (var key in keys)
        {
            JsonNode? value = null;
            foreach (var item in objects)
                if (item.TryGetPropertyValue(key, out var candidate) && candidate is not null) { value = candidate.DeepClone(); break; }
            merged[key] = value;
        }
        return merged;
    }

    private static string ToIdentifier(string? raw, string fallback)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var ch in raw ?? "")
        {
            // 下划线、连字符、空格等一律作为分词边界，生成 PascalCase 标识符。
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(upper ? char.ToUpperInvariant(ch) : ch);
                upper = false;
            }
            else upper = true;
        }
        if (builder.Length == 0) return fallback;
        if (char.IsDigit(builder[0])) builder.Insert(0, '_');
        var name = builder.ToString();
        return Keywords.Contains(name) ? "_" + name : name;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var suffix = 2; !used.Add(candidate); suffix++)
            candidate = name + suffix.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static string EscapeAttribute(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "Abstract", "As", "Base", "Bool", "Break", "Byte", "Case", "Catch", "Char", "Checked", "Class", "Const",
        "Continue", "Decimal", "Default", "Delegate", "Do", "Double", "Else", "Enum", "Event", "Explicit", "Extern",
        "False", "Finally", "Fixed", "Float", "For", "Foreach", "Goto", "If", "Implicit", "In", "Int", "Interface",
        "Internal", "Is", "Lock", "Long", "Namespace", "New", "Null", "Object", "Operator", "Out", "Override",
        "Params", "Private", "Protected", "Public", "Readonly", "Ref", "Return", "Sbyte", "Sealed", "Short",
        "Sizeof", "Stackalloc", "Static", "String", "Struct", "Switch", "This", "Throw", "True", "Try", "Typeof",
        "Uint", "Ulong", "Unchecked", "Unsafe", "Ushort", "Using", "Virtual", "Void", "Volatile", "While"
    };

    /// <summary>生成结果里的一个类：唯一类名加它来自的 JSON 字段名（用于注释）。</summary>
    private sealed record ClassInfo(string Name, string JsonKey);

    /// <summary>按出现顺序收集生成的类，类名在本文件内唯一。</summary>
    private sealed class ClassGraph
    {
        private readonly Dictionary<string, JsonObject> _bodies = new(StringComparer.Ordinal);
        private readonly List<ClassInfo> _order = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly string _suffix;

        public ClassGraph(string suffix) => _suffix = suffix;

        public int Count => _order.Count;
        public ClassInfo At(int index) => _order[index];
        public JsonObject Body(string name) => _bodies[name];

        public string Register(string suggested, string jsonKey, JsonObject body)
        {
            var name = Unique(WithSuffix(suggested, _suffix), _names);
            _bodies[name] = body;
            _order.Add(new ClassInfo(name, jsonKey));
            return name;
        }

        public string TypeOf(JsonNode? node, string suggested, string jsonKey)
        {
            switch (node)
            {
                case null: return "object";
                case JsonObject body: return Register(suggested, jsonKey, body);
                case JsonArray items:
                    var objects = items.OfType<JsonObject>().ToList();
                    if (objects.Count > 0)
                        return "List<" + Register(suggested, jsonKey, MergeObjects(objects) ?? new JsonObject()) + ">";
                    var types = items.Select(item => TypeOf(item, suggested + "Item", jsonKey))
                        .Distinct(StringComparer.Ordinal).ToList();
                    return types.Count == 1 ? "List<" + types[0] + ">" : "List<object>";
                case JsonValue scalar:
                    if (scalar.TryGetValue<bool>(out _)) return "bool";
                    if (scalar.TryGetValue<long>(out _)) return "long";
                    if (scalar.TryGetValue<double>(out _)) return "double";
                    if (scalar.TryGetValue<string>(out _)) return "string";
                    return "object";
                default: return "object";
            }
        }
    }
}
