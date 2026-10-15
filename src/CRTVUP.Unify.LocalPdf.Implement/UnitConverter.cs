using System.Globalization;
using System.Numerics;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 本地换算工具：进制、颜色、URL 拆解与 GUID 生成。
/// URL 只做字符串分析，绝不发起任何请求。
/// </summary>
public sealed class UnitConverter : IUnitConverter
{
    private static readonly Dictionary<char, int> Digits = BuildDigits();

    public string ConvertBase(string input, int fromBase, int toBase)
    {
        if (fromBase is not (2 or 8 or 10 or 16) || toBase is not (2 or 8 or 10 or 16))
            throw new PdfToolException("进制只支持 2、8、10、16 四种。");
        var text = (input ?? "").Trim();
        if (text.Length == 0) throw new PdfToolException("请先输入要转换的数值。");
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+')) text = text[1..];
        text = StripPrefix(text, fromBase);
        if (text.Length == 0) throw new PdfToolException("请先输入要转换的数值。");

        var value = BigInteger.Zero;
        foreach (var ch in text)
        {
            if (ch == '_') continue;
            if (!Digits.TryGetValue(char.ToUpperInvariant(ch), out var digit) || digit >= fromBase)
                throw new PdfToolException($"“{ch}”不是合法的 {fromBase} 进制数字。");
            value = value * fromBase + digit;
        }
        if (negative) value = -value;

        var output = ToBase(value, toBase);
        return $"输入（{fromBase} 进制）：{text}{Environment.NewLine}"
            + $"输出（{toBase} 进制）：{output}{Environment.NewLine}"
            + $"十进制：{value.ToString(CultureInfo.InvariantCulture)}";
    }

    public string ConvertColor(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) throw new PdfToolException("请先输入颜色值，例如 #1E88E5 或 rgb(30,136,229)。");
        var (red, green, blue) = ParseColor(text);
        var (hue, saturation, lightness) = ToHsl(red, green, blue);
        var shorthand = red >> 4 == red % 16 && green >> 4 == green % 16 && blue >> 4 == blue % 16
            ? $"#{red & 0xF:X1}{green & 0xF:X1}{blue & 0xF:X1}"
            : "不适用（各通道高低位不一致）";
        return $"HEX：#{red:X2}{green:X2}{blue:X2}{Environment.NewLine}"
            + $"HEX（简写）：{shorthand}{Environment.NewLine}"
            + $"RGB：rgb({red}, {green}, {blue}){Environment.NewLine}"
            + $"HSL：hsl({hue:0.#}, {saturation:0.#}%, {lightness:0.#}%){Environment.NewLine}"
            + $"亮度参考：{((red * 299 + green * 587 + blue * 114) / 1000 >= 128 ? "浅色底，适合深色文字" : "深色底，适合浅色文字")}";
    }

    public string DescribeUrl(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) throw new PdfToolException("请先输入要拆解的网址。");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new PdfToolException("无法识别为完整网址，请包含协议部分，例如 https://example.com/path?q=1。");

        var builder = new StringBuilder();
        builder.AppendLine($"协议：{uri.Scheme}");
        builder.AppendLine($"主机：{uri.Host}");
        builder.AppendLine($"端口：{(uri.IsDefaultPort ? "默认" : uri.Port.ToString(CultureInfo.InvariantCulture))}");
        builder.AppendLine($"路径：{(uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath)}");
        builder.AppendLine($"查询串：{(uri.Query.Length == 0 ? "（无）" : uri.Query.TrimStart('?'))}");
        builder.AppendLine($"锚点：{(uri.Fragment.Length == 0 ? "（无）" : uri.Fragment.TrimStart('#'))}");
        builder.AppendLine($"是否默认端口：{(uri.IsDefaultPort ? "是" : "否")}");
        var parameters = ParseQuery(uri.Query);
        builder.AppendLine($"查询参数：{parameters.Count} 个");
        foreach (var pair in parameters) builder.AppendLine($"  {pair.Key} = {pair.Value}");
        return builder.ToString().TrimEnd();
    }

    public string GenerateGuids(int count, bool upperCase, bool braces, bool noHyphen)
    {
        var total = Math.Clamp(count <= 0 ? 1 : count, 1, 500);
        var builder = new StringBuilder();
        for (var index = 0; index < total; index++)
        {
            var text = Guid.NewGuid().ToString(noHyphen ? "N" : "D");
            if (upperCase) text = text.ToUpperInvariant();
            if (braces) text = "{" + text + "}";
            builder.AppendLine(text);
        }
        return builder.ToString().TrimEnd();
    }

    private static string StripPrefix(string text, int fromBase)
    {
        if (text.Length < 2 || text[0] != '0') return text;
        var marker = char.ToLowerInvariant(text[1]);
        return (fromBase, marker) switch
        {
            (16, 'x') => text[2..],
            (2, 'b') => text[2..],
            (8, 'o') => text[2..],
            _ => text
        };
    }

    private static string ToBase(BigInteger value, int toBase)
    {
        if (value.IsZero) return "0";
        var negative = value.Sign < 0;
        if (negative) value = -value;
        var builder = new StringBuilder();
        while (!value.IsZero)
        {
            value = BigInteger.DivRem(value, toBase, out var remainder);
            builder.Insert(0, "0123456789ABCDEF"[(int)remainder]);
        }
        if (negative) builder.Insert(0, '-');
        return builder.ToString();
    }

    private static (byte Red, byte Green, byte Blue) ParseColor(string text)
    {
        var value = text.ToLowerInvariant();
        if (value.StartsWith("rgb", StringComparison.Ordinal))
        {
            var inner = Slice(value, '(', ')');
            var parts = inner.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) throw new PdfToolException("RGB 写法需要三个分量，例如 rgb(30, 136, 229)。");
            return (Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
        }
        if (value.StartsWith("hsl", StringComparison.Ordinal))
        {
            var inner = Slice(value, '(', ')');
            var parts = inner.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) throw new PdfToolException("HSL 写法需要三个分量，例如 hsl(210, 76%, 51%)。");
            var hue = Number(parts[0].TrimEnd('d', 'e', 'g'));
            var saturation = Number(parts[1].TrimEnd('%')) / 100d;
            var lightness = Number(parts[2].TrimEnd('%')) / 100d;
            return FromHsl(hue, Math.Clamp(saturation, 0, 1), Math.Clamp(lightness, 0, 1));
        }

        var hex = value.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => new string(ch, 2)));
        if (hex.Length == 4) hex = string.Concat(hex[1..].Select(ch => new string(ch, 2)));
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6) throw new PdfToolException("颜色格式无法识别，请使用 #RRGGBB、rgb(...) 或 hsl(...)。");
        try
        {
            return (byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        catch (FormatException error) { throw new PdfToolException("颜色里的十六进制字符无效。", error); }
    }

    private static string Slice(string value, char open, char close)
    {
        var start = value.IndexOf(open);
        var end = value.LastIndexOf(close);
        if (start < 0 || end <= start) throw new PdfToolException("颜色写法缺少括号，例如 rgb(30, 136, 229)。");
        return value[(start + 1)..end];
    }

    private static byte Channel(string raw)
    {
        var text = raw.Trim();
        double value;
        if (text.EndsWith('%')) value = Number(text.TrimEnd('%')) * 255d / 100d;
        else value = Number(text);
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private static double Number(string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new PdfToolException($"“{raw}”不是有效数字。");

    private static (double Hue, double Saturation, double Lightness) ToHsl(byte red, byte green, byte blue)
    {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2d;
        var delta = max - min;
        if (delta < 1e-6) return (0, 0, lightness * 100d);
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, saturation * 100d, lightness * 100d);
    }

    private static (byte Red, byte Green, byte Blue) FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = ((hue % 360) + 360) % 360 / 60d;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        var m = lightness - c / 2;
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            result.Add(separator < 0
                ? new KeyValuePair<string, string>(Decode(pair), "")
                : new KeyValuePair<string, string>(Decode(pair[..separator]), Decode(pair[(separator + 1)..])));
        }
        return result;
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }

    private static Dictionary<char, int> BuildDigits()
    {
        var map = new Dictionary<char, int>();
        for (var index = 0; index < 10; index++) map[(char)('0' + index)] = index;
        for (var index = 0; index < 6; index++) map[(char)('A' + index)] = 10 + index;
        return map;
    }
}
