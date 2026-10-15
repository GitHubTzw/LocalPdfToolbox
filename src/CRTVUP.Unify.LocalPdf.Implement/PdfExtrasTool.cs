using System.Globalization;
using System.IO;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// PDF 附加操作：查看文档信息、导出文字层、按页数或页码范围拆分。
/// 输入是调用方已经读入内存的内容，本类不读源文件；输出一律新建文件，不覆盖任何已有内容。
/// </summary>
public sealed class PdfExtrasTool : IPdfExtrasTool
{
    public PdfDocumentInfo Inspect(ReadOnlyMemory<byte> content)
    {
        if (content.Length == 0) throw new PdfToolException("PDF 内容为空，无法读取信息。");
        using var document = PdfiumRuntime.Open(content);
        var pages = new List<PdfPageInfo>(document.PageCount);
        for (var number = 1; number <= document.PageCount; number++)
        {
            var (width, height) = document.GetPageSize(number);
            pages.Add(new PdfPageInfo(number, width, height, 0));
        }
        var summary = $"共 {document.PageCount} 页，文件 {FileSizeFormatter.Format(content.Length)}。"
            + (pages.Count > 0 ? $" 首页尺寸 {pages[0].Width:0.#} × {pages[0].Height:0.#} 点。" : "");
        return new PdfDocumentInfo(document.PageCount, content.Length, pages, summary);
    }

    public string ExtractText(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0) throw new PdfToolException("PDF 内容为空，无法提取文字。");
        using var document = PdfiumRuntime.Open(content);
        var builder = new StringBuilder();
        for (var number = 1; number <= document.PageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("===== 第 ").Append(number.ToString(CultureInfo.InvariantCulture)).AppendLine(" 页 =====");
            var text = document.ExtractText(number);
            builder.AppendLine(string.IsNullOrWhiteSpace(text) ? "（这一页没有可提取的文字层）" : text.TrimEnd());
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public IReadOnlyList<string> SplitEvery(ReadOnlyMemory<byte> content, string displayName, string outputDirectory,
        int pagesPerFile, CancellationToken cancellationToken = default)
    {
        if (pagesPerFile < 1) throw new PdfToolException("每个文件至少包含 1 页。");
        using var workspace = new PdfWorkspace();
        var source = workspace.LoadFromMemory(displayName, content);
        var groups = new List<List<int>>();
        for (var start = 1; start <= source.Pages.Count; start += pagesPerFile)
            groups.Add(Enumerable.Range(start, Math.Min(pagesPerFile, source.Pages.Count - start + 1)).ToList());
        return Export(workspace, source, outputDirectory, groups, cancellationToken);
    }

    public IReadOnlyList<string> SplitByGroups(ReadOnlyMemory<byte> content, string displayName, string outputDirectory,
        string groups, CancellationToken cancellationToken = default)
    {
        using var workspace = new PdfWorkspace();
        var source = workspace.LoadFromMemory(displayName, content);
        var parsed = ParseGroups(groups, source.Pages.Count);
        return Export(workspace, source, outputDirectory, parsed, cancellationToken);
    }

    private static List<string> Export(PdfWorkspace workspace, PdfSource source, string outputDirectory,
        IReadOnlyList<List<int>> groups, CancellationToken cancellationToken)
    {
        var folder = LocalPathGuard.ExistingDirectory(outputDirectory);
        var stem = Path.GetFileNameWithoutExtension(source.FileName);
        if (stem.Length == 0) stem = "拆分结果";
        var outputs = new List<string>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = group.Count == 1 ? $"第{group[0]}页" : $"第{group[0]}-{group[^1]}页";
            var target = UniquePath(folder, stem, label);
            workspace.Export(group.Select(number => new PageEdit(source.Id, number)).ToArray(), target, cancellationToken);
            outputs.Add(target);
        }
        return outputs;
    }

    /// <summary>解析 1-3;4-6;7 这样的分组写法，页码从 1 开始，允许中文逗号与空格。</summary>
    private static List<List<int>> ParseGroups(string? text, int pageCount)
    {
        var raw = (text ?? "").Replace('，', ';').Replace(',', ';');
        var segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) throw new PdfToolException("请填写页码范围，例如 1-3;4-6;7。");
        var groups = new List<List<int>>();
        foreach (var segment in segments)
        {
            var bounds = segment.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2) throw new PdfToolException($"“{segment}”不是有效的页码范围。");
            var start = ParsePage(bounds[0], segment);
            var end = bounds.Length == 1 ? start : ParsePage(bounds[1], segment);
            if (end < start) throw new PdfToolException($"“{segment}”的结束页小于起始页。");
            var group = new List<int>();
            for (var number = start; number <= end; number++)
            {
                if (number < 1 || number > pageCount)
                    throw new PdfToolException($"页码 {number} 超出范围，这份 PDF 共 {pageCount} 页。");
                group.Add(number);
            }
            groups.Add(group);
        }
        return groups;
    }

    private static int ParsePage(string value, string segment) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new PdfToolException($"“{segment}”里包含不是数字的页码。");

    private static string UniquePath(string folder, string stem, string label)
    {
        var candidate = Path.Combine(folder, $"{stem}-{label}.pdf");
        for (var index = 2; File.Exists(candidate) || Directory.Exists(candidate); index++)
            candidate = Path.Combine(folder, $"{stem}-{label} ({index}).pdf");
        return candidate;
    }
}
