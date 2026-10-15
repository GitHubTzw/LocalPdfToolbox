using System.IO;
using System.Security.Cryptography;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 目录对比：递归比较两个本地文件夹，列出仅左侧、仅右侧、已修改和完全相同的文件。
/// 只读取文件大小或内容摘要，不写日志、不上传，也不修改任何一侧。
/// </summary>
public sealed class DirectoryDiffTool : IDirectoryDiffTool
{
    /// <summary>单侧最多比较的文件数，避免超大目录拖垮界面。</summary>
    public const int MaxFilesPerSide = 20_000;

    public DirectoryDiffResult Compare(string leftDirectory, string rightDirectory, bool compareContent,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var left = LocalPathGuard.ExistingDirectory(leftDirectory);
        var right = LocalPathGuard.ExistingDirectory(rightDirectory);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            throw new PdfToolException("两侧选择了同一个文件夹，请选择两个不同的文件夹。");

        var leftFiles = Enumerate(left, progress, cancellationToken);
        var rightFiles = Enumerate(right, progress, cancellationToken);
        var entries = new List<DirectoryDiffEntry>();
        var identical = 0;
        var modified = 0;
        var leftOnly = 0;
        var rightOnly = 0;

        foreach (var pair in leftFiles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rightFiles.TryGetValue(pair.Key, out var other))
            {
                entries.Add(new DirectoryDiffEntry(pair.Key, DirectoryEntryState.LeftOnly, pair.Value.Length, 0));
                leftOnly++;
                continue;
            }
            var same = compareContent
                ? SameContent(pair.Value.FullName, other.FullName, cancellationToken)
                : pair.Value.Length == other.Length;
            entries.Add(new DirectoryDiffEntry(pair.Key, same ? DirectoryEntryState.Identical : DirectoryEntryState.Modified,
                pair.Value.Length, other.Length));
            if (same) identical++;
            else modified++;
        }

        foreach (var pair in rightFiles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leftFiles.ContainsKey(pair.Key)) continue;
            entries.Add(new DirectoryDiffEntry(pair.Key, DirectoryEntryState.RightOnly, 0, pair.Value.Length));
            rightOnly++;
        }

        var mode = compareContent ? "按内容摘要" : "按文件大小";
        var summary = entries.Count == 0
            ? "两个文件夹都是空的，没有可对比的内容。"
            : $"共 {entries.Count} 个文件（{mode}比较）：相同 {identical}，修改 {modified}，仅左侧 {leftOnly}，仅右侧 {rightOnly}。";
        return new DirectoryDiffResult(entries, identical, modified, leftOnly, rightOnly, summary);
    }

    private static Dictionary<string, FileInfo> Enumerate(string root, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 跳过符号链接、目录联接和云占位符，避免越界读取。
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(directory);
            }
            foreach (var path in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (files.Count >= MaxFilesPerSide)
                    throw new PdfToolException($"单侧最多比较 {MaxFilesPerSide} 个文件，请先缩小对比范围。");
                var relative = Path.GetRelativePath(root, path);
                files[relative] = new FileInfo(path);
                if (files.Count % 500 == 0) progress?.Report($"已扫描 {files.Count} 个文件…");
            }
        }
        return files;
    }

    private static bool SameContent(string left, string right, CancellationToken cancellationToken)
    {
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (leftStream.Length != rightStream.Length) return false;
        var leftHash = SHA256.HashData(leftStream);
        cancellationToken.ThrowIfCancellationRequested();
        var rightHash = SHA256.HashData(rightStream);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }
}
