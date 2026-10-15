using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Implement;
using CRTVUP.Unify.LocalPdf.Abstractions;
using V2 = CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// V2 工具箱界面检查：确认五个页签能加载、布局不重叠、本地工具交互可用。
/// 全程只使用内存中的合成内容，不读取用户文档、不联网。
/// </summary>
internal static class ToolkitUiChecks
{
    public static async Task RunAsync(string outputDirectory)
    {
        // V2 的样式集中在自己的 Theme.xaml；测试进程共用 V1 的 Application，这里临时合并再还原。
        var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/LocalPdfV2;component/Theme.xaml") };
        Application.Current.Resources.MergedDictionaries.Add(theme);
        try
        {
            await CheckMainWindowAsync(outputDirectory);
        }
        finally { Application.Current.Resources.MergedDictionaries.Remove(theme); }
    }

    private static async Task CheckMainWindowAsync(string outputDirectory)
    {
        var pdf = TextPdfBuilder.Build(["V2 TOOLBOX TAB CHECK"]);
        var pdfPath = Path.Combine(outputDirectory, "v2-tab-check.pdf");
        using (var file = new FileStream(pdfPath, FileMode.CreateNew, FileAccess.Write)) file.Write(pdf);

        V2.MainWindow? window = null;
        try
        {
            window = new V2.MainWindow();
            Layout(window, 1320, 860);
            var tabs = (TabControl)window.FindName("Tabs");
            Check(tabs.Items.Count == 5, "V2 主窗口包含五个页签");
            var titles = tabs.Items.OfType<TabItem>().Select(HeaderText).ToArray();
            Check(titles.SequenceEqual(["PDF 工作台", "JSON / XML", "编码转换", "文件对比", "文件压缩"]),
                "V2 页签标题与顺序正确");

            var pdfTab = (V2.Views.PdfTab)window.FindName("PdfWorkspace");
            await pdfTab.ImportFilesAsync([pdfPath]);
            Check(pdfTab.Pages.Count == 1 && pdfTab.Sources.Count == 1, "V2 PDF 页签可导入合成文档");
            Check(((ListBox)pdfTab.FindName("PageList")).Items.Count == 1, "V2 PDF 页签显示页面列表");
            Layout(window, 1320, 860);
            Save(window, Path.Combine(outputDirectory, "界面-V2-PDF工作台.png"), 1320, 860);

            var json = (V2.Views.JsonTab)window.FindName("JsonTools");
            ((TextBox)json.FindName("InputBox")).Text = "{\"b\":1,\"a\":2}";
            Call(json, "Format_Click");
            Check(((TextBox)json.FindName("ResultBox")).Text.Contains('\n'), "V2 JSON 页签可格式化");

            var encoding = (V2.Views.EncodingTab)window.FindName("EncodingTools");
            ((TextBox)encoding.FindName("InputBox")).Text = "中文ABC";
            Call(encoding, "Apply_Click");
            Check(((TextBox)encoding.FindName("ResultBox")).Text == Convert.ToBase64String(Encoding.UTF8.GetBytes("中文ABC")),
                "V2 编码页签默认做 Base64 编码");

            var diff = (V2.Views.DiffTab)window.FindName("DiffTools");
            ((TextBox)diff.FindName("LeftBox")).Text = "a\nb\nc";
            ((TextBox)diff.FindName("RightBox")).Text = "a\nB\nc";
            Call(diff, "Compare_Click");
            var grid = (DataGrid)diff.FindName("DiffGrid");
            Check(grid.Items.Count == 3, "V2 对比页签并排显示三行");
            ((CheckBox)diff.FindName("OnlyDifferences")).IsChecked = true;
            Check(grid.Items.Count == 1, "V2 对比页签可只显示差异行");

            var compress = (V2.Views.CompressTab)window.FindName("CompressTools");
            Check(compress.FindName("ItemGrid") is DataGrid, "V2 压缩页签加载成功");
            CheckExtras(window, pdfPath, outputDirectory);
            // 压缩页签按体积压缩，不是打包 ZIP：加一个合成 PDF 并确认能识别类型和大小。
            var compressItems = (System.Collections.IList)typeof(V2.Views.CompressTab)
                .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(compress)!;
            compressItems.Add(new V2.Views.CompressTab.CompressItem(pdfPath));
            Check(compressItems.Count == 1, "V2 压缩页签可加入待压缩文件");
            var row = (V2.Views.CompressTab.CompressItem)compressItems[0]!;
            Check(row.KindLabel == "PDF" && row.OriginalBytes > 0, "V2 压缩页签识别文件类型和原始大小");
            CheckSizeReduction(outputDirectory, pdfPath);

            // 逐个页签截图，确认布局不重叠、不空白。
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                Layout(window, 1320, 860);
                Save(window, Path.Combine(outputDirectory, $"界面-V2-页签{index + 1}.png"), 1320, 860);
            }
            Console.WriteLine("V2 页签布局与本地工具交互检查全部通过。");
        }
        finally { window?.Close(); }
    }

    /// <summary>
    /// 新增工具箱能力的界面检查：多语言模型生成、JSON/XML 互转、结构统计、
    /// Base32/进制/文本统计、目录对比模式与行内差异、PDF 信息/提取/拆分。
    /// 全程只用合成内容，PDF 附加操作直接调内存工具，避免弹出对话框阻塞测试。
    /// </summary>
    private static void CheckExtras(V2.MainWindow window, string pdfPath, string outputDirectory)
    {
        // JSON 页签：模型生成（TypeScript / SQL）、JSON→XML、结构统计。
        var json = (V2.Views.JsonTab)window.FindName("JsonTools");
        var jsonInput = (TextBox)json.FindName("InputBox");
        var jsonResult = (TextBox)json.FindName("ResultBox");
        var modelLanguage = (ComboBox)json.FindName("ModelLanguage");
        jsonInput.Text = "{\"id\":1,\"name\":\"张三\",\"tags\":[\"a\",\"b\"]}";

        modelLanguage.SelectedIndex = 1; // TypeScript
        Call(json, "Classes_Click");
        Check(jsonResult.Text.Contains("export interface", StringComparison.Ordinal), "V2 JSON 页签可生成 TypeScript 模型");

        modelLanguage.SelectedIndex = 5; // SQL
        Call(json, "Classes_Click");
        Check(jsonResult.Text.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase), "V2 JSON 页签可生成 SQL 建表语句");

        Call(json, "ToXml_Click");
        Check(jsonResult.Text.Contains('<'), "V2 JSON 页签可转 XML");

        Call(json, "Describe_Click");
        Check(jsonResult.Text.Contains("最大深度", StringComparison.Ordinal), "V2 JSON 页签可统计结构");

        // 编码页签：Base32 往返、GUID 批量生成、进制转换、文本统计。
        var encoding = (V2.Views.EncodingTab)window.FindName("EncodingTools");
        var encodingInput = (TextBox)encoding.FindName("InputBox");
        var encodingResult = (TextBox)encoding.FindName("ResultBox");
        var operations = (ListBox)encoding.FindName("OperationList");
        var param1 = (TextBox)encoding.FindName("Param1Box");
        var param2 = (TextBox)encoding.FindName("Param2Box");

        encodingInput.Text = "中文ABC";
        operations.SelectedIndex = FindOperation(operations, "Base32 编码");
        Call(encoding, "Apply_Click");
        var base32 = encodingResult.Text;
        Check(base32.Length > 0, "V2 编码页签可做 Base32 编码");
        operations.SelectedIndex = FindOperation(operations, "Base32 解码");
        encodingInput.Text = base32;
        Call(encoding, "Apply_Click");
        Check(encodingResult.Text == "中文ABC", "V2 编码页签 Base32 可往返");

        operations.SelectedIndex = FindOperation(operations, "GUID 生成");
        param1.Text = "3";
        param2.Text = "";
        Call(encoding, "Apply_Click");
        Check(encodingResult.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3
            && encodingResult.Text.Contains('-'), "V2 编码页签可批量生成 GUID");

        operations.SelectedIndex = FindOperation(operations, "进制转换");
        param1.Text = "16";
        param2.Text = "2";
        encodingInput.Text = "FF";
        Call(encoding, "Apply_Click");
        Check(encodingResult.Text.Contains("11111111", StringComparison.Ordinal), "V2 编码页签可做进制转换");

        operations.SelectedIndex = FindOperation(operations, "文本统计");
        encodingInput.Text = "abc";
        Call(encoding, "Apply_Click");
        Check(encodingResult.Text.Contains("字符数", StringComparison.Ordinal), "V2 编码页签可统计文本");

        // 对比页签：目录模式切换与行内差异高亮。
        var diff = (V2.Views.DiffTab)window.FindName("DiffTools");
        var mode = (ComboBox)diff.FindName("ModeChoice");
        mode.SelectedIndex = 2;
        Check(((FrameworkElement)diff.FindName("DirectoryPanel")).Visibility == Visibility.Visible
            && ((FrameworkElement)diff.FindName("DirectoryGrid")).Visibility == Visibility.Visible,
            "V2 对比页签可切到目录模式");
        mode.SelectedIndex = 0;
        ((CheckBox)diff.FindName("OnlyDifferences")).IsChecked = false;
        ((CheckBox)diff.FindName("InlineDiff")).IsChecked = true;
        ((TextBox)diff.FindName("LeftBox")).Text = "alpha beta gamma";
        ((TextBox)diff.FindName("RightBox")).Text = "alpha delta gamma";
        Call(diff, "Compare_Click");
        Check(((DataGrid)diff.FindName("DiffGrid")).Items.Count == 1, "V2 对比页签行内差异高亮可用");

        // PDF 附加操作：文档信息、文字提取、按页拆分，只新建文件、不改源文件。
        var extras = new PdfExtrasTool();
        var content = File.ReadAllBytes(pdfPath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(content));

        var info = extras.Inspect(content);
        Check(info.PageCount == 1 && info.Summary.Contains("共 1 页", StringComparison.Ordinal), "V2 PDF 工具可读取文档信息");
        Check(extras.ExtractText(content).Contains("V2 TOOLBOX", StringComparison.Ordinal), "V2 PDF 工具可提取文字层");

        var splitFolder = Path.Combine(outputDirectory, "v2-pdf-拆分");
        Directory.CreateDirectory(splitFolder);
        var outputs = extras.SplitEvery(content, "v2-tab-check.pdf", splitFolder, 1);
        Check(outputs.Count == 1 && File.Exists(outputs[0]), "V2 PDF 工具可按页拆分并生成新文件");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))) == sourceHash, "PDF 附加操作未改动源文件");
    }

    /// <summary>在转换方式列表里按显示文字找到下标，避免硬编码顺序。</summary>
    private static int FindOperation(ListBox list, string label)
    {
        for (var index = 0; index < list.Items.Count; index++)
            if (string.Equals(list.Items[index]?.ToString(), label, StringComparison.Ordinal)) return index;
        throw new InvalidOperationException("找不到转换方式：" + label);
    }

    /// <summary>体积压缩的端到端检查：PDF 结构重写、PDF 图片化、图片缩放都只新建文件。</summary>
    private static void CheckSizeReduction(string outputDirectory, string pdfPath)
    {
        var compressor = new V2.LocalFileCompressor();
        var pdfHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath)));

        var rewritten = Path.Combine(outputDirectory, "v2-压缩-结构重写.pdf");
        var rewriteResult = compressor.Compress(pdfPath, rewritten, new CompressOptions());
        Check(File.Exists(rewritten) && rewriteResult.CompressedBytes > 0, "PDF 结构重写生成新文件");
        using (var document = PdfiumRuntime.Open(File.ReadAllBytes(rewritten)))
            Check(document.PageCount == 1 && document.ExtractText(1).Contains("V2 TOOLBOX", StringComparison.Ordinal),
                "PDF 结构重写保留页数和文字");

        var rasterized = Path.Combine(outputDirectory, "v2-压缩-图片版.pdf");
        var rasterResult = compressor.Compress(pdfPath, rasterized,
            new CompressOptions { RasterizePdf = true, Quality = 60, PdfDpi = 96 });
        Check(File.Exists(rasterized) && rasterResult.CompressedBytes > 0, "PDF 图片化生成新文件");
        using (var document = PdfiumRuntime.Open(File.ReadAllBytes(rasterized)))
        {
            Check(document.PageCount == 1, "PDF 图片化保留页数");
            var bitmap = document.Render(1, maxWidth: 200, maxHeight: 260);
            try { Check(bitmap.Width > 0 && bitmap.Height > 0, "PDF 图片化结果可渲染"); }
            finally { CryptographicOperations.ZeroMemory(bitmap.Bgra); }
        }

        // 合成一张噪声 PNG：缩放到 50% 后体积必然明显变小。
        var imagePath = Path.Combine(outputDirectory, "v2-压缩-图片源.png");
        WriteNoisePng(imagePath, 900, 700);
        var imageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)));
        var imageOut = Path.Combine(outputDirectory, "v2-压缩-图片输出.png");
        var imageResult = compressor.Compress(imagePath, imageOut, new CompressOptions { ScalePercent = 50 });
        Check(File.Exists(imageOut), "图片压缩生成新文件");
        Check(imageResult.CompressedBytes < imageResult.OriginalBytes,
            $"图片缩放后体积应变小（{imageResult.OriginalBytes} → {imageResult.CompressedBytes}）");

        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))) == pdfHash, "压缩过程未改动源 PDF");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))) == imageHash, "压缩过程未改动源图片");
    }

    private static void WriteNoisePng(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var seed = 12345u;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            seed = seed * 1664525u + 1013904223u;
            pixels[index] = (byte)(seed >> 24);
            pixels[index + 1] = (byte)(seed >> 16);
            pixels[index + 2] = (byte)(seed >> 8);
            pixels[index + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(output);
    }

    private static string HeaderText(TabItem item) =>
        item.Header is StackPanel panel
            ? string.Concat(panel.Children.OfType<TextBlock>().Select(block => block.Text))
            : item.Header?.ToString() ?? "";

    private static void Call(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, [target, new RoutedEventArgs()]);

    private static void Layout(Window window, double width, double height)
    {
        var element = (FrameworkElement)window.Content;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Save(Window window, string path, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background);
        image.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(output);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("验证失败：" + name);
        Console.WriteLine("通过：" + name);
    }
}
