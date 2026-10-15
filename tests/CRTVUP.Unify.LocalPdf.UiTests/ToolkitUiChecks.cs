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
        // 测试宿主跑在 V1 的 Application 上，而 V1 把按钮、输入框、窗口样式直接写在
        // Application.Resources 里。自有条目的优先级高于合并字典，会把 V2 主题整个盖掉，
        // 导致截图和断言看到的其实是 V1 的样式。这里先把 V1 的自有条目挪开，跑完再原样放回。
        var application = Application.Current;
        var shadowedKeys = application.Resources.Keys.Cast<object>().ToArray();
        var shadowedValues = shadowedKeys.ToDictionary(key => key, key => application.Resources[key]);
        foreach (var key in shadowedKeys) application.Resources.Remove(key);
        var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/LocalPdfV2;component/Theme.xaml") };
        application.Resources.MergedDictionaries.Add(theme);
        try
        {
            await CheckMainWindowAsync(outputDirectory);
        }
        finally
        {
            application.Resources.MergedDictionaries.Remove(theme);
            foreach (var pair in shadowedValues) application.Resources[pair.Key] = pair.Value;
        }
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
            var nav = (ListBox)window.FindName("Nav");
            Check(window.SectionCount == 5 && nav.Items.Count == 5, "V2 主窗口包含五个功能区");
            var titles = nav.Items.OfType<ListBoxItem>().Select(item => item.Content?.ToString() ?? "").ToArray();
            Check(titles.SequenceEqual(["PDF 工作台", "JSON / XML", "编码转换", "文件对比", "文件压缩"]),
                "V2 导航标题与顺序正确");
            Check(nav.SelectedIndex == 0, "V2 启动时默认停在 PDF 工作台");
            Check(((TextBlock)window.FindName("HeaderTitle")).Text == "PDF 工作台", "V2 标题条跟随导航切换");
            // 拖拽浮层默认收起，拖动文件时才出现。
            Check(((FrameworkElement)window.FindName("DropOverlay")).Visibility == Visibility.Collapsed,
                "V2 拖拽提示浮层默认隐藏");

            var pdfTab = (V2.Views.PdfTab)window.FindName("PdfWorkspace");
            await pdfTab.ImportFilesAsync([pdfPath]);
            Check(pdfTab.Pages.Count == 1 && pdfTab.Sources.Count == 1, "V2 PDF 页签可导入合成文档");
            Check(((ListBox)pdfTab.FindName("PageList")).Items.Count == 1, "V2 PDF 页签显示页面列表");
            Layout(window, 1320, 860);
            // 主操作按钮必须是 V2 的强调色；如果被同名样式盖掉，这里会立刻失败。
            var exportButton = (Button)pdfTab.FindName("ExportButton");
            Check(exportButton.Background is SolidColorBrush accent && accent.Color == Color.FromRgb(0x6C, 0x71, 0xED),
                "V2 主操作按钮使用 V2 强调色");
            // 操作条上的分组竖线要有实际高度，否则会被 WrapPanel 压成 0 高度而看不见。
            Check(((Border)pdfTab.FindName("PageGroupSeparator")).ActualHeight > 0, "V2 操作条分组竖线可见");
            // 低频的文档工具收进「更多工具」菜单，避免操作条上堆满按钮。
            var moreMenu = ((Button)pdfTab.FindName("MoreToolsButton")).ContextMenu;
            Check(moreMenu is not null && moreMenu.Items.OfType<MenuItem>().Count() == 5, "V2 更多工具菜单包含五项文档工具");
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
            for (var index = 0; index < nav.Items.Count; index++)
            {
                nav.SelectedIndex = index;
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

        CheckImageShrinks(outputDirectory);
    }

    /// <summary>
    /// 图片压缩回归：平滑的大 PNG 必须真正变小（旧实现里 PNG 质量参数无效，33 MB 进 33 MB 出），
    /// 已经压得很小的 JPEG 不能再被撑大，压不下去时必须放弃写入而不是产出更大的文件。
    /// </summary>
    private static void CheckImageShrinks(string outputDirectory)
    {
        var compressor = new V2.LocalFileCompressor();

        // 平滑渐变 + 细噪声：模拟真实照片，旧实现在这里几乎压不动。
        var photo = Path.Combine(outputDirectory, "v2-压缩-照片.png");
        WriteGradientPng(photo, 2400, 1800);
        var photoHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        var photoResult = compressor.Compress(photo, Path.Combine(outputDirectory, "v2-压缩-照片-输出.png"),
            new CompressOptions());
        Check(photoResult.WroteOutput && photoResult.CompressedBytes < photoResult.OriginalBytes,
            $"大 PNG 压缩后必须变小（{photoResult.OriginalBytes} → {photoResult.CompressedBytes}）");
        Check(photoResult.SavedPercent > 20, $"大 PNG 压缩幅度应明显（实际 {photoResult.SavedPercent:0.#}%）");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo))) == photoHash, "图片压缩未改动源 PNG");

        // 输出必须是能正常解码的图片，尺寸和原图一致。
        using (var stream = File.OpenRead(photoResult.OutputPath))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Count == 1 && decoder.Frames[0].PixelWidth == 2400 && decoder.Frames[0].PixelHeight == 1800,
                "压缩后的 PNG 可正常解码且尺寸不变");
        }

        // 已经压到很小的 JPEG：不允许产出更大的文件。
        var small = Path.Combine(outputDirectory, "v2-压缩-小图.jpg");
        WriteGradientJpeg(small, 1600, 1200, quality: 40);
        var smallResult = compressor.Compress(small, Path.Combine(outputDirectory, "v2-压缩-小图-输出.jpg"),
            new CompressOptions { Quality = 85 });
        Check(smallResult.CompressedBytes <= smallResult.OriginalBytes,
            $"压缩结果不得大于原文件（{smallResult.OriginalBytes} → {smallResult.CompressedBytes}）");
        if (!smallResult.WroteOutput)
            Check(smallResult.OutputPath.Length == 0, "放弃写入时不应留下输出路径");

        // 无损重编码也必须比 WPF 自带编码器更小：同一张图用两种方式各写一次做对比。
        var reference = Path.Combine(outputDirectory, "v2-压缩-照片-参考.png");
        using (var stream = File.OpenRead(photo))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var output = new FileStream(reference, FileMode.CreateNew, FileAccess.Write);
            encoder.Save(output);
        }
        Check(new FileInfo(photoResult.OutputPath).Length < new FileInfo(reference).Length,
            "本机 PNG 编码器应优于系统默认编码器");
    }

    /// <summary>合成一张平滑渐变加细噪声的大图，PNG 体积会很大，用来验证压缩率。</summary>
    private static void WriteGradientPng(string path, int width, int height)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        var seed = 987654321u;
        for (var y = 0; y < height; y++)
        {
            var offset = y * stride;
            for (var x = 0; x < width; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                var noise = (int)((seed >> 24) & 0x1F) - 16;
                pixels[offset] = Clamp(140 + 80 * Math.Cos(x * 0.0013 - y * 0.0011) + noise);
                pixels[offset + 1] = Clamp(120 + 90 * Math.Sin((x + y) * 0.0017) + noise);
                pixels[offset + 2] = Clamp(128 + 100 * Math.Sin(x * 0.0021) + noise);
                offset += 3;
            }
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(output);

        static byte Clamp(double value) => (byte)Math.Clamp((int)value, 0, 255);
    }

    /// <summary>合成一张已经压过的 JPEG，用来验证压缩不会把文件撑大。</summary>
    private static void WriteGradientJpeg(string path, int width, int height, int quality)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        var seed = 24681357u;
        for (var y = 0; y < height; y++)
        {
            var offset = y * stride;
            for (var x = 0; x < width; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                var noise = (int)((seed >> 24) & 0x1F) - 16;
                pixels[offset] = (byte)Math.Clamp((int)(140 + 80 * Math.Cos(x * 0.0013 - y * 0.0011)) + noise, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp((int)(120 + 90 * Math.Sin((x + y) * 0.0017)) + noise, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp((int)(128 + 100 * Math.Sin(x * 0.0021)) + noise, 0, 255);
                offset += 3;
            }
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(output);
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
