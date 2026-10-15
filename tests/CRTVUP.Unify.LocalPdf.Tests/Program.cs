using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using UglyToad.PdfPig;

internal static class Program
{
    private static int Main()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.Combine(temporaryRoot, "CRTVUP.LocalPdf.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = 0;
        try
        {
            var cases = new (string Name, Action Test)[]
            {
                ("跨文件排序、重复页面、增量旋转及源文件不变", () => MergePages(directory)),
                ("清空和移除尽力归零会话内存", () => ClearMemory(directory)),
                ("拒绝覆盖目标文件和源文件", () => NeverOverwrite(directory)),
                ("取消操作不创建结果", () => Cancellation(directory)),
                ("拒绝空页面、非法页码、角度和失效来源", () => InvalidEdits(directory)),
                ("拒绝损坏和空文件", () => InvalidDocuments(directory)),
                ("拒绝网络、URI、设备和备用数据流路径", () => NetworkPaths(directory)),
                ("页面上限", () => Limits(directory)),
                ("大文件不再受 150 MiB 限制", () => LargeFiles(directory)),
                ("拒绝批注、表单和签名", () => RejectInteractiveDocuments(directory)),
                ("拒绝实际带密码的 PDF", () => RejectEncryptedPdf(directory)),
                ("普通超链接可导入但不复制", () => OmitLinks(directory)),
                ("普通生成器字段和自定义资源名称兼容", () => StaticResourceNames(directory)),
                ("页面脚本、附件和元数据按原文合并", () => MergePageExtras(directory)),
                ("不复制文档元数据、附件和文档脚本", () => OmitDocumentExtras(directory)),
                ("不存在的输出文件夹不自动创建", () => MissingDestination(directory)),
                ("已释放会话拒绝继续使用", () => DisposedWorkspace(directory)),
                ("V2 内容修改与纯代码格式转换", V2CoreChecks.Run),
                ("本地工具箱：JSON/编码/对比/压缩", () => ToolkitChecks.Run(directory)),
                ("本地工具箱扩充：互转/模型/加密/目录/PDF", () => ToolkitExtrasChecks.Run(directory)),
                ("Office 结构和 Unicode 文字完整性", OpenXmlChecks.Run),
                ("文字导入资源上限与外部关系隔离", LocalImporterChecks.Run),
                ("原生内容编辑与旋转坐标", NativeEditorChecks.Run)
            };
            foreach (var (name, test) in cases)
            {
                test();
                Console.WriteLine("通过：" + name);
                passed++;
            }
            Console.WriteLine($"合成 PDF 检查全部通过：{passed} 项。未读取用户文档。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"合成 PDF 检查失败；已通过 {passed} 项。{exception}");
            return 1;
        }
        finally
        {
            // 仅递归删除这次创建的目录，先核实绝对路径确实位于系统临时目录内。
            var resolved = Path.GetFullPath(directory);
            var boundary = Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("CRTVUP.LocalPdf.Tests-", StringComparison.Ordinal)
                && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void MergePages(string directory)
    {
        var first = WritePdf(directory, "source-a.pdf", [("ALPHA", 0), ("BETA", 90)]);
        var second = WritePdf(directory, "source-b.pdf", [("GAMMA", 0)]);
        var firstHash = Hash(first);
        var secondHash = Hash(second);
        using var workspace = new PdfWorkspace();
        var a = workspace.Load(first);
        var b = workspace.Load(second);
        Check(a.Pages.Count == 2 && a.Pages[1].OriginalRotation == 90, "载入页数和原始旋转错误。");
        var output = Path.Combine(directory, "merged.pdf");
        var result = workspace.Export([
            new PageEdit(a.Id, 2, 90), new PageEdit(b.Id, 1, -90),
            new PageEdit(a.Id, 1), new PageEdit(a.Id, 2, 360)], output);
        Check(result.PageCount == 4 && result.ByteCount == new FileInfo(output).Length, "导出结果计数错误。");
        using var document = PdfDocument.Open(File.ReadAllBytes(output));
        Check(document.NumberOfPages == 4, "合并页数错误。");
        var expected = new[] { ("BETA", 180), ("GAMMA", 270), ("ALPHA", 0), ("BETA", 90) };
        for (var index = 0; index < expected.Length; index++)
        {
            var page = document.GetPage(index + 1);
            Check(page.Text == expected[index].Item1, "页面顺序或可搜索文字发生变化。");
            Check(page.Rotation.Value == expected[index].Item2, "增量旋转错误。");
        }
        Check(firstHash == Hash(first) && secondHash == Hash(second), "源文件被修改。");
    }

    private static void ClearMemory(string directory)
    {
        var path = WritePdf(directory, "clear.pdf", [("CLEAR", 0)]);
        using var workspace = new PdfWorkspace();
        var first = workspace.Load(path);
        var firstMemory = workspace.GetContent(first.Id);
        Check(firstMemory.Span.IndexOfAnyExcept((byte)0) >= 0, "未载入内容。");
        workspace.Remove(first.Id);
        Check(firstMemory.Span.IndexOfAnyExcept((byte)0) == -1, "移除后源字节未归零。");
        Expect<PdfToolException>(() => workspace.GetContent(first.Id));
        var next = workspace.Load(path);
        var nextMemory = workspace.GetContent(next.Id);
        workspace.Clear();
        Check(nextMemory.Span.IndexOfAnyExcept((byte)0) == -1, "清空后源字节未归零。");
        Check(workspace.Load(path).Pages.Count == 1, "清空后无法开始新会话。");
    }

    private static void NeverOverwrite(string directory)
    {
        var sourcePath = WritePdf(directory, "preserve.pdf", [("PRESERVE", 0)]);
        var existing = Path.Combine(directory, "existing.pdf");
        File.WriteAllBytes(existing, [1, 7, 9, 33]);
        var sourceHash = Hash(sourcePath);
        var existingHash = Hash(existing);
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(sourcePath);
        Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 1)], existing));
        Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 1)], sourcePath));
        Check(Hash(existing) == existingHash && Hash(sourcePath) == sourceHash, "覆盖拒绝后已有文件变化。");
    }

    private static void Cancellation(string directory)
    {
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(WritePdf(directory, "cancel-source.pdf", [("CANCEL", 0)]));
        var output = Path.Combine(directory, "cancelled.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Expect<OperationCanceledException>(() => workspace.Export([new PageEdit(source.Id, 1)], output, cancellation.Token));
        Check(!File.Exists(output), "已取消的导出仍产生文件。");
    }

    private static void InvalidEdits(string directory)
    {
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(WritePdf(directory, "invalid-edits.pdf", [("VALID", 0)]));
        var output = Path.Combine(directory, "invalid-result.pdf");
        Expect<PdfToolException>(() => workspace.Export([], output));
        Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 0)], output));
        Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 2)], output));
        Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 1, 45)], output));
        Expect<PdfToolException>(() => workspace.Export([new PageEdit("missing", 1)], output));
        Expect<PdfToolException>(() => workspace.Export([null!], output));
        Check(!File.Exists(output), "非法编辑产生了结果文件。");
    }

    private static void InvalidDocuments(string directory)
    {
        using var workspace = new PdfWorkspace();
        var damaged = Path.Combine(directory, "damaged.pdf");
        var empty = Path.Combine(directory, "empty.pdf");
        File.WriteAllText(damaged, "%PDF-1.7\nnot-a-document");
        File.WriteAllBytes(empty, []);
        Expect<PdfToolException>(() => workspace.Load(damaged));
        Expect<PdfToolException>(() => workspace.Load(empty));
    }

    private static void NetworkPaths(string directory)
    {
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(WritePdf(directory, "local-only.pdf", [("LOCAL", 0)]));
        var paths = new[]
        {
            @"\\server.invalid\share\private.pdf", "//server.invalid/share/private.pdf",
            "https://example.invalid/private.pdf", "file:///C:/private.pdf",
            @"\\?\C:\private.pdf", @"\\.\C:\private.pdf", @"C:\private.pdf:secret",
            @"C:\NUL.pdf", "relative.pdf"
        };
        foreach (var path in paths)
        {
            Expect<PdfToolException>(() => workspace.Load(path));
            Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 1)], path));
        }
    }

    private static void Limits(string directory)
    {
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(WritePdf(directory, "limit-source.pdf", [("LIMIT", 0)]));
        var tooManyEdits = Enumerable.Repeat(new PageEdit(source.Id, 1), 5001).ToArray();
        Expect<PdfToolException>(() => workspace.Export(tooManyEdits, Path.Combine(directory, "too-many.pdf")));
        var manyPages = Enumerable.Range(0, 5001).Select(_ => ("PAGE", 0)).ToArray();
        Expect<PdfToolException>(() => workspace.Load(WritePdf(directory, "too-many-source.pdf", manyPages)));
    }

    private static void LargeFiles(string directory)
    {
        // 旧的 150 MiB 单文件上限已移除：略大于该阈值的合法 PDF 应能正常载入。
        var path = WriteLargePdf(directory, "large-source.pdf", 150L * 1024 * 1024);
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(path);
        Check(source.Pages.Count == 1 && source.ByteCount > 150L * 1024 * 1024, "大文件载入结果不正确。");
    }

    private static string WriteLargePdf(string directory, string name, long streamBytes)
    {
        var path = Path.Combine(directory, name);
        var offsets = new List<long>();
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            void Write(string value)
            {
                var bytes = Encoding.ASCII.GetBytes(value);
                file.Write(bytes, 0, bytes.Length);
            }
            Write("%PDF-1.4\n%LOCAL_SYNTHETIC_TEST\n");
            offsets.Add(file.Position);
            Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets.Add(file.Position);
            Write("2 0 obj\n<< /Type /Pages /Count 1 /Kids [4 0 R] >>\nendobj\n");
            offsets.Add(file.Position);
            Write("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
            offsets.Add(file.Position);
            Write("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            const string content = "BT /F1 12 Tf 10 100 Td (LARGE) Tj ET\n";
            offsets.Add(file.Position);
            Write($"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n");
            // 未被引用的超大对象：只用于把文件撑到阈值以上，不参与页面复制。
            offsets.Add(file.Position);
            Write($"6 0 obj\n<< /Length {streamBytes} >>\nstream\n");
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'A');
            for (var remaining = streamBytes; remaining > 0;)
            {
                var count = (int)Math.Min(chunk.Length, remaining);
                file.Write(chunk, 0, count);
                remaining -= count;
            }
            Write("\nendstream\nendobj\n");
            var xref = file.Position;
            Write("xref\n0 7\n0000000000 65535 f \n");
            foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            Write($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        return path;
    }

    private static void RejectInteractiveDocuments(string directory)
    {
        using var workspace = new PdfWorkspace();
        var documents = new[]
        {
            ("form.pdf", "/AcroForm << /Fields [] >>", ""),
            ("signature.pdf", "/Perms << /DocMDP << /Type /Sig /ByteRange [0 10 20 30] >> >>", ""),
            ("annotation.pdf", "", "/Annots [<< /Type /Annot /Subtype /Text /Rect [10 10 40 40] /Contents (PRIVATE) >>]"),
            ("stamp.pdf", "", "/Annots [<< /Type /Annot /Subtype /Stamp /Rect [10 10 40 40] >>]"),
            ("layer.pdf", "/OCProperties << /OCGs [] >>", "")
        };
        foreach (var (name, catalog, page) in documents)
        {
            var path = WritePdf(directory, name, [("SPECIAL", 0)], catalog, page);
            Expect<PdfToolException>(() => workspace.Load(path));
        }
    }

    private static void OmitLinks(string directory)
    {
        var path = WritePdf(directory, "link.pdf", [("VISIBLE", 0)], pageExtra:
            "/Annots [<< /Type /Annot /Subtype /Link /Rect [10 10 40 40] /A << /S /URI /URI (https://example.invalid/PRIVATE_LINK) >> >>]");
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(path);
        var output = Path.Combine(directory, "link-removed.pdf");
        workspace.Export([new PageEdit(source.Id, 1)], output);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Check(!text.Contains("PRIVATE_LINK", StringComparison.Ordinal) && !text.Contains("/URI", StringComparison.Ordinal), "输出保留了外链动作。");
        using var pdf = PdfDocument.Open(File.ReadAllBytes(output));
        Check(pdf.GetPage(1).Text == "VISIBLE", "去除链接时破坏了页面文字。");
    }

    private static void RejectEncryptedPdf(string directory)
    {
        // 由本机 pypdf 生成的合成空白页，密码为 LOCAL_SYNTHETIC_TEST_ONLY。
        // 固定夹具使控制台测试不依赖 Python、任何联网操作或真实用户文档。
        const string fixture = "JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPDVmYjI0NDU5ZDc+Cj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9UeXBlIC9QYWdlcwovQ291bnQgMQovS2lkcyBbIDQgMCBSIF0KPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL0NhdGFsb2cKL1BhZ2VzIDIgMCBSCj4+CmVuZG9iago0IDAgb2JqCjw8Ci9UeXBlIC9QYWdlCi9SZXNvdXJjZXMgPDwKPj4KL01lZGlhQm94IFsgMC4wIDAuMCAyMDAgMzAwIF0KL1BhcmVudCAyIDAgUgo+PgplbmRvYmoKNSAwIG9iago8PAovViAyCi9SIDMKL0xlbmd0aCAxMjgKL1AgNDI5NDk2NzI5MgovRmlsdGVyIC9TdGFuZGFyZAovTyA8ZTZhMGQ0MWYyMzA3OWJmMzY3MjZhNTUzOGY4NDM1MTA4OGJkZDFjNzFhYTQ2NDRjZTYyZGU4MzY5YmYzNjkzOD4KL1UgPDgyYjhjMzVlN2U5NTgyNjBkNGEwNmU4ZmI0OTk4NjJiMjhiZjRlNWU0ZTc1OGE0MTY0MDA0ZTU2ZmZmYTAxMDg+Cj4+CmVuZG9iagp4cmVmCjAgNgowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA1OSAwMDAwMCBuIAowMDAwMDAwMTE4IDAwMDAwIG4gCjAwMDAwMDAxNjcgMDAwMDAgbiAKMDAwMDAwMDI2MSAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDYKL1Jvb3QgMyAwIFIKL0luZm8gMSAwIFIKL0lEIFsgPDYyMzE2MzY1NjYzMDMzNjYzNDY1MzczNTM3Mzc2MjY2MzMzOTM2MzE2NDY1NjQ2MTM5MzYzNDM0Mzk2NTYxMzA+IDw2MjMxNjM2NTY2MzAzMzY2MzQ2NTM3MzUzNzM3NjI2NjMzMzkzNjMxNjQ2NTY0NjEzOTM2MzQzNDM5NjU2MTMwPiBdCi9FbmNyeXB0IDUgMCBSCj4+CnN0YXJ0eHJlZgo0NzYKJSVFT0YK";
        var path = Path.Combine(directory, "encrypted.pdf");
        File.WriteAllBytes(path, Convert.FromBase64String(fixture));
        using var workspace = new PdfWorkspace();
        var exception = Expect<PdfToolException>(() => workspace.Load(path));
        Check(exception.Message.Contains("加密", StringComparison.Ordinal), "加密文件未给出明确原因。");
    }

    private static void StaticResourceNames(string directory)
    {
        foreach (var fontName in new[] { "F1", "JS", "Metadata" })
        {
            var path = WritePdf(directory, "resource-" + fontName + ".pdf", [("ORDINARY", 0)],
                pageExtra: "/ProcSet [/PDF /Text] /LastModified (D:20260101000000) /Dur 1 /Trans << /S /Dissolve >>",
                fontName: fontName);
            using var workspace = new PdfWorkspace();
            var source = workspace.Load(path);
            var output = Path.Combine(directory, "resource-out-" + fontName + ".pdf");
            workspace.Export([new PageEdit(source.Id, 1)], output);
            using var document = PdfDocument.Open(File.ReadAllBytes(output));
            Check(document.GetPage(1).Text == "ORDINARY", "普通页面或自定义字体名称被误判。");
        }
    }

    private static void MergePageExtras(string directory)
    {
        // 页面级脚本、附件和元数据不再拦截，按原文复制到合并结果。
        var path = WritePdf(directory, "page-extras.pdf", [("VISIBLE", 0)],
            pageExtra: "/AA << /O << /S /JavaScript /JS (PRIVATE_SCRIPT) >> >> /Metadata 6 0 R",
            additionalObjects: [MakeStream("PRIVATE_PAGE_XMP", "/Type /Metadata /Subtype /XML")]);
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(path);
        var output = Path.Combine(directory, "page-extras-merged.pdf");
        workspace.Export([new PageEdit(source.Id, 1)], output);
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Check(raw.Contains("PRIVATE_SCRIPT", StringComparison.Ordinal)
            && raw.Contains("PRIVATE_PAGE_XMP", StringComparison.Ordinal), "按原文合并时丢失了页面附加内容。");
        using var document = PdfDocument.Open(File.ReadAllBytes(output));
        Check(document.GetPage(1).Text == "VISIBLE", "按原文合并时丢失了页面文字。");
    }

    private static void OmitDocumentExtras(string directory)
    {
        var path = WritePdf(directory, "extras.pdf", [("BODY", 0)],
            catalogExtra: "/Metadata 7 0 R /OpenAction << /S /JavaScript /JS (PRIVATE_SCRIPT) >> /Names << /EmbeddedFiles << /Names [(private.txt) 8 0 R] >> >>",
            additionalObjects: ["<< /Author (PRIVATE_AUTHOR) /Subject (PRIVATE_SUBJECT) >>",
                MakeStream("PRIVATE_XMP", "/Type /Metadata /Subtype /XML"),
                "<< /Type /Filespec /F (private.txt) /EF << /F 9 0 R >> >>",
                MakeStream("PRIVATE_ATTACHMENT", "/Type /EmbeddedFile")],
            trailerExtra: "/Info 6 0 R");
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(path);
        var output = Path.Combine(directory, "extras-removed.pdf");
        workspace.Export([new PageEdit(source.Id, 1)], output);
        var bytes = File.ReadAllBytes(output);
        var raw = Encoding.Latin1.GetString(bytes);
        Check(!raw.Contains("PRIVATE_", StringComparison.Ordinal), "文档附带私有标识被复制。");
        using var document = PdfDocument.Open(bytes);
        var catalog = document.Structure.Catalog.CatalogDictionary.Data;
        Check(!catalog.ContainsKey("OpenAction") && !catalog.ContainsKey("Names") && !catalog.ContainsKey("Metadata"), "输出保留了文档动作、附件或元数据入口。");
        Check(document.GetPage(1).Text == "BODY", "正文未保留。");
    }

    private static void MissingDestination(string directory)
    {
        using var workspace = new PdfWorkspace();
        var source = workspace.Load(WritePdf(directory, "destination-source.pdf", [("DEST", 0)]));
        var absent = Path.Combine(directory, "does-not-exist");
        Expect<PdfToolException>(() => workspace.Export([new PageEdit(source.Id, 1)], Path.Combine(absent, "result.pdf")));
        Check(!Directory.Exists(absent), "意外创建了目标目录。");
    }

    private static void DisposedWorkspace(string directory)
    {
        var workspace = new PdfWorkspace();
        var source = workspace.Load(WritePdf(directory, "disposed.pdf", [("DISPOSE", 0)]));
        var memory = workspace.GetContent(source.Id);
        workspace.Dispose();
        workspace.Dispose();
        Check(memory.Span.IndexOfAnyExcept((byte)0) == -1, "释放会话未归零源字节。");
        Expect<ObjectDisposedException>(() => workspace.GetContent(source.Id));
    }

    private static string WritePdf(string directory, string name,
        IReadOnlyList<(string Text, int Rotation)> pages, string catalogExtra = "",
        string pageExtra = "", IReadOnlyList<string>? additionalObjects = null, string trailerExtra = "", string fontName = "F1")
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            $"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Count).Select(index => $"{4 + index * 2} 0 R"))}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
        };
        for (var index = 0; index < pages.Count; index++)
        {
            var (text, rotation) = pages[index];
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Rotate {rotation} /Resources << /Font << /{fontName} 3 0 R >> >> /Contents {5 + index * 2} 0 R {pageExtra} >>");
            objects.Add(MakeStream($"BT /{fontName} 12 Tf 10 100 Td ({text}) Tj ET\n"));
        }
        if (additionalObjects is not null)
            objects.AddRange(additionalObjects);
        using var content = new MemoryStream();
        void Append(string value) => content.Write(Encoding.ASCII.GetBytes(value));
        Append("%PDF-1.4\n%LOCAL_SYNTHETIC_TEST\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(content.Position);
            Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var crossReference = content.Position;
        Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{crossReference}\n%%EOF\n");
        var path = Path.Combine(directory, name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        content.Position = 0;
        content.CopyTo(file);
        return path;
    }

    private static string MakeStream(string data, string extra = "") =>
        $"<< /Length {Encoding.ASCII.GetByteCount(data)} {extra} >>\nstream\n{data}\nendstream";

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"预期出现 {typeof(T).Name}，操作却成功。");
    }
}
