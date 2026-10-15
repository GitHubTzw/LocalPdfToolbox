using System.Windows;

namespace CRTVUP.Unify.LocalPdf.Desktop;

public partial class App : Application
{
    public bool LaunchMainWindow { get; init; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("操作未能完成。原文件未修改，请关闭程序后重新尝试。", "本地 PDF 工作台", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        base.OnStartup(e);
        // 发布检查只使用内存中的合成内容，不读取用户文件、不显示窗口、不写日志。
        if (e.Args is ["--verify-local-startup"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(StartupVerification.Run() ? 0 : 1);
            return;
        }
        if (LaunchMainWindow)
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
