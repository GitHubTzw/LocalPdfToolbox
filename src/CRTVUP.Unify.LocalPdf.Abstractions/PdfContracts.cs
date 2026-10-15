namespace CRTVUP.Unify.LocalPdf.Abstractions;

public sealed record PdfPageInfo(int PageNumber, double Width, double Height, int OriginalRotation);

public sealed record PdfSource(
    string Id,
    string FileName,
    long ByteCount,
    IReadOnlyList<PdfPageInfo> Pages);

/// <summary>PageNumber 从 1 开始；Rotation 是在原方向上增加的顺时针角度。</summary>
public sealed record PageEdit(string SourceId, int PageNumber, int Rotation = 0);

public sealed record ExportResult(int PageCount, long ByteCount);

public sealed class PdfToolException : Exception
{
    public PdfToolException(string message) : base(message) { }
    public PdfToolException(string message, Exception innerException) : base(message, innerException) { }
}

public interface IPdfWorkspace : IDisposable
{
    PdfSource Load(string localPath);
    PdfSource LoadFromMemory(string displayName, ReadOnlyMemory<byte> content);
    PdfSource ReplaceContent(string sourceId, ReadOnlyMemory<byte> content);
    ReadOnlyMemory<byte> GetContent(string sourceId);
    void Remove(string sourceId);
    void Clear();
    ExportResult Export(IReadOnlyList<PageEdit> pages, string outputPath, CancellationToken cancellationToken = default);
    byte[] ExportToMemory(IReadOnlyList<PageEdit> pages, CancellationToken cancellationToken = default);
}
