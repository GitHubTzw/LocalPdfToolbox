using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// V2 主窗口：左侧是渐变导航栏，右侧一次只显示一个功能区。
/// 拖文件到窗口任意位置都会弹出预览浮层，松手后按文件类型自动送到对应功能区。
/// 所有处理都在本机完成，不联网、不写使用历史。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>导航项顺序与功能区一一对应，标题和说明也在这里维护。</summary>
    private static readonly (string Title, string Hint)[] Sections =
    [
        ("PDF 工作台", "把 PDF 拖进窗口即可开始；列表顺序就是导出顺序，双击页面可直接改内容。"),
        ("JSON / XML", "格式化、压缩、校验，JSON / XML / CSV 互转，还能按结构生成六种语言的模型类。"),
        ("编码转换", "Base64、哈希、HMAC、AES、JWT、进制、颜色、命名风格、正则与时间戳，全部在本机换算。"),
        ("文件对比", "文本逐行并排对比、二进制字节对比、目录递归对比，可导出 Markdown 报告。"),
        ("文件压缩", "把 PDF、图片和 Office 文档的体积压小（不是打包 ZIP）；压不下去会如实告诉你。")
    ];

    private string? _previewPath;
    private BitmapSource? _previewImage;

    public MainWindow()
    {
        InitializeComponent();
        Nav.SelectedIndex = 0;
    }

    /// <summary>功能区数量，供测试核对导航完整性。</summary>
    public int SectionCount => Sections.Length;

    /// <summary>当前显示的功能区下标；设置时会同步左侧导航。</summary>
    public int SelectedSection
    {
        get => Nav.SelectedIndex;
        set => Nav.SelectedIndex = value;
    }

    /// <summary>按标题切换功能区，找不到时保持原状。</summary>
    public void SelectSection(string title)
    {
        for (var index = 0; index < Sections.Length; index++)
            if (Sections[index].Title == title) { Nav.SelectedIndex = index; return; }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化阶段控件树还没建好，此时只依赖 XAML 里的默认可见性。
        if (HeaderTitle is null || PdfWorkspace is null) return;
        var index = Math.Clamp(Nav.SelectedIndex, 0, Sections.Length - 1);
        PdfWorkspace.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        JsonTools.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        EncodingTools.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        DiffTools.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        CompressTools.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        HeaderTitle.Text = Sections[index].Title;
        HeaderHint.Text = Sections[index].Hint;
    }

    // ---------------------------------------------------------------- 拖拽导入

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        var accepted = files.Length > 0 && !PdfWorkspace.IsBusy;
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!accepted) { HideDropOverlay(); return; }
        ShowDropOverlay(files);
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedFiles(e);
        HideDropOverlay();
        if (files.Length == 0 || PdfWorkspace.IsBusy) return;

        // 按文件类型送到最合适的功能区，用户不用自己找。
        var target = Route(files);
        Nav.SelectedIndex = target.Section;
        switch (target.Section)
        {
            case 0:
                await PdfWorkspace.ImportFilesAsync(target.Files);
                break;
            case 1:
                JsonTools.LoadFiles(target.Files);
                break;
            case 3:
                DiffTools.LoadFiles(target.Files);
                break;
            case 4:
                CompressTools.AddFiles(target.Files);
                break;
        }
    }

    /// <summary>拖拽分流：PDF 进工作台，两个文本进对比，压缩类文件进压缩页。</summary>
    private static (int Section, string[] Files) Route(string[] files)
    {
        if (files.Any(file => Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            return (0, files);
        var text = files.Where(IsTextFile).ToArray();
        if (text.Length == files.Length)
            return (files.Length >= 2 ? 3 : 1, files);
        return (4, files);
    }

    private static bool IsTextFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".json" or ".xml" or ".csv" or ".txt" or ".log" or ".md" or ".yaml" or ".yml" or ".ini" or ".config";

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : [];

    // ---------------------------------------------------------------- 拖拽预览浮层

    /// <summary>拖动过程中显示浮层：说明会送到哪个功能区，并对单个文件给出缩略图。</summary>
    private void ShowDropOverlay(string[] files)
    {
        DropOverlay.Visibility = Visibility.Visible;
        var target = Route(files);
        var name = target.Section switch
        {
            0 => "PDF 工作台",
            1 => "JSON / XML",
            3 => "文件对比",
            4 => "文件压缩",
            _ => "本地工具箱"
        };
        DropTitle.Text = files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} 个文件";
        DropDetail.Text = $"松开后会在「{name}」中打开。文件只在本机处理，不会上传。";

        // 单个文件才做缩略图，多个文件时只提示数量，避免拖动时反复解码。
        if (files.Length != 1) { ClearPreview(); return; }
        if (string.Equals(_previewPath, files[0], StringComparison.OrdinalIgnoreCase) && _previewImage is not null)
        {
            DropPreviewCard.Visibility = Visibility.Visible;
            return;
        }
        _previewPath = files[0];
        _previewImage = TryBuildPreview(files[0]);
        DropPreviewImage.Source = _previewImage;
        DropPreviewCard.Visibility = _previewImage is null ? Visibility.Collapsed : Visibility.Visible;
        DropPreviewCaption.Text = _previewImage is null ? "" : Path.GetFileName(files[0]);
    }

    /// <summary>生成拖拽缩略图：PDF 渲染第一页，图片直接解码；其他类型返回 null。</summary>
    private static BitmapSource? TryBuildPreview(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pdf")
            {
                var bytes = LocalFileStore.Read(path);
                try
                {
                    using var document = PdfiumRuntime.Open(bytes);
                    if (document.PageCount < 1) return null;
                    var bitmap = document.Render(1, maxWidth: 420, maxHeight: 300);
                    try { return ToImage(bitmap); }
                    finally { CryptographicOperations.ZeroMemory(bitmap.Bgra); }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff")
            {
                using var stream = File.OpenRead(path);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = 420;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        catch (PdfToolException) { }
        catch (IOException) { }
        catch (NotSupportedException) { }
        return null;
    }

    private static BitmapSource ToImage(PdfBitmap bitmap)
    {
        var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null,
            bitmap.Bgra, bitmap.Stride);
        image.Freeze();
        return image;
    }

    private void HideDropOverlay()
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        ClearPreview();
    }

    private void ClearPreview()
    {
        _previewPath = null;
        _previewImage = null;
        DropPreviewImage.Source = null;
        DropPreviewCard.Visibility = Visibility.Collapsed;
    }

    private void Window_Closing(object sender, CancelEventArgs e) => PdfWorkspace.Close();
}
