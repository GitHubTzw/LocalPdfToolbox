using System.IO;
using System.Security.Cryptography;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>本地摘要计算：文本或分块读取文件，不写日志、不保留内容。</summary>
public sealed class HashTool : IHashTool
{
    public string Compute(string input, HashKind kind, TextCharset charset = TextCharset.Utf8)
    {
        var bytes = new TextCodec().Encode(input ?? "", charset);
        try { return Convert.ToHexString(Hash(bytes, kind)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string ComputeFile(string path, HashKind kind, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var localPath = LocalPathGuard.ExistingFile(path);
        using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(new HashAlgorithmName(AlgorithmName(kind)));
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (IOException) { throw new PdfToolException("无法读取该文件，请确认文件存在且没有被其他程序独占。"); }
        catch (UnauthorizedAccessException) { throw new PdfToolException("没有读取权限，或所选项目不是普通文件。"); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string AlgorithmName(HashKind kind) => kind switch
    {
        HashKind.Md5 => "MD5",
        HashKind.Sha1 => "SHA1",
        HashKind.Sha256 => "SHA256",
        _ => "SHA512"
    };

    private static byte[] Hash(byte[] bytes, HashKind kind) => kind switch
    {
        HashKind.Md5 => MD5.HashData(bytes),
        HashKind.Sha1 => SHA1.HashData(bytes),
        HashKind.Sha256 => SHA256.HashData(bytes),
        _ => SHA512.HashData(bytes)
    };
}
