using System.Security.Cryptography;
using CRTVUP.Unify.LocalPdf.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace CRTVUP.Unify.LocalPdf.Implement;

public sealed partial class PdfWorkspace
{
    public PdfSource LoadFromMemory(string displayName, ReadOnlyMemory<byte> content) =>
        StoreGeneratedContent(null, displayName, content);

    public PdfSource ReplaceContent(string sourceId, ReadOnlyMemory<byte> content)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var source = GetSource(sourceId);
            return StoreGeneratedContent(sourceId, source.Source.FileName, content);
        }
    }

    private PdfSource StoreGeneratedContent(string? replacingId, string displayName, ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var previous = replacingId is null ? null : GetSource(replacingId);
            var oldBytes = previous?.Content.LongLength ?? 0;
            var oldPages = previous?.Source.Pages.Count ?? 0;
            if (data.Length == 0)
                throw new PdfToolException("生成的 PDF 为空，无法载入。");
            var content = data.ToArray();
            var committed = false;
            try
            {
                using var document = OpenDocument(content);
                ValidateDocument(document);
                if (document.NumberOfPages <= 0 || document.NumberOfPages > MAX_PAGES - _pageCount + oldPages)
                    throw new PdfToolException("生成的 PDF 没有页面或超过会话的 5000 页限制。");
                if (previous is not null && document.NumberOfPages != oldPages)
                    throw new PdfToolException("内容编辑改变了源文件页数，已保留原来的会话内容。");
                var pages = new PdfPageInfo[document.NumberOfPages];
                for (var index = 0; index < pages.Length; index++)
                {
                    var page = document.GetPage(index + 1);
                    ValidatePage(document, page);
                    if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0)
                        throw new PdfToolException("生成的 PDF 页面尺寸无效。");
                    pages[index] = new(index + 1, page.Width, page.Height, NormalizeRotation(page.Rotation.Value));
                }
                var id = replacingId ?? Guid.NewGuid().ToString("N");
                var name = Path.GetFileName(displayName);
                if (string.IsNullOrWhiteSpace(name)) name = "转换后的文档.pdf";
                var result = new PdfSource(id, name, content.LongLength, Array.AsReadOnly(pages));
                _sources[id] = new SourceEntry(result, content);
                _byteCount += content.LongLength - oldBytes;
                _pageCount += pages.Length - oldPages;
                committed = true;
                if (previous is not null) CryptographicOperations.ZeroMemory(previous.Content);
                return result;
            }
            catch (PdfToolException) { throw; }
            catch (Exception) { throw new PdfToolException("生成的 PDF 无法安全载入，当前会话保持不变。"); }
            finally { if (!committed) CryptographicOperations.ZeroMemory(content); }
        }
    }

    public byte[] ExportToMemory(IReadOnlyList<PageEdit> pages, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (pages is null || pages.Count == 0 || pages.Count > MAX_PAGES)
                throw new PdfToolException("请选择 1–5000 页后再转换。");
            var edits = pages.ToArray();
            var documents = new Dictionary<string, PdfDocument>(StringComparer.Ordinal);
            try
            {
                using var builder = new PdfDocumentBuilder { IncludeDocumentInformation = false };
                foreach (var edit in edits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (edit is null) throw new PdfToolException("页面列表包含无效项。");
                    var source = GetSource(edit.SourceId);
                    if (edit.PageNumber < 1 || edit.PageNumber > source.Source.Pages.Count || edit.Rotation % 90 != 0)
                        throw new PdfToolException("页面范围或旋转角度无效。");
                    if (!documents.TryGetValue(edit.SourceId, out var document))
                    {
                        document = OpenDocument(source.Content);
                        documents.Add(edit.SourceId, document);
                    }
                    var page = builder.AddPage(document, edit.PageNumber);
                    page.SetRotation(new PageRotationDegrees((source.Source.Pages[edit.PageNumber - 1].OriginalRotation + NormalizeRotation(edit.Rotation)) % 360));
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = builder.Build();
                if (cancellationToken.IsCancellationRequested)
                {
                    CryptographicOperations.ZeroMemory(result);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return result;
            }
            catch (PdfToolException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new PdfToolException("无法在内存中生成 PDF，原文件未修改。"); }
            finally { foreach (var document in documents.Values) document.Dispose(); }
        }
    }
}

public static class LocalFileStore
{
    public static byte[] Read(string path)
    {
        try
        {
            var localPath = LocalPathPolicy.Validate(path, output: false);
            using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length <= 0 || file.Length > int.MaxValue)
                throw new PdfToolException("文件为空或超出单次可载入内存的范围。");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (PdfToolException) { throw; }
        catch (Exception) { throw new PdfToolException("无法读取所选本地文件，请检查文件权限和大小。"); }
    }

    public static void WriteNew(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        try { PdfWorkspace.WriteNewFile(path, bytes, cancellationToken); }
        catch (PdfToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new PdfToolException("保存失败，请使用可写的本地文件夹和新的文件名。"); }
    }
}
