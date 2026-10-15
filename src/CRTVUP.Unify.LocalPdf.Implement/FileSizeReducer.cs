using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>体积文本格式化，供压缩结果和界面共用。</summary>
public static class FileSizeFormatter
{
    public static string Format(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024d / 1024d:0.##} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.##} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB"
        : $"{bytes} 字节";

    /// <summary>生成“原大小 → 新大小，减少 x%”的说明；没有变小时如实提示保留原文件。</summary>
    public static string DescribeChange(long original, long size, string action)
    {
        var saved = original - size;
        var percent = original <= 0 ? 0 : saved * 100d / original;
        return saved > 0
            ? $"{action}：{Format(original)} → {Format(size)}，减少 {percent:0.#}%。"
            : $"{action}：{Format(original)} → {Format(size)}，体积没有变小，建议保留原文件。";
    }
}

/// <summary>按扩展名识别文件类别；只做本地判断，不读取文件内容。</summary>
public static class CompressKindDetector
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx" };

    public static CompressFileKind Detect(string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return CompressFileKind.Pdf;
        if (ImageExtensions.Contains(extension)) return CompressFileKind.Image;
        if (OfficeExtensions.Contains(extension)) return CompressFileKind.Office;
        return CompressFileKind.Unknown;
    }

    public static string Describe(CompressFileKind kind) => kind switch
    {
        CompressFileKind.Pdf => "PDF",
        CompressFileKind.Image => "图片",
        CompressFileKind.Office => "Office 文档",
        _ => "不支持"
    };
}

/// <summary>
/// PDF 体积压缩：用本机 PDFium 完整重写文件，压缩对象流并去掉增量更新的历史垃圾。
/// 保留可搜索文字、字体和图形，不改变页面外观。
/// </summary>
public sealed class PdfSizeReducer : IFileSizeReducer
{
    public CompressFileKind Kind => CompressFileKind.Pdf;

    public CompressResult Reduce(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var destination = LocalPathGuard.NewFile(outputPath);
        var original = new FileInfo(source).Length;
        var bytes = LocalFileStore.Read(source);
        byte[]? reduced = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在重新压缩 PDF 结构…");
            using var document = PdfiumRuntime.Open(bytes);
            if (document.PageCount is < 1 or > 5000) throw new PdfToolException("这个 PDF 没有可处理的页面，或页数超出范围。");
            reduced = document.SaveAsCopy();
            if (reduced.Length == 0) throw new PdfToolException("重新压缩后内容为空，已放弃，原文件未修改。");
            // 必须能再次打开才写出，避免产生无法读取的结果。
            using (var verify = PdfiumRuntime.Open(reduced))
                if (verify.PageCount != document.PageCount)
                    throw new PdfToolException("重新压缩后页数发生变化，已放弃，原文件未修改。");
            cancellationToken.ThrowIfCancellationRequested();
            LocalFileStore.WriteNew(destination, reduced, cancellationToken);
        }
        catch (IOException error) { throw new PdfToolException("压缩失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (reduced is not null) CryptographicOperations.ZeroMemory(reduced);
        }
        var size = new FileInfo(destination).Length;
        return new CompressResult(destination, original, size, FileSizeFormatter.DescribeChange(original, size, "PDF 结构已重写"));
    }
}

/// <summary>
/// Office 文档（docx/xlsx/pptx）体积压缩：按最大压缩率重新打包内部 ZIP。
/// 只搬运原有条目，不解析、不改写文档内容，也不执行宏或公式。
/// </summary>
public sealed class OfficeSizeReducer : IFileSizeReducer
{
    public const int MaxEntries = 20000;
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    public CompressFileKind Kind => CompressFileKind.Office;

    public CompressResult Reduce(string inputPath, string outputPath, CompressOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var destination = LocalPathGuard.NewFile(outputPath);
        var original = new FileInfo(source).Length;
        var created = false;
        var completed = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                created = true;
                if (archive.Entries.Count > MaxEntries) throw new PdfToolException($"文档内部条目超过 {MaxEntries} 个，请先拆分。");
                using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var buffer = new byte[64 * 1024];
                long total = 0;
                try
                {
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.FullName.Length == 0) continue;
                        total += entry.Length;
                        if (total > MaxTotalBytes) throw new PdfToolException("文档内容过大，请先拆分。");
                        progress?.Report("正在压缩 " + entry.FullName);
                        var target_entry = target.CreateEntry(entry.FullName, CompressionLevel.SmallestSize);
                        try { target_entry.LastWriteTime = entry.LastWriteTime; }
                        catch (ArgumentOutOfRangeException) { }
                        if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;
                        using var entryInput = entry.Open();
                        using var entryOutput = target_entry.Open();
                        int read;
                        while ((read = entryInput.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            entryOutput.Write(buffer, 0, read);
                        }
                    }
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
            completed = true;
        }
        catch (InvalidDataException error) { throw new PdfToolException("这个文件不是有效的 Office 文档（应为 docx、xlsx 或 pptx）。", error); }
        catch (IOException error) { throw new PdfToolException("压缩失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { if (created && !completed) TryDelete(destination); }
        var size = new FileInfo(destination).Length;
        return new CompressResult(destination, original, size, FileSizeFormatter.DescribeChange(original, size, "文档已重新打包"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
