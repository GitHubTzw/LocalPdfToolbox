using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Desktop;
using CRTVUP.Unify.LocalPdf.Implement;
using UglyToad.PdfPig.Core;
using PdfPoint = UglyToad.PdfPig.Core.PdfPoint;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        var outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/verification");
        Directory.CreateDirectory(outputDirectory);
        using var networkEvents = new NetworkEvents();
        var application = new App { LaunchMainWindow = false };
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = application.Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            try
            {
                Check(MainWindow.ParsePageRange("1-3，5,3", 5).SequenceEqual([1, 2, 3, 5]), "页码解析去重");
                foreach (var bad in new[] { "0", "6", "3-1", "1-2-3", "1,", "9999999999999" })
                {
                    try { MainWindow.ParsePageRange(bad, 5); throw new InvalidOperationException("接受了无效页码"); }
                    catch (ArgumentException) { }
                }

                // 文字编辑/互转仅在内存中完成：TXT、DOCX、XLSX 都应能回读文字层，且不依赖 Office 或网络。
                var editableText = new ImportedText(["标题\n第一行\n第二行", "工作表\nA\tB"], "合成文字");
                foreach (var format in new[] { PdfExportFormat.Text, PdfExportFormat.WordText, PdfExportFormat.ExcelText })
                {
                    var converted = LocalConversionService.ExportText(editableText.Sections, format, CancellationToken.None);
                    Check(converted.Length > 0, $"文字编辑后导出 {format}");
                    if (format == PdfExportFormat.Text)
                    {
                        var roundTrip = LocalTextImporter.Read(converted, ".txt");
                        Check(roundTrip.Sections[0].Contains("第一行", StringComparison.Ordinal), "TXT 文字层可回读");
                    }
                    else
                    {
                        var extension = format == PdfExportFormat.WordText ? ".docx" : ".xlsx";
                        var roundTrip = LocalTextImporter.Read(converted, extension);
                        Check(roundTrip.Sections.Any(section => section.Contains("标题", StringComparison.Ordinal) || section.Contains("工作表", StringComparison.Ordinal)), $"{format} 文字层可回读");
                    }
                    CryptographicOperations.ZeroMemory(converted);
                }
                var textEditor = new TextContentEditorWindow(editableText);
                try
                {
                    LayoutElement(textEditor, new Size(980, 740));
                    SaveElement(textEditor, Path.Combine(outputDirectory, "界面-文字编辑转换.png"), 980, 740);
                    var sections = (ListBox)textEditor.FindName("SectionList");
                    var textInput = (TextBox)textEditor.FindName("SectionText");
                    Check(sections.Items.Count == 2, "文字编辑窗口显示分节");
                    textInput.Text = "EDITED FIRST SECTION\n修改后的第一节";
                    sections.SelectedIndex = 1;
                    Check(textInput.Text.Contains("工作表", StringComparison.Ordinal), "切换分节显示对应文字");
                    textInput.Text = "EDITED SECOND SECTION";
                    sections.SelectedIndex = 0;
                    Check(textInput.Text.StartsWith("EDITED FIRST SECTION", StringComparison.Ordinal), "切换分节保留已编辑文字");
                    Check(!textInput.CanUndo, "切换分节不会撤销出其他节的文字");
                    var editedPdf = await textEditor.BuildPdfAsync();
                    try
                    {
                        using var document = PdfiumRuntime.Open(editedPdf);
                        Check(document.PageCount == 2, "修改后的多节文字生成分页 PDF");
                        Check(document.ExtractText(1).Contains("EDITED FIRST SECTION", StringComparison.Ordinal)
                            && document.ExtractText(2).Contains("EDITED SECOND SECTION", StringComparison.Ordinal), "PDF 使用各分节修改后的文字");
                    }
                    finally { CryptographicOperations.ZeroMemory(editedPdf); }
                    LayoutElement(textEditor, new Size(780, 620));
                    SaveElement(textEditor, Path.Combine(outputDirectory, "界面-文字编辑转换-最小尺寸.png"), 780, 620);
                    Check(((Button)textEditor.FindName("BuildPdfButton")).TransformToAncestor((FrameworkElement)textEditor.Content).Transform(new Point()).X >= 0, "最小宽度显示生成 PDF 按钮");
                }
                finally
                {
                    // 模拟已确认丢弃合成测试输入，避免无人值守测试触发关闭确认框。
                    typeof(TextContentEditorWindow).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(textEditor, true);
                    textEditor.Close();
                }
                var converter = new ConversionWindow(0, _ => throw new InvalidOperationException("空工作区不应请求 PDF"));
                try
                {
                    Check(!((Button)converter.FindName("ConvertExport")).IsEnabled, "空工作区禁用 PDF 导出");
                    ((ComboBox)converter.FindName("ImportKind")).SelectedIndex = 1;
                    Check(((Button)converter.FindName("ImportConvert")).Content.ToString()!.Contains("编辑", StringComparison.Ordinal), "Office/TXT 导入入口提示可先编辑");
                    LayoutElement(converter, new Size(840, 700));
                    SaveElement(converter, Path.Combine(outputDirectory, "界面-格式转换.png"), 840, 700);
                }
                finally { converter.Close(); }

                var sourceA = Path.Combine(outputDirectory, "项目说明.pdf");
                var sourceB = Path.Combine(outputDirectory, "会议纪要.pdf");
                // 测试目录由调用方指定；始终 CreateNew，测试也不能覆盖现存文件。
                CreateFixture(sourceA, "PROJECT BRIEF", 3, false);
                CreateFixture(sourceB, "MEETING NOTES", 2, true);
                var syntheticBytes = LocalFileStore.Read(sourceA);
                try
                {
                    ConversionIntegrationChecks.Run(syntheticBytes, outputDirectory);
                    var contentEditor = new ContentEditorWindow(syntheticBytes, 1, 0);
                    try
                    {
                        await contentEditor.RefreshAsync();
                        var preview = (Image)contentEditor.FindName("PageImage");
                        var initialImage = (BitmapSource)preview.Source;
                        using var originalDocument = PdfiumRuntime.Open(syntheticBytes);
                        var textObject = originalDocument.EnumerateObjects(1, 0).Objects.First(item => item.Kind == PdfObjectKind.Text && item.CanEdit);
                        await contentEditor.ApplyEditAsync(new PdfEditRequest
                        {
                            PageNumber = 1, Kind = PdfEditKind.ReplaceText, ObjectIndex = textObject.Index,
                            Text = "OFFLINE", FontSize = textObject.FontSize, Font = PdfTextFont.Arial
                        });
                        Check(!SamePixels(initialImage, (BitmapSource)preview.Source), "PDF 内容编辑更新本地预览");
                        ((ListBox)contentEditor.FindName("ObjectList")).SelectedIndex = textObject.Index;
                        LayoutElement(contentEditor, new Size(1220, 840));
                        SaveElement(contentEditor, Path.Combine(outputDirectory, "界面-PDF内容编辑.png"), 1220, 840);
                        CallWindowEvent(contentEditor, "Highlight_Click");
                        CallWindowEvent(contentEditor, "SelectMode_Click");
                        Check(((TextBlock)contentEditor.FindName("ModeHint")).Text.StartsWith("选择对象", StringComparison.Ordinal), "绘制模式可退回对象选择");
                        await contentEditor.UndoEditAsync();
                        Check(SamePixels(initialImage, (BitmapSource)preview.Source), "撤销内容编辑恢复原页面像素");
                        Check(!((Button)contentEditor.FindName("EditorUndo")).IsEnabled, "已完全撤销后关闭无需丢弃修改");
                    }
                    finally { contentEditor.Close(); }
                }
                finally { CryptographicOperations.ZeroMemory(syntheticBytes); }
                using (var workspace = new PdfWorkspace())
                using (var renderer = new PdfPreviewRenderer())
                {
                    var first = workspace.Load(sourceA);
                    var second = workspace.Load(sourceB);
                    renderer.Add(first.Id, workspace.GetContent(first.Id));
                    renderer.Add(second.Id, workspace.GetContent(second.Id));
                    var beforePortrait = renderer.Render(first.Id, 2, 90, 600, 850);
                    var beforeLandscape = renderer.Render(second.Id, 1, 0, 600, 850);
                    var mergedPath = Path.Combine(outputDirectory, "合并验证.pdf");
                    workspace.Export([new(second.Id, 1), new(first.Id, 2, 90)], mergedPath);
                    var merged = workspace.Load(mergedPath);
                    renderer.Add(merged.Id, workspace.GetContent(merged.Id));
                    Check(SamePixels(beforeLandscape, renderer.Render(merged.Id, 1, 0, 600, 850)), "横向页面导出像素一致");
                    Check(SamePixels(beforePortrait, renderer.Render(merged.Id, 2, 0, 600, 850)), "旋转后导出像素一致");
                    SaveImage(renderer.Render(merged.Id, 1, 0, 900, 1000), Path.Combine(outputDirectory, "export-page-1.png"));
                    SaveImage(renderer.Render(merged.Id, 2, 0, 900, 1000), Path.Combine(outputDirectory, "export-page-2.png"));
                }

                window = new MainWindow();
                Layout(window);
                SaveWindow(window, Path.Combine(outputDirectory, "界面-空工作区.png"));
                await window.ImportFilesAsync([sourceA, sourceB]);
                Check(window.Sources.Count == 2 && window.Pages.Count == 5, "界面导入两个文档");
                Layout(window);
                var list = (ListBox)window.FindName("PageList");
                var original = window.Pages.ToArray();
                list.UnselectAll();
                list.SelectedItems.Add(original[1]);
                list.SelectedItems.Add(original[2]);
                CallEvent(window, "MoveUp_Click");
                Check(ReferenceEquals(window.Pages[0], original[1]) && ReferenceEquals(window.Pages[1], original[2]) && ReferenceEquals(window.Pages[2], original[0]), "多选页面上移保持顺序");
                Check(list.SelectedItems.Count == 2, "移动后保持多选");
                CallEvent(window, "MoveDown_Click");
                Check(window.Pages.SequenceEqual(original), "多选页面下移恢复顺序");
                CallEvent(window, "Rotate_Click");
                Check(original[1].Rotation == 90 && original[2].Rotation == 90 && original[0].Rotation == 0, "仅旋转选中页面");
                CallEvent(window, "Delete_Click");
                Check(window.Pages.Count == 3, "删除仅作用于输出列表");
                CallEvent(window, "Undo_Click");
                Check(window.Pages.Count == 5 && window.Pages[1].Rotation == 90, "撤销恢复页面和旋转");
                ((TextBox)window.FindName("PageRangeBox")).Text = "1-2,5";
                CallEvent(window, "SelectRange_Click");
                Check(list.SelectedItems.Count == 3, "界面按页码选择");
                CallEvent(window, "KeepSelected_Click");
                Check(window.Pages.Count == 3 && window.Pages[2].PageNumber == 2, "保留选中页");
                CallEvent(window, "Undo_Click");
                Check(window.Pages.Count == 5, "撤销保留选中操作");
                list.UnselectAll();
                list.SelectedItem = window.Pages[0];
                await (Task)typeof(MainWindow).GetMethod("ShowPreviewAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window.Pages[0]])!;
                Check(((Image)window.FindName("PreviewImage")).Source is BitmapSource, "本地预览显示");
                Layout(window);
                SaveWindow(window, Path.Combine(outputDirectory, "界面-页面编辑.png"));
                var pendingPreview = (Task)typeof(MainWindow).GetMethod("ShowPreviewAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window.Pages[0]])!;
                var remove = new Button { Tag = window.Sources[1].Id };
                typeof(MainWindow).GetMethod("RemoveSource_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [remove, new RoutedEventArgs()]);
                await pendingPreview;
                Check(window.Sources.Count == 1 && window.Pages.Count == 3, "移除文档同步移除页面");
                Check(((TextBlock)window.FindName("PreviewPlaceholder")).Visibility == Visibility.Collapsed, "移除其他文档不会打断正在生成的预览");
                await ToolkitUiChecks.RunAsync(outputDirectory);
                Check(networkEvents.Count == 0, "测试全过程无 .NET HTTP、Socket 或 DNS 事件");
                Console.WriteLine("界面交互、导出像素一致性及离线调用检查全部通过。截图位于指定的测试输出目录。");
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine(exception);
            }
            finally
            {
                window?.Close();
                application.Shutdown();
            }
        });
        application.Run();
        return _exitCode;
    }

    private static void CreateFixture(string path, string title, int pages, bool landscape)
    {
        using var builder = new PdfDocumentBuilder { IncludeDocumentInformation = false };
        var heading = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var body = builder.AddStandard14Font(Standard14Font.Helvetica);
        var pixels = new byte[12 * 12 * 3];
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++)
            {
                var offset = (y * 12 + x) * 3;
                pixels[offset] = (byte)(x * 20);
                pixels[offset + 1] = (byte)(y * 20);
                pixels[offset + 2] = 100;
            }
        var raster = BitmapSource.Create(12, 12, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, pixels, 36);
        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(raster));
        using var pngStream = new MemoryStream();
        pngEncoder.Save(pngStream);
        var rasterBytes = pngStream.ToArray();
        for (var index = 1; index <= pages; index++)
        {
            var width = landscape ? 842 : 595;
            var height = landscape ? 595 : 842;
            var page = builder.AddPage(width, height);
            page.SetTextAndFillColor(24, 106, 87);
            page.DrawRectangle(new PdfPoint(0, height - 170), width, 170, 0, true);
            page.SetTextAndFillColor(255, 255, 255);
            page.AddText(title, 27, new PdfPoint(45, height - 90), heading);
            page.AddText("LOCAL DOCUMENT / 2026", 11, new PdfPoint(47, height - 119), body);
            page.SetTextAndFillColor(32, 60, 53);
            page.AddText($"Page {index:00}", 21, new PdfPoint(45, height - 225), heading);
            page.AddText("A synthetic document for offline verification.", 13, new PdfPoint(45, height - 260), body);
            page.AddText("Text and graphics should stay sharp after merging.", 12, new PdfPoint(45, height - 286), body);
            page.SetTextAndFillColor(231, 240, 234);
            page.DrawRectangle(new PdfPoint(45, height - 412), width - 90, 88, 0, true);
            page.SetTextAndFillColor(77, 112, 87);
            page.AddText("01   PLAN       02   REVIEW       03   DELIVER", 12, new PdfPoint(65, height - 372), heading);
            page.SetTextAndFillColor(121, 144, 129);
            page.AddPng(rasterBytes, new PdfRectangle(45, 80, 140, 140));
            page.AddText("Created locally. No real document data.", 10, new PdfPoint(45, 45), body);
            page.AddText($"{index} / {pages}", 10, new PdfPoint(width - 80, 45), body);
        }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        output.Write(builder.Build());
    }

    private static void CallEvent(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);

    private static void CallWindowEvent(Window window, string name) =>
        window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("验证失败：" + name);
        Console.WriteLine("通过：" + name);
    }

    private static bool SamePixels(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return false;
        var stride = first.PixelWidth * 4;
        var left = new byte[stride * first.PixelHeight];
        var right = new byte[left.Length];
        first.CopyPixels(left, stride, 0);
        second.CopyPixels(right, stride, 0);
        return left.AsSpan().SequenceEqual(right);
    }

    private static void Layout(MainWindow window)
    {
        var element = (FrameworkElement)window.Content;
        element.Measure(new Size(1240, 760));
        element.Arrange(new Rect(0, 0, 1240, 760));
        element.UpdateLayout();
    }

    private static void LayoutElement(Window window, Size size)
    {
        var element = (FrameworkElement)window.Content;
        element.Measure(size);
        element.Arrange(new Rect(new Point(), size));
        element.UpdateLayout();
    }

    private static void SaveWindow(MainWindow window, string path)
    {
        var image = new RenderTargetBitmap(1240, 760, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render((System.Windows.Media.Visual)window.Content);
        SaveImage(image, path);
    }

    private static void SaveElement(Window window, string path, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background);
        image.Render((System.Windows.Media.Visual)window.Content);
        SaveImage(image, path);
    }

    private static void SaveImage(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(output);
    }

    private sealed class NetworkEvents : EventListener
    {
        private int _count;
        public int Count => _count;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.NameResolution")
                EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId != -1) Interlocked.Increment(ref _count);
        }
    }
}
