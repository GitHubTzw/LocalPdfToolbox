namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>页码表达式解析；直接复用 V1 已通过测试的实现，避免两套规则。</summary>
internal static class PageRange
{
    public static IReadOnlyList<int> Parse(string text, int pageCount) =>
        CRTVUP.Unify.LocalPdf.Desktop.MainWindow.ParsePageRange(text, pageCount);
}
