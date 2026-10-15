using System.Globalization;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>Base64、URL、Unicode、HTML、十六进制等本地文本转换；不访问网络。</summary>
public sealed class TextCodec : ITextCodec
{
    static TextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Apply(string input, TextCodecOperation operation, TextCharset charset = TextCharset.Utf8)
    {
        var value = input ?? "";
        return operation switch
        {
            TextCodecOperation.Base64Encode => Convert.ToBase64String(Encode(value, charset)),
            TextCodecOperation.Base64Decode => Decode(FromBase64(value), charset),
            TextCodecOperation.UrlEncode => Uri.EscapeDataString(value),
            TextCodecOperation.UrlDecode => Uri.UnescapeDataString(value),
            TextCodecOperation.UnicodeEncode => ToUnicodeEscapes(value),
            TextCodecOperation.UnicodeDecode => FromUnicodeEscapes(value),
            TextCodecOperation.HtmlEncode => HtmlEscape(value),
            TextCodecOperation.HtmlDecode => HtmlUnescape(value),
            TextCodecOperation.HexEncode => Convert.ToHexString(Encode(value, charset)),
            TextCodecOperation.HexDecode => Decode(FromHex(value), charset),
            _ => throw new PdfToolException("不支持的转换方式。")
        };
    }

    public byte[] Encode(string text, TextCharset charset) => Charset(charset).GetBytes(text ?? "");

    public string Decode(ReadOnlyMemory<byte> bytes, TextCharset charset) => Charset(charset).GetString(bytes.Span);

    private static Encoding Charset(TextCharset charset) => charset switch
    {
        TextCharset.Utf8 => new UTF8Encoding(false),
        TextCharset.Utf16Le => new UnicodeEncoding(false, false),
        TextCharset.Utf16Be => new UnicodeEncoding(true, false),
        TextCharset.Ascii => Encoding.ASCII,
        TextCharset.Latin1 => Encoding.Latin1,
        TextCharset.Gbk => Encoding.GetEncoding(936),
        _ => new UTF8Encoding(false)
    };

    private static byte[] FromBase64(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch)) continue;
            builder.Append(ch switch { '-' => '+', '_' => '/', _ => ch });
        }
        var text = builder.ToString();
        var remainder = text.Length % 4;
        if (remainder != 0) text = text.PadRight(text.Length + (4 - remainder), '=');
        try { return Convert.FromBase64String(text); }
        catch (FormatException error) { throw new PdfToolException("Base64 内容无效，请检查是否完整。", error); }
    }

    private static byte[] FromHex(string input)
    {
        var builder = new StringBuilder(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            var ch = input[index];
            if (char.IsWhiteSpace(ch)) continue;
            if ((ch == '0' || ch == '\\') && index + 1 < input.Length && input[index + 1] is 'x' or 'X')
            {
                index++;
                continue;
            }
            builder.Append(ch);
        }
        var text = builder.ToString();
        if (text.Length == 0) return [];
        if (text.Length % 2 != 0) throw new PdfToolException("十六进制内容长度必须是偶数。");
        try { return Convert.FromHexString(text); }
        catch (FormatException error) { throw new PdfToolException("十六进制内容无效，只能包含 0-9 和 A-F。", error); }
    }

    private static string ToUnicodeEscapes(string input)
    {
        var builder = new StringBuilder(input.Length * 2);
        foreach (var ch in input)
        {
            if (ch is >= ' ' and <= '~') builder.Append(ch);
            else builder.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string FromUnicodeEscapes(string input)
    {
        var builder = new StringBuilder(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] == '\\' && index + 5 < input.Length && input[index + 1] is 'u' or 'U'
                && int.TryParse(input.AsSpan(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                builder.Append((char)code);
                index += 5;
                continue;
            }
            builder.Append(input[index]);
        }
        return builder.ToString();
    }

    private static string HtmlEscape(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                default: builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }

    private static string HtmlUnescape(string input)
    {
        var builder = new StringBuilder(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] != '&') { builder.Append(input[index]); continue; }
            var end = input.IndexOf(';', index + 1);
            if (end < 0 || end - index > 10) { builder.Append(input[index]); continue; }
            var entity = input[(index + 1)..end];
            var replacement = entity switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                "nbsp" => "\u00A0",
                _ => DecodeNumericEntity(entity)
            };
            if (replacement is null) { builder.Append(input[index]); continue; }
            builder.Append(replacement);
            index = end;
        }
        return builder.ToString();
    }

    private static string? DecodeNumericEntity(string entity)
    {
        if (entity.Length < 2 || entity[0] != '#') return null;
        var digits = entity.AsSpan(1);
        var hexadecimal = digits[0] is 'x' or 'X';
        if (hexadecimal) digits = digits[1..];
        if (digits.Length == 0) return null;
        if (!int.TryParse(digits, hexadecimal ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            return null;
        if (code is < 0 or > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return null;
        return char.ConvertFromUtf32(code);
    }
}

/// <summary>Unix 时间戳与本地时间的互转；不访问网络时间服务。</summary>
public static class UnixTime
{
    public static DateTimeOffset FromUnix(long value, bool milliseconds)
    {
        try
        {
            var time = milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
            return time.ToLocalTime();
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new PdfToolException("时间戳超出可表示范围，请检查单位是秒还是毫秒。", error);
        }
    }

    public static long ToUnix(DateTimeOffset value, bool milliseconds) =>
        milliseconds ? value.ToUnixTimeMilliseconds() : value.ToUnixTimeSeconds();
}
