using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 令牌与对称加密：Base32、JWT 解析、HMAC 与口令加解密。
/// 全部使用本机 .NET 实现；密钥不落盘，JWT 只解码不校验签名，也不访问任何服务。
/// </summary>
public sealed class TokenCryptoTool : ICryptoTextTool
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int AesKeyBytes = 32;
    private const int AesNonceBytes = 12;
    private const int AesTagBytes = 16;
    private const int SaltBytes = 16;
    private const int Iterations = 210_000;
    private const string EnvelopePrefix = "LPDF1:";

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public string Base32Encode(string input, TextCharset charset)
    {
        var bytes = new TextCodec().Encode(input ?? "", charset);
        try { return ToBase32(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Base32Decode(string input, TextCharset charset)
    {
        var bytes = FromBase32(input ?? "");
        try { return new TextCodec().Decode(bytes, charset); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string DescribeJwt(string token)
    {
        var text = (token ?? "").Trim();
        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) text = text[7..].Trim();
        var parts = text.Split('.');
        if (parts.Length < 2) throw new PdfToolException("JWT 至少需要头部和载荷两段，用英文句点分隔。");

        var builder = new StringBuilder();
        builder.AppendLine("头部（Header）：");
        builder.AppendLine(DecodeSegment(parts[0], "头部"));
        builder.AppendLine();
        builder.AppendLine("载荷（Payload）：");
        builder.AppendLine(DecodeSegment(parts[1], "载荷"));
        builder.AppendLine();
        builder.AppendLine($"签名段长度：{(parts.Length > 2 ? parts[2].Length : 0)} 字符");
        builder.AppendLine("提示：本工具只做本地解码，不校验签名，也不判断令牌是否有效。");
        AppendTimes(builder, parts[1]);
        return builder.ToString().TrimEnd();
    }

    public string ComputeHmac(string input, string key, HashKind kind, TextCharset charset)
    {
        if (string.IsNullOrEmpty(key)) throw new PdfToolException("请先输入密钥。");
        var codec = new TextCodec();
        var keyBytes = codec.Encode(key, charset);
        var dataBytes = codec.Encode(input ?? "", charset);
        try
        {
            using HMAC hmac = kind switch
            {
                HashKind.Md5 => new HMACMD5(keyBytes),
                HashKind.Sha1 => new HMACSHA1(keyBytes),
                HashKind.Sha512 => new HMACSHA512(keyBytes),
                _ => new HMACSHA256(keyBytes)
            };
            var digest = hmac.ComputeHash(dataBytes);
            try
            {
                return $"十六进制：{Convert.ToHexString(digest).ToLowerInvariant()}{Environment.NewLine}"
                    + $"Base64：{Convert.ToBase64String(digest)}";
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(dataBytes);
        }
    }

    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new PdfToolException("请先输入口令。");
        var plainBytes = Encoding.UTF8.GetBytes(plainText ?? "");
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(AesNonceBytes);
        var key = DeriveKey(password, salt);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[AesTagBytes];
        try
        {
            using var aes = new AesGcm(key, AesTagBytes);
            aes.Encrypt(nonce, plainBytes, cipher, tag);
            var envelope = new byte[salt.Length + nonce.Length + tag.Length + cipher.Length];
            salt.CopyTo(envelope, 0);
            nonce.CopyTo(envelope, salt.Length);
            tag.CopyTo(envelope, salt.Length + nonce.Length);
            cipher.CopyTo(envelope, salt.Length + nonce.Length + tag.Length);
            try
            {
                // 只返回可整段复制的密文；算法说明由界面提示，避免污染剪贴板内容。
                return EnvelopePrefix + Convert.ToBase64String(envelope);
            }
            finally { CryptographicOperations.ZeroMemory(envelope); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    public string Decrypt(string cipherText, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new PdfToolException("请先输入口令。");
        var text = (cipherText ?? "").Trim();
        // 允许粘贴时带上换行或说明文字，只取第一行有效载荷。
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (firstLine is not null) text = firstLine;
        if (text.StartsWith(EnvelopePrefix, StringComparison.Ordinal)) text = text[EnvelopePrefix.Length..];
        text = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (text.Length == 0) throw new PdfToolException("请先输入要解密的文本。");

        byte[] envelope;
        try { envelope = Convert.FromBase64String(text); }
        catch (FormatException error) { throw new PdfToolException("密文不是有效的 Base64 文本。", error); }
        try
        {
            var minimum = SaltBytes + AesNonceBytes + AesTagBytes;
            if (envelope.Length < minimum) throw new PdfToolException("密文长度不足，无法解密。");
            var salt = envelope.AsSpan(0, SaltBytes);
            var nonce = envelope.AsSpan(SaltBytes, AesNonceBytes);
            var tag = envelope.AsSpan(SaltBytes + AesNonceBytes, AesTagBytes);
            var cipher = envelope.AsSpan(SaltBytes + AesNonceBytes + AesTagBytes);
            var key = DeriveKey(password, salt);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, AesTagBytes);
                aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException error)
            {
                throw new PdfToolException("解密失败：口令不对，或密文被改动过。", error);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally { CryptographicOperations.ZeroMemory(envelope); }
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, AesKeyBytes);

    private static string DecodeSegment(string segment, string label)
    {
        byte[] bytes;
        try { bytes = Base64UrlDecode(segment); }
        catch (FormatException error) { throw new PdfToolException($"JWT {label}不是有效的 Base64URL 文本。", error); }
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            try
            {
                var node = JsonNode.Parse(text);
                return node?.ToJsonString(PrettyJson) ?? "null";
            }
            catch (JsonException) { return text; }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void AppendTimes(StringBuilder builder, string payload)
    {
        try
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(payload))) as JsonObject;
            if (json is null) return;
            var lines = new List<string>();
            foreach (var name in new[] { "iat", "nbf", "exp" })
            {
                if (json[name] is JsonValue value && value.TryGetValue<long>(out var seconds))
                {
                    var label = name switch { "iat" => "签发", "nbf" => "生效", _ => "过期" };
                    lines.Add($"{label}时间：{DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                }
            }
            if (lines.Count == 0) return;
            builder.AppendLine();
            foreach (var line in lines) builder.AppendLine(line);
        }
        catch (Exception) { /* 时间字段缺失或格式异常时忽略提示，不影响解码结果。 */ }
    }

    private static string ToBase32(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "";
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0) builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        while (builder.Length % 8 != 0) builder.Append('=');
        return builder.ToString();
    }

    private static byte[] FromBase32(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch) || ch == '=') continue;
            var upper = char.ToUpperInvariant(ch);
            if (Base32Alphabet.IndexOf(upper) < 0) throw new PdfToolException($"Base32 内容包含无效字符“{ch}”。");
            builder.Append(upper);
        }
        var text = builder.ToString();
        if (text.Length == 0) return [];
        var output = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var ch in text)
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. output];
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        var remainder = text.Length % 4;
        if (remainder != 0) text = text.PadRight(text.Length + (4 - remainder), '=');
        return Convert.FromBase64String(text);
    }
}
