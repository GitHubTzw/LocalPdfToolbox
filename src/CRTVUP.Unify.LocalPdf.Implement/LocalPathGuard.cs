using System.IO;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// 各本地工具共用的路径策略：只接受本地固定/可移动磁盘上的普通文件和文件夹，
/// 拒绝网址、共享目录、映射盘、设备名、备用数据流、符号链接和云文件占位符。
/// </summary>
public static class LocalPathGuard
{
    /// <summary>校验一个已存在的普通文件。</summary>
    public static string ExistingFile(string? path) => LocalPathPolicy.Validate(path, output: false);

    /// <summary>校验一个尚不存在的输出文件位置；目标已存在时直接拒绝。</summary>
    public static string NewFile(string? path) => LocalPathPolicy.Validate(path, output: true);

    /// <summary>校验一个已存在的文件夹。</summary>
    public static string ExistingDirectory(string? path) => ValidateDirectory(path, mustNotExist: false);

    /// <summary>校验一个尚不存在的输出文件夹位置；目标已存在时直接拒绝。</summary>
    public static string NewDirectory(string? path) => ValidateDirectory(path, mustNotExist: true);

    /// <summary>校验一个已存在的文件或文件夹。</summary>
    public static string ExistingPath(string? path)
    {
        try { return ExistingFile(path); }
        catch (PdfToolException fileError)
        {
            try { return ExistingDirectory(path); }
            catch (PdfToolException) { throw fileError; }
        }
    }

    private static string ValidateDirectory(string? path, bool mustNotExist)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PdfToolException("只允许本地磁盘的完整文件夹路径，不支持网址、共享目录或设备路径。");
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length < 3 || !char.IsAsciiLetter(trimmed[0]) || trimmed[1] != ':'
            || (trimmed[2] != '\\' && trimmed[2] != '/')
            || trimmed.AsSpan(2).Contains(':'))
            throw new PdfToolException("只允许本地磁盘的完整文件夹路径，不支持网址、共享目录或设备路径。");

        string fullPath;
        try { fullPath = Path.GetFullPath(trimmed); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PdfToolException("文件夹路径无效，请选择本地磁盘上的普通文件夹。");
        }

        var root = Path.GetPathRoot(fullPath)!;
        var drive = new DriveInfo(root);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
            throw new PdfToolException("不允许使用网络映射盘或不可识别的磁盘，请选择本地磁盘。");

        var components = fullPath[root.Length..].Split(Path.DirectorySeparatorChar);
        if (components.Length == 0 || components.Any(LocalPathPolicy.InvalidComponent))
            throw new PdfToolException("文件夹路径包含设备名称、无效字符或不明确的名称。");

        var current = root;
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var final = index == components.Length - 1;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException)
            {
                if (mustNotExist && final) return fullPath;
                throw new PdfToolException("文件夹不存在，请选择已有的本地文件夹。");
            }
            catch (DirectoryNotFoundException)
            {
                throw new PdfToolException("上级文件夹不存在，请选择已有的本地文件夹。");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PdfToolException("不允许符号链接、目录联接或云文件占位符，请选择实际保存在本地的文件夹。");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new PdfToolException("路径中包含普通文件，无法作为文件夹使用。");
            if (final && mustNotExist)
                throw new PdfToolException("目标位置已存在同名文件或文件夹；请换一个名称，本工具不会覆盖任何内容。");
        }
        return fullPath;
    }
}
