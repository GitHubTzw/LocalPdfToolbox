using System.ComponentModel;
using System.Windows;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// V2 主窗口：顶部品牌栏加页签容器，把 PDF 工作台和本地开发工具箱放在同一个窗口里。
/// PDF 会话、路径策略和导出规则与 V1 完全一致，V1 程序保持独立可用。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>把文件拖到窗口任意位置时，都交给 PDF 工作台处理。</summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !PdfWorkspace.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (PdfWorkspace.IsBusy || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        Tabs.SelectedIndex = 0;
        await PdfWorkspace.ImportFilesAsync(files);
    }

    private void Window_Closing(object sender, CancelEventArgs e) => PdfWorkspace.Close();
}
