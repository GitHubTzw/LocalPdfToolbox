using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using UglyToad.PdfPig.Writer;

namespace CRTVUP.Unify.LocalPdf.Implement;

public sealed partial class PdfiumDocument
{
    internal static byte[] BuildTextDocument(IReadOnlyList<string> sections, CancellationToken cancellationToken)
    {
        var allText = string.Join('\n', sections);
        if (string.IsNullOrWhiteSpace(allText)) throw new PdfToolException("没有可转换的文字。");
        if (allText.Any(character => char.IsSurrogate(character) || character is >= '\u0590' and <= '\u0FFF'))
            throw new PdfToolException("文字版转换支持常用中文和西文；表情、补充平面字符或复杂字形语言暂不支持。");
        using var builder = new PdfDocumentBuilder { IncludeDocumentInformation = false };
        builder.AddPage(595.276, 841.89);
        var blank = builder.Build();
        try
        {
            using var document = new PdfiumDocument(blank);
            lock (PdfiumRuntime.SyncRoot)
            {
                var selectedFont = allText.Any(IsChineseCharacter) ? PdfTextFont.SimHei : PdfTextFont.Arial;
                var font = document.GetFont(selectedFont, allText);
                const double FONT_SIZE = 11;
                var widths = new Dictionary<char, double>();
                List<List<string>> pageLines = [[]];
                List<string> Current() => pageLines[^1];
                void NewPage()
                {
                    if (pageLines.Count == 500) throw new PdfToolException("文字重新分页后超过 500 页，请拆分文件。");
                    pageLines.Add([]);
                }
                void AddLine(string text)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Current().Count == 49) NewPage();
                    Current().Add(text);
                }
                for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
                {
                    if (sectionIndex > 0) NewPage();
                    foreach (var raw in sections[sectionIndex].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                    {
                        var line = new StringBuilder();
                        var width = 0d;
                        foreach (var c in raw.Replace("\t", "    ", StringComparison.Ordinal))
                        {
                            if (char.IsControl(c)) continue;
                            if (!widths.TryGetValue(c, out var characterWidth)) widths[c] = characterWidth = font.AdvanceWidth(c, FONT_SIZE);
                            if (line.Length > 0 && width + characterWidth > 499) { AddLine(line.ToString()); line.Clear(); width = 0; }
                            line.Append(c);
                            width += characterWidth;
                        }
                        AddLine(line.ToString());
                    }
                }
                for (var index = 0; index < pageLines.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index > 0)
                    {
                        var created = PdfiumNative.FPDFPage_New(document._handle, index, 595.276, 841.89);
                        RequireHandle(created, "无法添加文字页面。");
                        PdfiumNative.FPDF_ClosePage(created);
                        document._pageCount++;
                    }
                    var text = string.Join('\n', pageLines[index]);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    document.WithPage(index + 1, page =>
                    {
                        document.InsertText(page, new PdfEditRequest { PageNumber = index + 1, Kind = PdfEditKind.InsertText, Text = text, Font = selectedFont },
                            new PdfiumNative.Matrix(1, 0, 0, 1, 48, 791), FONT_SIZE, nint.Zero);
                        Require(PdfiumNative.FPDFPage_GenerateContent(page), "无法生成文字页。");
                        return 0;
                    });
                }
                cancellationToken.ThrowIfCancellationRequested();
                return document.SaveAsCopyCore();
            }
        }
        finally { CryptographicOperations.ZeroMemory(blank); }
    }
}
