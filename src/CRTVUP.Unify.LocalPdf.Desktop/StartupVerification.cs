using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.Desktop;

/// <summary>随发布执行的合成启动检查；仅返回退出码，不接收文档或输出路径。</summary>
internal static class StartupVerification
{
    internal static bool Run()
    {
        byte[]? pdf = null;
        MainWindow? window = null;
        try
        {
            var runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "runtime")) + Path.DirectorySeparatorChar;
            // 核心运行库和 WPF 必须从随附目录载入，不能依赖开发机全局安装。
            if (!RuntimeEnvironment.GetRuntimeDirectory().StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase)
                || !typeof(Window).Assembly.Location.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase)) return false;
            pdf = TextPdfBuilder.Build(["LOCAL STARTUP VERIFICATION"]);
            using var document = PdfiumRuntime.Open(pdf);
            if (!document.ExtractText(1).Contains("LOCAL STARTUP VERIFICATION", StringComparison.Ordinal)) return false;
            var bitmap = document.Render(1, maxWidth: 160, maxHeight: 200);
            try { if (bitmap.Width < 1 || bitmap.Height < 1) return false; }
            finally { CryptographicOperations.ZeroMemory(bitmap.Bgra); }
            window = new MainWindow();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1240, 760));
            content.Arrange(new Rect(0, 0, 1240, 760));
            content.UpdateLayout();
            return window.Icon is not null;
        }
        catch { return false; }
        finally
        {
            window?.Close();
            if (pdf is not null) CryptographicOperations.ZeroMemory(pdf);
        }
    }
}
