using System.Security.Cryptography;
using CRTVUP.Unify.LocalPdf.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Writer;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>仅在内存中保存当前会话；不记录路径、历史或文档内容。</summary>
public sealed partial class PdfWorkspace : IPdfWorkspace
{
    public const int MAX_PAGES = 5000;

    private readonly object _gate = new();
    private readonly Dictionary<string, SourceEntry> _sources = new(StringComparer.Ordinal);
    private long _byteCount;
    private int _pageCount;
    private bool _disposed;

    public PdfSource Load(string localPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            byte[]? content = null;
            try
            {
                var path = LocalPathPolicy.Validate(localPath, output: false);
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                {
                    var length = input.Length;
                    if (length <= 0)
                        throw new PdfToolException("文件为空，无法作为 PDF 打开。");
                    if (length > int.MaxValue)
                        throw new PdfToolException("文件过大，超出单次可载入内存的范围。");

                    content = GC.AllocateUninitializedArray<byte>((int)length);
                    input.ReadExactly(content);
                    if (input.ReadByte() != -1)
                        throw new PdfToolException("读取期间文件发生变化，请重新添加。");
                }

                using var document = OpenDocument(content);
                ValidateDocument(document);
                if (document.NumberOfPages <= 0)
                    throw new PdfToolException("PDF 中没有可处理的页面。");
                if (document.NumberOfPages > MAX_PAGES - _pageCount)
                    throw new PdfToolException("当前会话最多载入 5000 页，请先移除部分文件。");

                var pages = new PdfPageInfo[document.NumberOfPages];
                for (var index = 0; index < pages.Length; index++)
                {
                    var page = document.GetPage(index + 1);
                    ValidatePage(document, page);
                    if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height)
                        || page.Width <= 0 || page.Height <= 0)
                        throw new PdfToolException("PDF 包含无效页面尺寸，无法安全处理。");
                    pages[index] = new PdfPageInfo(index + 1, page.Width, page.Height,
                        NormalizeRotation(page.Rotation.Value));
                }

                var source = new PdfSource(Guid.NewGuid().ToString("N"),
                    Path.GetFileName(path), content.LongLength, Array.AsReadOnly(pages));
                _sources.Add(source.Id, new SourceEntry(source, content));
                _byteCount += content.LongLength;
                _pageCount += pages.Length;
                content = null; // 所有权转移给会话；失败路径才在 finally 中归零。
                return source;
            }
            catch (PdfToolException) { throw; }
            catch (Exception exception) when (IsEncryptionFailure(exception))
            {
                throw new PdfToolException("当前版本不处理加密或带密码的 PDF，请先在可信软件中解密。");
            }
            catch (UnauthorizedAccessException)
            {
                throw new PdfToolException("没有读取权限，或所选项目不是普通文件。");
            }
            catch (IOException)
            {
                throw new PdfToolException("无法读取文件，请确认文件存在且没有被其他程序独占。");
            }
            catch (OutOfMemoryException)
            {
                throw new PdfToolException("内存不足，请减少文件数量或使用更小的 PDF。");
            }
            catch (Exception)
            {
                throw new PdfToolException("PDF 已损坏或采用当前版本不支持的格式，文件未被修改。");
            }
            finally
            {
                if (content is not null)
                    CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    public ReadOnlyMemory<byte> GetContent(string sourceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return GetSource(sourceId).Content;
        }
    }

    public void Remove(string sourceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (sourceId is not null && _sources.Remove(sourceId, out var source))
            {
                _byteCount -= source.Content.LongLength;
                _pageCount -= source.Source.Pages.Count;
                CryptographicOperations.ZeroMemory(source.Content);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ClearCore();
        }
    }

    public ExportResult Export(IReadOnlyList<PageEdit> pages, string outputPath,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (pages is null || pages.Count == 0)
                throw new PdfToolException("请至少保留一页再导出。");
            if (pages.Count > MAX_PAGES)
                throw new PdfToolException("一次最多导出 5000 页。");

            // 固定本次操作顺序，避免调用方在后台导出时修改集合。
            var edits = pages.ToArray();
            var opened = new Dictionary<string, PdfDocument>(StringComparer.Ordinal);
            byte[]? result = null;
            try
            {
                var destination = LocalPathPolicy.Validate(outputPath, output: true);
                foreach (var edit in edits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (edit is null)
                        throw new PdfToolException("页面列表包含无效项，请重新选择页面。");
                    var source = GetSource(edit.SourceId);
                    if (edit.PageNumber < 1 || edit.PageNumber > source.Source.Pages.Count)
                        throw new PdfToolException("页码超出文件范围，请重新选择页面。");
                    if (edit.Rotation % 90 != 0)
                        throw new PdfToolException("旋转角度必须是 90 度的整数倍。");
                }

                using var builder = new PdfDocumentBuilder
                {
                    IncludeDocumentInformation = false
                };
                foreach (var edit in edits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = GetSource(edit.SourceId);
                    if (!opened.TryGetValue(edit.SourceId, out var document))
                    {
                        document = OpenDocument(source.Content);
                        opened.Add(edit.SourceId, document);
                    }

                    // PdfPig 0.1.9 两参数重载的 copyLink 为 null，不复制链接动作。
                    var page = builder.AddPage(document, edit.PageNumber);
                    var original = source.Source.Pages[edit.PageNumber - 1].OriginalRotation;
                    var rotation = (original + NormalizeRotation(edit.Rotation)) % 360;
                    page.SetRotation(new PageRotationDegrees(rotation));
                }

                cancellationToken.ThrowIfCancellationRequested();
                result = builder.Build();
                cancellationToken.ThrowIfCancellationRequested();

                WriteNewFile(destination, result, cancellationToken);
                return new ExportResult(edits.Length, result.LongLength);
            }
            catch (OperationCanceledException) { throw; }
            catch (PdfToolException) { throw; }
            catch (UnauthorizedAccessException)
            {
                throw new PdfToolException("没有目标文件夹的写入权限，请选择其他本地位置。");
            }
            catch (IOException)
            {
                throw new PdfToolException("导出失败，请检查磁盘空间；已有文件不会被覆盖。");
            }
            catch (OutOfMemoryException)
            {
                throw new PdfToolException("内存不足，请减少页面数量或分批导出。");
            }
            catch (Exception)
            {
                throw new PdfToolException("无法合并这些页面，源文件未被修改。");
            }
            finally
            {
                foreach (var document in opened.Values)
                    document.Dispose();
                if (result is not null)
                    CryptographicOperations.ZeroMemory(result);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            ClearCore();
            _disposed = true;
        }
    }

    private void ClearCore()
    {
        foreach (var source in _sources.Values)
            CryptographicOperations.ZeroMemory(source.Content);
        _sources.Clear();
        _byteCount = 0;
        _pageCount = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private SourceEntry GetSource(string? sourceId)
    {
        if (sourceId is null || !_sources.TryGetValue(sourceId, out var source))
            throw new PdfToolException("源文件已移除，请重新添加。");
        return source;
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;

    private static PdfDocument OpenDocument(byte[] bytes) => PdfDocument.Open(bytes,
        new ParsingOptions { UseLenientParsing = false, Logger = SilentPdfLog.Instance });

    private static bool IsEncryptionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void ValidateDocument(PdfDocument document)
    {
        if (document.IsEncrypted)
            throw new PdfToolException("当前版本不处理加密或带密码的 PDF，请先在可信软件中解密。");
        var catalog = document.Structure.Catalog.CatalogDictionary.Data;
        if (catalog.ContainsKey("AcroForm") || catalog.ContainsKey("Perms"))
            throw new PdfToolException("此 PDF 包含交互表单或数字签名；为避免丢失填写内容或破坏签名，当前版本不处理。");
        if (catalog.ContainsKey("OCProperties"))
            throw new PdfToolException("此 PDF 包含可切换图层，当前版本无法保证显示内容不变，请先导出为普通 PDF。");
    }

    private static void ValidatePage(PdfDocument document, Page page)
    {
        if (page.Dictionary.Data.TryGetValue("Annots", out var annotationToken))
        {
            var token = ResolveToken(document, annotationToken);
            if (token is not NullToken)
            {
                if (token is not ArrayToken array)
                    throw new PdfToolException("PDF 的批注结构无效，无法安全处理。");
                foreach (var item in array.Data)
                {
                    if (ResolveToken(document, item) is not DictionaryToken annotation
                        || !annotation.Data.TryGetValue("Subtype", out var subtype)
                        || ResolveToken(document, subtype) is not NameToken { Data: "Link" })
                        throw new PdfToolException("此 PDF 包含批注、印章或表单控件；为避免丢失可见内容，当前版本不处理。");
                }
            }
        }

        // 按原文合并：页面字典及其资源里的脚本、附件、元数据等附加内容不再拦截，
        // 由 AddPage 原样复制到新文件；这里只保留自包含检查，拒绝引用外部内容流。
        foreach (var (key, value) in page.Dictionary.Data)
        {
            if (key is "Parent" or "Annots" or "Type")
                continue;
            ValidateCopiedValue(document, value, new HashSet<UglyToad.PdfPig.Core.IndirectReference>(), 0);
        }
    }

    private static void ValidateCopiedValue(PdfDocument document, IToken token,
        HashSet<UglyToad.PdfPig.Core.IndirectReference> visited, int depth)
    {
        if (depth > 100 || visited.Count > 100_000)
            throw new PdfToolException("PDF 内部结构过于复杂，当前版本无法安全处理。");
        if (token is IndirectReferenceToken reference)
        {
            if (!visited.Add(reference.Data))
                return;
            ValidateCopiedValue(document, document.Structure.GetObject(reference.Data).Data,
                visited, depth + 1);
            return;
        }
        if (token is StreamToken stream)
        {
            if (stream.StreamDictionary.Data.ContainsKey("F"))
                throw new PdfToolException("PDF 引用了外部内容流，当前版本只处理自包含的本地 PDF。");
            token = stream.StreamDictionary;
        }
        if (token is DictionaryToken dictionary)
        {
            foreach (var value in dictionary.Data.Values)
                ValidateCopiedValue(document, value, visited, depth + 1);
        }
        else if (token is ArrayToken array)
        {
            foreach (var value in array.Data)
                ValidateCopiedValue(document, value, visited, depth + 1);
        }
    }

    private static IToken ResolveToken(PdfDocument document, IToken token)
    {
        for (var depth = 0; token is IndirectReferenceToken reference; depth++)
        {
            if (depth > 32)
                throw new PdfToolException("PDF 包含循环对象引用，无法安全处理。");
            token = document.Structure.GetObject(reference.Data).Data;
        }
        return token;
    }

    internal static void WriteNewFile(string path, byte[] bytes, CancellationToken token)
    {
        var created = false;
        var completed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            LocalPathPolicy.Validate(path, output: true);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                created = true;
                for (var offset = 0; offset < bytes.Length; offset += 64 * 1024)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                }
                output.Flush(flushToDisk: true);
                token.ThrowIfCancellationRequested();
            }
            completed = true;
        }
        finally
        {
            // 仅清理本次 CreateNew 成功创建、尚未完成的文件，绝不删除原有文件。
            if (created && !completed)
            {
                try { File.Delete(path); }
                catch (IOException)
                {
                    throw new PdfToolException("导出未完成，且未能清理本次新建的部分文件，请在所选位置手动删除该文件。");
                }
                catch (UnauthorizedAccessException)
                {
                    throw new PdfToolException("导出未完成，且没有权限清理本次新建的部分文件，请在所选位置手动删除该文件。");
                }
            }
        }
    }

    private sealed record SourceEntry(PdfSource Source, byte[] Content);

    private sealed class SilentPdfLog : ILog
    {
        public static readonly SilentPdfLog Instance = new();
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }
}

internal static class LocalPathPolicy
{
    public static string Validate(string? path, bool output)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3
            || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || (path[2] != '\\' && path[2] != '/')
            || path.AsSpan(2).Contains(':'))
            throw new PdfToolException("只允许本地磁盘的完整文件路径，不支持网址、共享目录或设备路径。");

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PdfToolException("文件路径无效，请选择本地磁盘上的普通 PDF 文件。");
        }

        var root = Path.GetPathRoot(fullPath)!;
        var drive = new DriveInfo(root);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
            throw new PdfToolException("不允许使用网络映射盘或不可识别的磁盘，请选择本地磁盘。");

        var components = fullPath[root.Length..].Split(Path.DirectorySeparatorChar);
        if (components.Length == 0 || components.Any(InvalidComponent))
            throw new PdfToolException("文件路径包含设备名称、无效字符或不明确的文件名。");

        var current = root;
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var final = index == components.Length - 1;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) when (output && final) { return fullPath; }
            catch (DirectoryNotFoundException)
            {
                throw new PdfToolException("目标文件夹不存在，请选择已有的本地文件夹。");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PdfToolException("不允许符号链接、目录联接或云文件占位符，请选择实际保存在本地的文件和文件夹。");
            if (!final && (attributes & FileAttributes.Directory) == 0)
                throw new PdfToolException("文件所在目录无效。");
            if (final)
            {
                if (output)
                    throw new PdfToolException("目标位置已有同名文件；请使用新文件名，本工具不会覆盖任何文件。");
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new PdfToolException("请选择普通 PDF 文件，而不是文件夹。");
            }
        }
        return fullPath;
    }

    internal static bool InvalidComponent(string value)
    {
        if (value.Length == 0 || value.EndsWith(' ') || value.EndsWith('.')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return true;
        var stem = value.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                                    || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]));
    }
}
