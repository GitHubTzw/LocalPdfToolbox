using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 本地 ZIP / GZip 压缩与解压：只读写本地磁盘，输出使用新文件名，
/// 解压时拒绝越界条目、设备名和覆盖已有文件。
/// </summary>
public sealed class ArchiveTool : IArchiveTool
{
    public const int MaxEntries = 20000;
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    public ArchiveResult CreateZip(string outputPath, IReadOnlyList<string> paths, ArchiveLevel level,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (paths is null || paths.Count == 0) throw new PdfToolException("请先选择要压缩的文件或文件夹。");
        var destination = LocalPathGuard.NewFile(outputPath);
        var entries = new List<(string Source, string Name)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = LocalPathGuard.ExistingPath(path);
                if ((File.GetAttributes(full) & FileAttributes.Directory) != 0)
                {
                    var root = Path.GetFullPath(full);
                    var parent = Path.GetDirectoryName(root) ?? root;
                    foreach (var file in EnumerateFiles(root, cancellationToken))
                    {
                        if (entries.Count >= MaxEntries) throw new PdfToolException($"压缩条目超过 {MaxEntries} 个，请分批处理。");
                        total += new FileInfo(file).Length;
                        if (total > MaxTotalBytes) throw new PdfToolException("压缩内容过大，请分批处理。");
                        entries.Add((file, Unique(Path.GetRelativePath(parent, file).Replace('\\', '/'), used)));
                    }
                }
                else
                {
                    if (entries.Count >= MaxEntries) throw new PdfToolException($"压缩条目超过 {MaxEntries} 个，请分批处理。");
                    total += new FileInfo(full).Length;
                    if (total > MaxTotalBytes) throw new PdfToolException("压缩内容过大，请分批处理。");
                    entries.Add((full, Unique(Path.GetFileName(full), used)));
                }
            }
        }
        catch (UnauthorizedAccessException error) { throw new PdfToolException("没有读取权限，或所选项目不是普通文件或文件夹。", error); }
        catch (DirectoryNotFoundException error) { throw new PdfToolException("所选文件夹已被移动或删除，请重新选择。", error); }
        if (entries.Count == 0) throw new PdfToolException("所选文件夹中没有可压缩的文件。");

        var created = false;
        var completed = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                created = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var buffer = new byte[64 * 1024];
                try
                {
                    foreach (var (source, name) in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report("正在压缩 " + name);
                        ZipArchiveEntry entry;
                        try { entry = archive.CreateEntry(name, Map(level)); }
                        catch (ArgumentException error) { throw new PdfToolException("有文件名无法写入压缩包，请重命名后重试。", error); }
                        try { entry.LastWriteTime = File.GetLastWriteTime(source); }
                        catch (ArgumentOutOfRangeException) { }
                        using var entryStream = entry.Open();
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            entryStream.Write(buffer, 0, read);
                        }
                    }
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
            completed = true;
        }
        catch (IOException error) { throw new PdfToolException("压缩失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { if (created && !completed) TryDelete(destination); }
        return new ArchiveResult(destination, entries.Count, new FileInfo(destination).Length);
    }

    public IReadOnlyList<ArchiveEntryInfo> ListZip(string zipPath)
    {
        var localPath = LocalPathGuard.ExistingFile(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(localPath);
            if (archive.Entries.Count > MaxEntries) throw new PdfToolException($"压缩包条目超过 {MaxEntries} 个，请先拆分。");
            return archive.Entries
                .Select(entry => new ArchiveEntryInfo(entry.FullName, entry.Length, entry.CompressedLength,
                    entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
                .ToArray();
        }
        catch (InvalidDataException error) { throw new PdfToolException("不是有效的 ZIP 压缩包，或文件已损坏。", error); }
    }

    public ArchiveResult ExtractZip(string zipPath, string destinationDirectory,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var localPath = LocalPathGuard.ExistingFile(zipPath);
        var destination = LocalPathGuard.NewDirectory(destinationDirectory);
        var boundary = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        var created = false;
        var completed = false;
        var count = 0;
        long total = 0;
        try
        {
            using var archive = ZipFile.OpenRead(localPath);
            if (archive.Entries.Count > MaxEntries) throw new PdfToolException($"压缩包条目超过 {MaxEntries} 个，请先拆分。");
            Directory.CreateDirectory(destination);
            created = true;
            var buffer = new byte[64 * 1024];
            try
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.Length == 0) continue;
                    ValidateEntryName(name);
                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(Path.Combine(destination, name));
                        continue;
                    }
                    var target = Path.GetFullPath(Path.Combine(destination, name));
                    if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                        throw new PdfToolException("压缩包中包含越界路径，已停止解压。");
                    total += Math.Max(0, entry.Length);
                    if (total > MaxTotalBytes) throw new PdfToolException("解压内容过大，请分批处理。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    progress?.Report("正在解压 " + name);
                    using var entryStream = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                    int read;
                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                    count++;
                }
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            completed = true;
        }
        catch (InvalidDataException error) { throw new PdfToolException("不是有效的 ZIP 压缩包，或文件已损坏。", error); }
        catch (IOException error) { throw new PdfToolException("解压失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { if (created && !completed) TryDeleteDirectory(destination); }
        return new ArchiveResult(destination, count, total);
    }

    public ArchiveResult CompressGzip(string outputPath, string inputPath, ArchiveLevel level,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var destination = LocalPathGuard.NewFile(outputPath);
        var created = false;
        var completed = false;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
            created = true;
            using var gzip = new GZipStream(output, Map(level), leaveOpen: true);
            Pump(input, gzip, progress, cancellationToken);
            completed = true;
        }
        catch (IOException error) { throw new PdfToolException("压缩失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { if (created && !completed) TryDelete(destination); }
        return new ArchiveResult(destination, 1, new FileInfo(destination).Length);
    }

    public ArchiveResult ExtractGzip(string outputPath, string inputPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPathGuard.ExistingFile(inputPath);
        var destination = LocalPathGuard.NewFile(outputPath);
        var created = false;
        var completed = false;
        long size;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
            created = true;
            size = Pump(gzip, output, progress, cancellationToken);
            completed = true;
        }
        catch (InvalidDataException error) { throw new PdfToolException("GZip 数据无效或已损坏。", error); }
        catch (IOException error) { throw new PdfToolException("解压失败，请检查磁盘空间和目标位置是否可写。", error); }
        finally { if (created && !completed) TryDelete(destination); }
        return new ArchiveResult(destination, 1, size);
    }

    private static long Pump(Stream from, Stream to, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        long reported = 0;
        try
        {
            int read;
            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                to.Write(buffer, 0, read);
                total += read;
                if (progress is not null && total - reported >= 4L * 1024 * 1024)
                {
                    reported = total;
                    progress.Report($"已处理 {total / 1024 / 1024} MB");
                }
            }
            return total;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                // 不跟随符号链接、目录联接和云占位符。
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                yield return file;
            }
        }
    }

    private static void ValidateEntryName(string name)
    {
        foreach (var segment in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (LocalPathPolicy.InvalidComponent(segment))
                throw new PdfToolException("压缩包中包含不安全的条目名称，已停止解压。");
    }

    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : "";
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static CompressionLevel Map(ArchiveLevel level) => level switch
    {
        ArchiveLevel.Fastest => CompressionLevel.Fastest,
        ArchiveLevel.Smallest => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
