using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 把一份 JSON 结构转成 C# / TypeScript / Java / Python / Go / SQL 的类型定义文本。
/// 只做文本生成，不调用任何外部编译器或解释器，也不联网。
/// </summary>
public sealed class CodeModelGenerator : ICodeModelGenerator
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };

    public string Generate(string json, CodeLanguage language, CodeModelOptions options)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new PdfToolException("请先输入 JSON 内容。");
        var settings = options ?? new CodeModelOptions();
        if (language == CodeLanguage.CSharp)
        {
            return new StructuredTextTool().ToCSharpClasses(json, new CSharpClassOptions
            {
                RootName = settings.RootName,
                Namespace = settings.Namespace,
                ClassSuffix = settings.ClassSuffix,
                StringLength = settings.StringLength
            });
        }

        var graph = ModelGraph.Build(json, settings);
        return language switch
        {
            CodeLanguage.TypeScript => WriteTypeScript(graph, settings),
            CodeLanguage.Java => WriteJava(graph, settings),
            CodeLanguage.Python => WritePython(graph, settings),
            CodeLanguage.Go => WriteGo(graph, settings),
            _ => WriteSql(graph, settings)
        };
    }

    // ------------------------------------------------------------ TypeScript

    private static string WriteTypeScript(ModelGraph graph, CodeModelOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// 由本地工具箱根据 JSON 生成；未使用的字段请自行删除。");
        if (graph.RootIsArray) builder.AppendLine($"// 顶层是数组：请使用 {graph.RootName}[]。");
        builder.AppendLine();
        foreach (var type in graph.Types)
        {
            builder.Append("export interface ").Append(type.Name).AppendLine(" {");
            foreach (var member in type.Members)
            {
                var optional = member.Nullable ? "?" : "";
                builder.Append("  ").Append(Naming.Camel(member.Name)).Append(optional).Append(": ")
                    .Append(TypeScriptType(member)).AppendLine(";");
            }
            builder.AppendLine("}");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string TypeScriptType(Member member)
    {
        var baseType = member.Kind switch
        {
            MemberKind.Nested => member.NestedName!,
            MemberKind.Number => "number",
            MemberKind.Boolean => "boolean",
            MemberKind.String => "string",
            _ => "unknown"
        };
        return member.IsArray ? baseType + "[]" : baseType;
    }

    // ------------------------------------------------------------ Java

    private static string WriteJava(ModelGraph graph, CodeModelOptions options)
    {
        var package = options.Namespace.Trim();
        var builder = new StringBuilder();
        if (package.Length > 0) builder.Append("package ").Append(package).AppendLine(";").AppendLine();
        var needsList = graph.Types.Any(type => type.Members.Any(member => member.IsArray));
        if (needsList) builder.AppendLine("import java.util.List;").AppendLine();
        foreach (var type in graph.Types)
        {
            builder.Append("public class ").Append(type.Name).AppendLine(" {").AppendLine();
            foreach (var member in type.Members)
            {
                builder.Append("    private ").Append(JavaType(member)).Append(' ')
                    .Append(Naming.Camel(member.Name)).AppendLine(";");
            }
            builder.AppendLine();
            foreach (var member in type.Members)
            {
                var property = Naming.Pascal(member.Name);
                var field = Naming.Camel(member.Name);
                builder.Append("    public ").Append(JavaType(member)).Append(" get").Append(property)
                    .AppendLine("() {").Append("        return ").Append(field).AppendLine(";").AppendLine("    }");
                builder.AppendLine();
                builder.Append("    public void set").Append(property).Append('(').Append(JavaType(member)).Append(' ')
                    .Append(field).AppendLine(") {").Append("        this.").Append(field).Append(" = ").Append(field)
                    .AppendLine(";").AppendLine("    }");
                builder.AppendLine();
            }
            builder.AppendLine("}");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string JavaType(Member member)
    {
        var baseType = member.Kind switch
        {
            MemberKind.Nested => member.NestedName!,
            MemberKind.Number => member.IsInteger ? "long" : "double",
            MemberKind.Boolean => "boolean",
            MemberKind.String => "String",
            _ => "Object"
        };
        return member.IsArray ? "List<" + Boxed(baseType) + ">" : baseType;
    }

    private static string Boxed(string type) => type switch
    {
        "long" => "Long",
        "double" => "Double",
        "boolean" => "Boolean",
        _ => type
    };

    // ------------------------------------------------------------ Python

    private static string WritePython(ModelGraph graph, CodeModelOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("from dataclasses import dataclass");
        builder.AppendLine("from typing import Any, List, Optional");
        builder.AppendLine();
        if (graph.RootIsArray) builder.AppendLine($"# 顶层是数组：请使用 List[{graph.RootName}]。").AppendLine();
        foreach (var type in graph.Types)
        {
            builder.AppendLine("@dataclass");
            builder.Append("class ").Append(type.Name).AppendLine(":");
            if (type.Members.Count == 0)
            {
                builder.AppendLine("    pass");
                builder.AppendLine();
                continue;
            }
            foreach (var member in type.Members)
                builder.Append("    ").Append(Naming.Snake(member.Name)).Append(": ").Append(PythonType(member)).AppendLine();
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string PythonType(Member member)
    {
        var baseType = member.Kind switch
        {
            MemberKind.Nested => member.NestedName!,
            MemberKind.Number => member.IsInteger ? "int" : "float",
            MemberKind.Boolean => "bool",
            MemberKind.String => "str",
            _ => "Any"
        };
        if (member.IsArray) baseType = "List[" + baseType + "]";
        return member.Nullable ? "Optional[" + baseType + "]" : baseType;
    }

    // ------------------------------------------------------------ Go

    private static string WriteGo(ModelGraph graph, CodeModelOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("package model");
        builder.AppendLine();
        if (graph.RootIsArray) builder.AppendLine($"// 顶层是数组：请使用 []{graph.RootName}。").AppendLine();
        foreach (var type in graph.Types)
        {
            builder.Append("type ").Append(type.Name).AppendLine(" struct {");
            foreach (var member in type.Members)
                builder.Append("\t").Append(Naming.Pascal(member.Name)).Append(' ').Append(GoType(member))
                    .Append(" `json:\"").Append(member.JsonName).Append("\"`").AppendLine();
            builder.AppendLine("}");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string GoType(Member member)
    {
        var baseType = member.Kind switch
        {
            MemberKind.Nested => member.NestedName!,
            MemberKind.Number => member.IsInteger ? "int64" : "float64",
            MemberKind.Boolean => "bool",
            MemberKind.String => "string",
            _ => "interface{}"
        };
        if (member.IsArray) baseType = "[]" + baseType;
        return member.Nullable ? "*" + baseType : baseType;
    }

    // ------------------------------------------------------------ SQL

    private static string WriteSql(ModelGraph graph, CodeModelOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- 由本地工具箱根据 JSON 生成；请按实际业务补充主键、索引与约束。");
        builder.AppendLine();
        foreach (var type in graph.Types)
        {
            builder.Append("CREATE TABLE ").Append(Naming.Snake(type.Name)).AppendLine(" (");
            var lines = new List<string> { "    id BIGINT NOT NULL PRIMARY KEY" };
            foreach (var member in type.Members)
                lines.Add("    " + Naming.Snake(member.Name) + " " + SqlType(member) + (member.Nullable ? "" : " NOT NULL"));
            builder.AppendLine(string.Join("," + Environment.NewLine, lines));
            builder.AppendLine(");");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string SqlType(Member member) => member.Kind switch
    {
        MemberKind.Nested => "TEXT",
        MemberKind.Number => member.IsInteger ? "BIGINT" : "DOUBLE",
        MemberKind.Boolean => "BOOLEAN",
        MemberKind.String => "TEXT",
        _ => "TEXT"
    };

    // ------------------------------------------------------------ 共用模型

    private enum MemberKind { String, Number, Boolean, Nested, Unknown }

    private sealed record Member(string JsonName, string Name, MemberKind Kind, bool IsArray, bool IsInteger, bool Nullable, string? NestedName);

    private sealed record TypeDef(string Name, List<Member> Members);

    /// <summary>把 JSON 结构归纳成一组类型定义，供各语言生成器共用。</summary>
    private sealed class ModelGraph
    {
        private readonly List<TypeDef> _types = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly string _suffix;

        public string RootName { get; private set; } = "Root";
        public bool RootIsArray { get; private set; }
        public IReadOnlyList<TypeDef> Types => _types;

        private ModelGraph(string suffix) => _suffix = suffix;

        public static ModelGraph Build(string json, CodeModelOptions options)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(json, nodeOptions: null, documentOptions: DocumentOptions); }
            catch (JsonException error) { throw new PdfToolException("JSON 语法错误：" + error.Message, error); }
            catch (ArgumentException) { throw new PdfToolException("JSON 中存在重复的属性名，无法生成类型。"); }
            if (root is null) throw new PdfToolException("JSON 内容为空，无法生成类型。");

            var graph = new ModelGraph(Naming.Identifier(options.ClassSuffix, ""));
            var rootBase = Naming.Identifier(options.RootName, "Root");
            var body = root switch
            {
                JsonObject value => value,
                JsonArray array => Merge(array.OfType<JsonObject>().ToList())
                    ?? throw new PdfToolException("顶层数组中没有对象，无法生成类型。"),
                _ => throw new PdfToolException("顶层不是对象或数组，无法生成类型。")
            };
            graph.RootIsArray = root is JsonArray;
            graph.RootName = graph.Register(rootBase, body);
            return graph;
        }

        private string Register(string suggested, JsonObject body)
        {
            var name = Unique(Naming.WithSuffix(suggested, _suffix));
            var members = new List<Member>();
            _types.Add(new TypeDef(name, members));
            foreach (var pair in body)
            {
                var memberName = Naming.Identifier(pair.Key, "Item");
                members.Add(Describe(pair.Key, memberName, pair.Value));
            }
            return name;
        }

        private Member Describe(string jsonName, string memberName, JsonNode? node)
        {
            switch (node)
            {
                case JsonObject nested:
                    return new Member(jsonName, memberName, MemberKind.Nested, false, false, false, Register(memberName, nested));
                case JsonArray array:
                    var objects = array.OfType<JsonObject>().ToList();
                    if (objects.Count > 0)
                        return new Member(jsonName, memberName, MemberKind.Nested, true, false, false,
                            Register(memberName, Merge(objects) ?? new JsonObject()));
                    var element = array.FirstOrDefault(item => item is not null);
                    var inner = Describe(jsonName, memberName, element);
                    return new Member(jsonName, memberName, inner.Kind, true, inner.IsInteger,
                        array.Any(item => item is null), inner.NestedName);
                case JsonValue scalar:
                    if (scalar.TryGetValue<bool>(out _)) return new Member(jsonName, memberName, MemberKind.Boolean, false, false, false, null);
                    if (scalar.TryGetValue<long>(out _)) return new Member(jsonName, memberName, MemberKind.Number, false, true, false, null);
                    if (scalar.TryGetValue<double>(out _)) return new Member(jsonName, memberName, MemberKind.Number, false, false, false, null);
                    if (scalar.TryGetValue<string>(out _)) return new Member(jsonName, memberName, MemberKind.String, false, false, false, null);
                    return new Member(jsonName, memberName, MemberKind.Unknown, false, false, false, null);
                default:
                    return new Member(jsonName, memberName, MemberKind.Unknown, false, false, true, null);
            }
        }

        private string Unique(string name)
        {
            var candidate = name;
            for (var suffix = 2; !_names.Add(candidate); suffix++)
                candidate = name + suffix.ToString(CultureInfo.InvariantCulture);
            return candidate;
        }

        /// <summary>数组里的多个对象合并成一个类型：键取并集，值取第一个非空样本。</summary>
        private static JsonObject? Merge(IReadOnlyList<JsonObject> objects)
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
                    if (item.TryGetPropertyValue(key, out var candidate) && candidate is not null)
                    {
                        value = candidate.DeepClone();
                        break;
                    }
                merged[key] = value;
            }
            return merged;
        }
    }

    /// <summary>各语言的标识符风格转换；只处理 ASCII 字母数字，其余字符作为分词边界。</summary>
    private static class Naming
    {
        public static string Identifier(string? raw, string fallback)
        {
            var words = Split(raw);
            if (words.Count == 0) return fallback;
            var name = string.Concat(words.Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
            if (char.IsDigit(name[0])) name = "_" + name;
            return Keywords.Contains(name) ? "_" + name : name;
        }

        public static string Pascal(string raw) => Identifier(raw, "Item");

        public static string Camel(string raw)
        {
            var name = Identifier(raw, "item");
            return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
        }

        public static string Snake(string raw)
        {
            var words = Split(raw);
            return words.Count == 0 ? "item" : string.Join('_', words.Select(word => word.ToLowerInvariant()));
        }

        public static string WithSuffix(string name, string suffix) =>
            suffix.Length == 0 || name.EndsWith(suffix, StringComparison.Ordinal) ? name : name + suffix;

        private static List<string> Split(string? raw)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            foreach (var ch in raw ?? "")
            {
                if (!char.IsAsciiLetterOrDigit(ch)) { Flush(); continue; }
                // 驼峰边界也算分词，这样 UserName → user_name、userName。
                if (current.Length > 0 && char.IsUpper(ch) && char.IsLower(current[^1])) Flush();
                current.Append(ch);
            }
            Flush();
            return words;

            void Flush()
            {
                if (current.Length == 0) return;
                words.Add(current.ToString());
                current.Clear();
            }
        }

        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Abstract", "As", "Base", "Bool", "Break", "Byte", "Case", "Catch", "Char", "Class", "Const", "Continue",
            "Default", "Delegate", "Do", "Double", "Else", "Enum", "Event", "Explicit", "Extern", "False", "Finally",
            "Fixed", "Float", "For", "Foreach", "Func", "Go", "Goto", "If", "Implicit", "Import", "In", "Int",
            "Interface", "Internal", "Is", "Lambda", "Lock", "Long", "Map", "Namespace", "New", "Null", "Object",
            "Operator", "Out", "Override", "Package", "Params", "Pass", "Private", "Protected", "Public", "Range",
            "Readonly", "Ref", "Return", "Select", "Short", "Static", "String", "Struct", "Switch", "This", "Throw",
            "True", "Try", "Type", "Using", "Var", "Virtual", "Void", "While", "With", "Yield"
        };
    }
}
