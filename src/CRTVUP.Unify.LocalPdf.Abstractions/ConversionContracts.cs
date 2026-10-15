namespace CRTVUP.Unify.LocalPdf.Abstractions;

public enum PdfExportFormat { Png, Jpeg, Text, WordText, WordImages, ExcelText, PowerPointImages }

/// <summary>转换过程中的页面数据；图片为已剥离元数据的 PNG，尺寸以 PDF 点为单位。</summary>
public sealed record ConversionPage(int Number, double Width, double Height, string Text, byte[]? Png = null);

public interface IOfficeExporter
{
    byte[] Export(IReadOnlyList<ConversionPage> pages, PdfExportFormat format, CancellationToken cancellationToken = default);
}

/// <summary>文字导入不执行 Office 引擎、宏、公式或外部关系，也不承诺恢复原排版。</summary>
public sealed record ImportedText(IReadOnlyList<string> Sections, string Limitation);
