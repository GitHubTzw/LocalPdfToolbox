using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 文本变形：命名风格、大小写、行处理、文本统计与正则测试。
/// 全部在本机内存中完成，不写日志、不上传内容。
/// </summary>
public sealed class TextTransformTool : ITextTransformTool
{
    /// <summary>正则单次匹配的时间上限，避免灾难性回溯卡住界面。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private const int MaxMatches = 2000;

    public string ApplyNaming(string input, NamingStyle style)
    {
        var text = input ?? "";
        if (text.Length == 0) return "";
        // 标识符风格逐行处理，方便一次转换一列名称；整体大小写则作用于全文。
        return style switch
        {
            NamingStyle.UpperCase => text.ToUpperInvariant(),
            NamingStyle.LowerCase => text.ToLowerInvariant(),
            NamingStyle.TitleCase => ToTitleCase(text),
            _ => TransformLines(text, line => ConvertWords(line, style))
        };
    }

    public string ApplyLineOperation(string input, LineOperation operation, bool ignoreCase)
    {
        var text = input ?? "";
        if (text.Length == 0) return "";
        var trailingNewline = text.EndsWith('\n');
        var lines = SplitLines(text);
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        switch (operation)
        {
            case LineOperation.SortAscending:
                lines.Sort(comparer);
                break;
            case LineOperation.SortDescending:
                lines.Sort(comparer);
                lines.Reverse();
                break;
            case LineOperation.Deduplicate:
                var seen = new HashSet<string>(comparer);
                lines = lines.Where(seen.Add).ToList();
                break;
            case LineOperation.Reverse:
                lines.Reverse();
                break;
            case LineOperation.RemoveEmptyLines:
                lines = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
                break;
            case LineOperation.TrimLines:
                lines = lines.Select(line => line.Trim()).ToList();
                break;
            default:
                var width = lines.Count.ToString(CultureInfo.InvariantCulture).Length;
                lines = lines.Select((line, index) =>
                    (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width) + ". " + line).ToList();
                break;
        }
        var result = string.Join(Environment.NewLine, lines);
        return trailingNewline ? result + Environment.NewLine : result;
    }

    public string Describe(string input)
    {
        var text = input ?? "";
        var lines = SplitLines(text);
        var cjk = text.Count(IsCjk);
        var whitespace = text.Count(char.IsWhiteSpace);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var bytes = new UTF8Encoding(false).GetByteCount(text);
        var builder = new StringBuilder();
        builder.AppendLine($"字符数：{text.Length}");
        builder.AppendLine($"UTF-8 字节数：{bytes}");
        builder.AppendLine($"行数：{lines.Count}");
        builder.AppendLine($"非空行数：{lines.Count(line => !string.IsNullOrWhiteSpace(line))}");
        builder.AppendLine($"词数（按空白分隔）：{words}");
        builder.AppendLine($"中文字符数：{cjk}");
        builder.AppendLine($"空白字符数：{whitespace}");
        builder.AppendLine($"最长行长度：{(lines.Count == 0 ? 0 : lines.Max(line => line.Length))}");
        return builder.ToString().TrimEnd();
    }

    public RegexResult TestRegex(string input, string pattern, bool ignoreCase, bool multiline, bool singleline, int maxMatches)
    {
        if (string.IsNullOrEmpty(pattern)) throw new PdfToolException("请先输入正则表达式。");
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        if (multiline) options |= RegexOptions.Multiline;
        if (singleline) options |= RegexOptions.Singleline;
        var limit = maxMatches > 0 ? Math.Min(maxMatches, MaxMatches) : MaxMatches;

        Regex regex;
        try { regex = new Regex(pattern, options, RegexTimeout); }
        catch (ArgumentException error) { throw new PdfToolException("正则表达式无效：" + error.Message, error); }

        var matches = new List<RegexMatchInfo>();
        try
        {
            foreach (Match match in regex.Matches(input ?? ""))
            {
                matches.Add(new RegexMatchInfo(match.Index, match.Length, match.Value,
                    match.Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray()));
                if (matches.Count >= limit) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new RegexResult(false, "匹配超时，请简化正则或缩短输入。", matches, true);
        }

        var groups = regex.GetGroupNames().Skip(1).ToArray();
        var summary = matches.Count == 0
            ? "没有匹配项。"
            : $"匹配到 {matches.Count} 处" + (groups.Length > 0 ? $"；捕获组：{string.Join(", ", groups)}。" : "。");
        return new RegexResult(true, summary, matches, false);
    }

    private static string ConvertWords(string line, NamingStyle style)
    {
        var words = SplitWords(line);
        if (words.Count == 0) return line;
        var lowered = words.Select(word => word.ToLowerInvariant()).ToList();
        return style switch
        {
            NamingStyle.CamelCase => lowered[0] + string.Concat(lowered.Skip(1).Select(Capitalize)),
            NamingStyle.PascalCase => string.Concat(lowered.Select(Capitalize)),
            NamingStyle.SnakeCase => string.Join('_', lowered),
            NamingStyle.KebabCase => string.Join('-', lowered),
            NamingStyle.ConstantCase => string.Join('_', lowered.Select(word => word.ToUpperInvariant())),
            _ => line
        };
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>按非字母数字切词，同时把驼峰边界拆开，例如 userName → user Name。</summary>
    private static List<string> SplitWords(string line)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in line)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                Flush();
                continue;
            }
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

    private static string TransformLines(string text, Func<string, string> transform)
    {
        var trailingNewline = text.EndsWith('\n');
        var result = string.Join(Environment.NewLine, SplitLines(text).Select(transform));
        return trailingNewline ? result + Environment.NewLine : result;
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string ToTitleCase(string text)
    {
        var builder = new StringBuilder(text.Length);
        var startOfWord = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
                startOfWord = true;
                continue;
            }
            builder.Append(startOfWord ? char.ToUpperInvariant(ch) : ch);
            startOfWord = false;
        }
        return builder.ToString();
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF';
}
