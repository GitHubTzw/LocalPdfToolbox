using CRTVUP.Unify.LocalPdf.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace CRTVUP.Unify.LocalPdf.Implement;

public static class ImagePdfBuilder
{
    /// <summary>用已经重新编码的 JPEG 帧构建 PDF；用于把 PDF 压成图片版。</summary>
    public static byte[] BuildJpeg(IReadOnlyList<JpegPdfPage> pages, CancellationToken cancellationToken = default)
    {
        if (pages is null || pages.Count == 0) throw new PdfToolException("没有可写入的页面图片。");
        using var builder = new PdfDocumentBuilder { IncludeDocumentInformation = false };
        foreach (var pageImage in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageImage.Jpeg is not { Length: > 0 }) throw new PdfToolException("页面图片为空，无法生成 PDF。");
            if (!double.IsFinite(pageImage.Width) || !double.IsFinite(pageImage.Height)
                || pageImage.Width < 1 || pageImage.Height < 1 || pageImage.Width > 14400 || pageImage.Height > 14400)
                throw new PdfToolException("页面尺寸无效，无法生成 PDF。");
            var page = builder.AddPage(pageImage.Width, pageImage.Height);
            page.AddJpeg(pageImage.Jpeg, new PdfRectangle(0, 0, pageImage.Width, pageImage.Height));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Build();
    }

    /// <summary>调用方提供重新编码的 PNG 帧，页面大小以点为单位，不带原图 EXIF。</summary>
    public static byte[] Build(IEnumerable<ConversionPage> images, CancellationToken cancellationToken = default)
    {
        using var builder = new PdfDocumentBuilder { IncludeDocumentInformation = false };
        var count = 0;
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 500 || image.Png is not { Length: > 0 }) throw new PdfToolException("图片转 PDF 一次最多支持 500 页，且图片不能为空。");
            if (!double.IsFinite(image.Width) || !double.IsFinite(image.Height) || image.Width < 1 || image.Height < 1 || image.Width > 14400 || image.Height > 14400)
                throw new PdfToolException("图片页面尺寸无效。");
            var page = builder.AddPage(image.Width, image.Height);
            page.AddPng(image.Png, new PdfRectangle(0, 0, image.Width, image.Height));
        }
        if (count == 0) throw new PdfToolException("请选择至少一张图片。");
        cancellationToken.ThrowIfCancellationRequested();
        var result = builder.Build();
        return result;
    }
}
