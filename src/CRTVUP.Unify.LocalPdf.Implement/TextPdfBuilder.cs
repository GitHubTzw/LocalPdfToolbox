using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>使用本地 PDFium 和完整本机字体，把文字重新排版为 A4 PDF。</summary>
public static class TextPdfBuilder
{
    public static byte[] Build(IReadOnlyList<string> sections, CancellationToken cancellationToken = default)
    {
        if (sections.Count is < 1 or > 500 || sections.Sum(section => (long)section.Length) > 2_000_000)
            throw new PdfToolException("文字数量超过本地转换上限。");
        return PdfiumDocument.BuildTextDocument(sections, cancellationToken);
    }
}
