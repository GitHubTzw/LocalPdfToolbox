using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>本地文本文件读写：沿用同一套路径策略，导出只新建文件，不覆盖已有内容。</summary>
internal static class LocalTextFile
{
    public static string Read(string path)
    {
        var bytes = LocalFileStore.Read(path);
        try { return Decode(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void WriteNew(string path, string text)
    {
        var encoding = new UTF8Encoding(true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text ?? "");
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        try { LocalFileStore.WriteNew(path, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }
}
