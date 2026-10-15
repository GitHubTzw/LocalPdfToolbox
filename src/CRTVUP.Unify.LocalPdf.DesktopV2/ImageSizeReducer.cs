using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Desktop;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 图片体积压缩：按目标质量重新编码，可选按比例缩小分辨率。
/// 解码和编码都使用 Windows 自带组件，不联网、不写入原文件。
/// </summary>
internal sealed class ImageSizeReducer : IFileSizeReducer
{
    public CompressFileKind Kind => CompressFileKind.Image;

    public CompressResult Reduce(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var original = new FileInfo(source).Length;
        var bytes = LocalFileStore.Read(source);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在重新编码图片…");
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = LocalImageCodec.Decode(stream);
            var extension = Path.GetExtension(source);
            var (encoder, targetExtension) = LocalImageEncoding.CreateEncoder(extension, options.Quality, decoder.Frames.Count > 1);
            // 只有 TIFF 保留多帧；其他格式取第一帧并在状态里说明。
            var keepAllFrames = targetExtension == ".tif";
            var count = keepAllFrames ? decoder.Frames.Count : 1;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = decoder.Frames[index];
                LocalImageEncoding.RequireReasonableSize(frame);
                var scaled = LocalImageEncoding.Scale(frame, options.ScalePercent);
                encoder.Frames.Add(BitmapFrame.Create(targetExtension == ".jpg" ? LocalImageEncoding.Flatten(scaled) : scaled));
            }
            var destination = LocalPathGuard.NewFile(ReplaceExtension(outputPath, targetExtension));
            byte[]? encoded = null;
            try
            {
                encoded = LocalImageEncoding.Save(encoder);
                cancellationToken.ThrowIfCancellationRequested();
                LocalFileStore.WriteNew(destination, encoded, cancellationToken);
            }
            finally { if (encoded is not null) CryptographicOperations.ZeroMemory(encoded); }
            var size = new FileInfo(destination).Length;
            var note = decoder.Frames.Count > 1 && !keepAllFrames ? "（只保留第一帧）" : "";
            var action = $"图片已重新编码为 {targetExtension.TrimStart('.').ToUpperInvariant()}{note}";
            return new CompressResult(destination, original, size, FileSizeFormatter.DescribeChange(original, size, action));
        }
        catch (NotSupportedException error) { throw new PdfToolException("这个图片格式无法在本机重新编码。", error); }
        catch (IOException error) { throw new PdfToolException("压缩失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string ReplaceExtension(string path, string extension)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path) + extension;
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }
}
