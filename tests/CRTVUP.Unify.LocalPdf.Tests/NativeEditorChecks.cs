using System.Globalization;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using UglyToad.PdfPig;

/// <summary>全部夹具在内存中合成，不读取用户文档，不启动外部程序。</summary>
public static class NativeEditorChecks
{
    private const double PAGE_WIDTH = 300;
    private const double PAGE_HEIGHT = 200;

    public static void Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("原生编辑：中英文替换、插入和删除可被独立解析器提取", TextEditing),
            ("原生编辑：图片插入、替换和删除改变实际像素与对象", ImageEditing),
            ("原生编辑：裁剪、透明和嵌套内容明确拒绝且保持原状", RestrictedObjects),
            ("原生编辑：无效请求失败后原页面和原输入完整保留", FailedEditsAreAtomic),
            ("原生编辑：原始旋转与预览旋转组合保持插入方向和位置", RotatedInsertions),
            ("原生编辑：带偏移的裁剪框坐标换算正确", CroppedPageCoordinates),
            ("原生编辑：高亮和边框为可删除的矢量对象", VectorMarks),
            ("原生编辑：输出数组独立且释放后拒绝继续操作", OwnedBuffersAndDisposal)
        };
        foreach (var (name, test) in cases)
        {
            test();
            Console.WriteLine("通过：" + name);
        }
    }

    private static void TextEditing()
    {
        var source = CreatePdf("0.1 0.2 0.7 rg BT /F1 14 Tf 24 156 Td (ORIGINAL_BODY) Tj ET\n"
            + "0 0 0 rg BT /F1 12 Tf 24 35 Td (PRESERVED_BODY) Tj ET\n");
        var originalBytes = source.ToArray();
        using var document = PdfiumRuntime.Open(source);
        var original = FindText(document, "ORIGINAL_BODY");
        Check(original.CanEdit && original.FontSize == 14, "普通文字未被识别为可编辑对象。");
        var output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.ReplaceText, ObjectIndex = original.Index,
            Text = "ENGLISH_REPLACED"
        });
        AssertExtractable(document, output, ["ENGLISH_REPLACED", "PRESERVED_BODY"], ["ORIGINAL_BODY"]);
        Check(document.EnumerateObjects(1).Objects.Count == 2, "替换文字留下了原对象或添加了遮挡对象。");
        Check(FindText(document, "ENGLISH_REPLACED").FontSize == original.FontSize, "替换未保留原字号。");
        Check(HasBlueInk(document.Render(1, maxWidth: 600, maxHeight: 400)), "替换文字未保留原填充颜色。");

        output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.InsertText, Position = new PdfPoint(24, 122),
            Text = "ENGLISH_INSERTED", Font = PdfTextFont.Arial, FontSize = 12
        });
        AssertExtractable(document, output, ["ENGLISH_REPLACED", "ENGLISH_INSERTED", "PRESERVED_BODY"], ["ORIGINAL_BODY"]);
        var replaced = FindText(document, "ENGLISH_REPLACED");
        output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.ReplaceText, ObjectIndex = replaced.Index,
            Text = "中文替换结果", Font = PdfTextFont.Auto
        });
        AssertExtractable(document, output, ["中文替换结果", "ENGLISH_INSERTED", "PRESERVED_BODY"], ["ORIGINAL_BODY", "ENGLISH_REPLACED"]);
        output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.InsertText, Position = new PdfPoint(24, 92),
            Text = "第一行中文\r\n第二行中文", Font = PdfTextFont.SimHei, FontSize = 12
        });
        AssertExtractable(document, output,
            ["中文替换结果", "第一行中文", "第二行中文", "ENGLISH_INSERTED", "PRESERVED_BODY"], ["ORIGINAL_BODY"]);
        Check(document.EnumerateObjects(1).Objects.Count == 5, "多行插入没有生成正确的文字对象数量。");
        output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.DeleteObject,
            ObjectIndex = FindText(document, "中文替换结果").Index
        });
        AssertExtractable(document, output, ["第一行中文", "第二行中文", "PRESERVED_BODY"], ["中文替换结果", "ORIGINAL_BODY"]);
        Check(document.EnumerateObjects(1).Objects.Count == 4, "删除文字没有真正移除对象。");
        Check(source.AsSpan().SequenceEqual(originalBytes), "原生文字编辑修改了调用方的源字节。");
    }

    private static void ImageEditing()
    {
        var source = CreateImagePdf();
        var sourceCopy = source.ToArray();
        using var document = PdfiumRuntime.Open(source);
        var original = document.EnumerateObjects(1).Objects.Single(item => item.Kind == PdfObjectKind.Image);
        Check(original.CanEdit, "普通不透明图片未被识别为可编辑对象。");
        AssertPixel(document.Render(1, maxWidth: 600, maxHeight: 400), 50 / PAGE_WIDTH, 1 - 50 / PAGE_HEIGHT,
            new PdfColor(255, 0, 0));

        var insertedImage = QuadrantImage();
        var insertedCopy = insertedImage.Bgra.ToArray();
        var output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.InsertImage,
            Image = insertedImage, Bounds = new PdfBounds(150, 40, 250, 100)
        });
        Check(insertedImage.Bgra.AsSpan().SequenceEqual(insertedCopy), "插入图片修改了调用方像素数组。");
        Check(ImageCount(output) == 2, "插入图片没有进入保存后的内容流。");
        var imageObjects = document.EnumerateObjects(1).Objects.Where(item => item.Kind == PdfObjectKind.Image).ToArray();
        Check(imageObjects.Length == 2, "插入图片对象数量错误。");
        AssertQuadrants(document.Render(1, maxWidth: 600, maxHeight: 400),
            new PdfNormalizedBounds(0.5, 0.5, 1d / 3, 0.3));

        var replacement = SolidImage(new PdfColor(0, 0, 255), paddedStride: true);
        var replacementCopy = replacement.Bgra.ToArray();
        var beforeBounds = imageObjects[0].Bounds;
        output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.ReplaceImage, ObjectIndex = imageObjects[0].Index,
            Image = replacement
        });
        Check(replacement.Bgra.AsSpan().SequenceEqual(replacementCopy), "替换图片修改了调用方像素数组。");
        var after = document.EnumerateObjects(1).Objects.Where(item => item.Kind == PdfObjectKind.Image).ToArray();
        Check(after.Length == 2 && after[0].Bounds == beforeBounds, "替换图片未保留原矩阵或留下旧对象。");
        Check(ImageCount(output) == 2, "图片替换只叠加了新图片。");
        AssertPixel(document.Render(1, maxWidth: 600, maxHeight: 400), 50 / PAGE_WIDTH, 1 - 50 / PAGE_HEIGHT,
            new PdfColor(0, 0, 255));
        Array.Clear(replacement.Bgra);
        Array.Clear(insertedImage.Bgra);
        AssertPixel(document.Render(1, maxWidth: 600, maxHeight: 400), 50 / PAGE_WIDTH, 1 - 50 / PAGE_HEIGHT,
            new PdfColor(0, 0, 255));
        Check(after[0].CanEdit, "不透明替换图片在保存后无法再次编辑。");
        output = document.ApplyEdit(new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.DeleteObject, ObjectIndex = after[0].Index
        });
        Check(ImageCount(output) == 1 && document.EnumerateObjects(1).Objects.Count(item => item.Kind == PdfObjectKind.Image) == 1,
            "删除图片没有移除保存后的图片对象。");
        AssertPixel(document.Render(1, maxWidth: 600, maxHeight: 400), 50 / PAGE_WIDTH, 1 - 50 / PAGE_HEIGHT,
            new PdfColor(255, 255, 255));
        AssertExtractable(document, output, ["UNCHANGED_TEXT"], []);
        Check(source.AsSpan().SequenceEqual(sourceCopy), "原生图片编辑修改了调用方的源字节。");
    }

    private static void RestrictedObjects()
    {
        var fixtures = new (string Id, byte[] Pdf, PdfObjectKind Kind, string Text, string Reason)[]
        {
            // 裁剪框必须实际截断文字；PDFium 会消除完整包含对象的冗余矩形裁剪。
            ("path-clip-text", CreatePdf("q 10 10 50 170 re W n BT /F1 14 Tf 24 100 Td (CLIPPED_BODY) Tj ET Q"),
                PdfObjectKind.Text, "CLIPPED_BODY", "裁剪"),
            ("text-render-clip", CreatePdf("BT /F1 14 Tf 7 Tr 24 100 Td (TEXT_MASK) Tj ET"),
                PdfObjectKind.Text, "TEXT_MASK", "裁剪"),
            ("text-derived-clip", CreatePdf("q BT /F1 30 Tf 7 Tr 24 100 Td (MASK) Tj ET BT /F1 14 Tf 0 Tr 24 100 Td (CLIPPED_BODY) Tj ET Q"),
                PdfObjectKind.Text, "CLIPPED_BODY", "裁剪"),
            ("alpha-text", CreatePdf("q /GS1 gs BT /F1 14 Tf 24 100 Td (TRANSPARENT_BODY) Tj ET Q",
                resources: "/ExtGState << /GS1 6 0 R >>", extraObjects: ["<< /Type /ExtGState /ca 0.5 /CA 1 >>"]),
                PdfObjectKind.Text, "TRANSPARENT_BODY", "透明"),
            ("path-clip-image", CreateImagePdf("q 15 15 60 60 re W n ", " Q"), PdfObjectKind.Image, "", "裁剪"),
            ("alpha-image", CreateImagePdf("q /GS1 gs ", " Q", transparency: true), PdfObjectKind.Image, "", "透明"),
            ("nested-form", CreatePdf("q /Fm1 Do Q", resources: "/XObject << /Fm1 6 0 R >>", extraObjects:
                [MakeStream("BT /F1 12 Tf 15 50 Td (NESTED_BODY) Tj ET",
                    "/Type /XObject /Subtype /Form /BBox [0 0 180 150] /Resources << /Font << /F1 3 0 R >> >>")]),
                PdfObjectKind.Form, "", "嵌套")
        };
        foreach (var fixture in fixtures)
        {
            using var document = PdfiumRuntime.Open(fixture.Pdf);
            var selected = document.EnumerateObjects(1).Objects.Single(item =>
                item.Kind == fixture.Kind && (fixture.Text.Length == 0 || item.Text.Contains(fixture.Text, StringComparison.Ordinal)));
            Check(!selected.CanEdit && selected.Limitation?.Contains(fixture.Reason, StringComparison.Ordinal) == true,
                $"合成夹具 {fixture.Id}：复杂对象未说明不可编辑的具体原因。CanEdit={selected.CanEdit}；Limitation={selected.Limitation ?? "无"}。");
            var before = Snapshot(document);
            var failure = Expect<PdfToolException>(() => document.ApplyEdit(new PdfEditRequest
            {
                PageNumber = 1, Kind = PdfEditKind.DeleteObject, ObjectIndex = selected.Index
            }));
            Check(failure.Message.Contains(fixture.Reason, StringComparison.Ordinal), $"合成夹具 {fixture.Id}：拒绝复杂对象时没有说明具体原因。");
            AssertUnchanged(document, before);
            if (fixture.Kind is PdfObjectKind.Text or PdfObjectKind.Image)
            {
                Expect<PdfToolException>(() => document.ApplyEdit(new PdfEditRequest
                {
                    PageNumber = 1,
                    Kind = fixture.Kind == PdfObjectKind.Text ? PdfEditKind.ReplaceText : PdfEditKind.ReplaceImage,
                    ObjectIndex = selected.Index, Text = "REPLACEMENT", Image = SolidImage(new PdfColor(0, 0, 255))
                }));
                AssertUnchanged(document, before);
            }
        }
    }

    private static void FailedEditsAreAtomic()
    {
        var source = CreatePdf("BT /F1 14 Tf 24 100 Td (ATOMIC_BODY) Tj ET");
        var original = source.ToArray();
        using var document = PdfiumRuntime.Open(source);
        var selected = FindText(document, "ATOMIC_BODY");
        var validReplace = new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.ReplaceText, ObjectIndex = selected.Index, Text = "VALID_BODY"
        };
        var validInsert = new PdfEditRequest
        {
            PageNumber = 1, Kind = PdfEditKind.InsertText, Position = new PdfPoint(20, 30), Text = "VALID_BODY"
        };
        var invalid = new[]
        {
            validReplace with { ObjectIndex = -1 }, validReplace with { ObjectIndex = 20 },
            validReplace with { PageNumber = 0 }, validReplace with { PageNumber = 2 },
            validReplace with { Text = "" }, validReplace with { Text = " \r\n\t " },
            validReplace with { Text = "BAD\0TEXT" }, validReplace with { Text = "\U0001F600" },
            validReplace with { Text = new string('A', 32769) },
            validReplace with { Text = string.Join('\n', Enumerable.Repeat("A", 513)) },
            validReplace with { FontSize = double.NaN }, validReplace with { FontSize = 0 },
            validReplace with { Font = (PdfTextFont)99 },
            validReplace with { Font = PdfTextFont.Arial, Text = "中文缺字检查" },
            validInsert with { Position = null }, validInsert with { Position = new PdfPoint(double.PositiveInfinity, 20) },
            validInsert with { ViewRotation = 45 }, validInsert with { Kind = (PdfEditKind)999 },
            new PdfEditRequest { PageNumber = 1, Kind = PdfEditKind.ReplaceImage, ObjectIndex = selected.Index,
                Image = SolidImage(new PdfColor(0, 0, 255)) },
            new PdfEditRequest { PageNumber = 1, Kind = PdfEditKind.InsertImage, Bounds = new PdfBounds(10, 20, 30, 40),
                Image = new PdfBgraImage([1, 2, 3], 2, 2, 8) },
            new PdfEditRequest { PageNumber = 1, Kind = PdfEditKind.InsertImage, Bounds = new PdfBounds(30, 20, 10, 40),
                Image = SolidImage(new PdfColor(0, 0, 255)) },
            new PdfEditRequest { PageNumber = 1, Kind = PdfEditKind.AddRectangle, Bounds = new PdfBounds(10, 20, 30, 40),
                StrokeWidth = double.NaN }
        };
        var before = Snapshot(document);
        foreach (var request in invalid)
        {
            Expect<PdfToolException>(() => document.ApplyEdit(request));
            AssertUnchanged(document, before);
            Check(source.AsSpan().SequenceEqual(original), "失败请求修改了调用方的源字节。");
        }
        var successful = document.ApplyEdit(validReplace);
        AssertExtractable(document, successful, ["VALID_BODY"], ["ATOMIC_BODY"]);
    }

    private static void RotatedInsertions()
    {
        byte[]? referenceText = null;
        foreach (var originalRotation in new[] { 0, 90, 180, 270 })
        foreach (var viewRotation in new[] { 0, 90, 180, 270 })
        {
            using var document = PdfiumRuntime.Open(CreatePdf("", rotation: originalRotation));
            var totalRotation = (originalRotation + viewRotation) % 360;
            var rendered = document.Render(1, viewRotation, 600, 600);
            Check(rendered.Width == (totalRotation % 180 == 0 ? 600 : 400)
                && rendered.Height == (totalRotation % 180 == 0 ? 400 : 600), "旋转预览尺寸错误。");
            var baseline = document.DeviceToPdf(1, viewRotation, rendered.Width, rendered.Height,
                rendered.Width * 0.18, rendered.Height * 0.25);
            AssertPoint(baseline, ExpectedPoint(totalRotation, 0.18, 0.25, 0, 0, PAGE_WIDTH, PAGE_HEIGHT));
            document.ApplyEdit(new PdfEditRequest
            {
                PageNumber = 1, Kind = PdfEditKind.InsertText, Text = "UPRIGHT", FontSize = 12,
                Font = PdfTextFont.Arial, Position = baseline, ViewRotation = viewRotation
            });
            var target = new PdfNormalizedBounds(0.15, 0.48, 0.35, 0.28);
            var bounds = BoundsFromPreview(document, rendered, viewRotation, target);
            var output = document.ApplyEdit(new PdfEditRequest
            {
                PageNumber = 1, Kind = PdfEditKind.InsertImage, Image = QuadrantImage(),
                Bounds = bounds, ViewRotation = viewRotation
            });
            var objects = document.EnumerateObjects(1, viewRotation);
            var inserted = objects.Objects.Single(item => item.Kind == PdfObjectKind.Image);
            AssertNormalizedBounds(inserted.PreviewBounds, target);
            rendered = document.Render(1, viewRotation, 600, 600);
            AssertQuadrants(rendered, target);
            var patch = ExtractPatch(rendered, (int)(rendered.Width * 0.18) - 3,
                (int)(rendered.Height * 0.25) - 30, 128, 36);
            if (referenceText is null)
            {
                referenceText = patch;
                Check(patch.Count(value => value < 128) > 150, "旋转文字基准区域没有可见文字。");
            }
            else
            {
                var difference = patch.Zip(referenceText, (actual, expected) => Math.Abs(actual - expected)).Average();
                Check(difference < 3, "文字插入在旋转预览中倒置、侧向或偏离选定基线。");
            }
            using var reopened = PdfiumRuntime.Open(output);
            AssertQuadrants(reopened.Render(1, viewRotation, 600, 600), target);
            AssertExtractable(document, output, ["UPRIGHT"], []);
        }
    }

    private static void CroppedPageCoordinates()
    {
        foreach (var originalRotation in new[] { 0, 90, 180, 270 })
        foreach (var viewRotation in new[] { 0, 90, 180, 270, -90, 450 })
        {
            using var document = PdfiumRuntime.Open(CreatePdf("", rotation: originalRotation,
                pageExtra: "/CropBox [20 30 280 180]"));
            var normalizedRotation = ((originalRotation + viewRotation) % 360 + 360) % 360;
            var view = document.EnumerateObjects(1, viewRotation);
            Check(view.Width == (normalizedRotation % 180 == 0 ? 260 : 150)
                && view.Height == (normalizedRotation % 180 == 0 ? 150 : 260), "带裁剪框的页面尺寸错误。");
            foreach (var sample in new[] { (0d, 0d), (1d, 1d), (0.231, 0.674) })
            {
                var actual = document.DeviceToPdf(1, viewRotation, 780, 450, sample.Item1 * 780, sample.Item2 * 450);
                AssertPoint(actual, ExpectedPoint(normalizedRotation, sample.Item1, sample.Item2, 20, 30, 260, 150));
            }
        }
    }

    private static void VectorMarks()
    {
        using var document = PdfiumRuntime.Open(CreatePdf("BT /F1 14 Tf 24 100 Td (MARKED_BODY) Tj ET"));
        foreach (var kind in new[] { PdfEditKind.AddHighlight, PdfEditKind.AddRectangle })
        {
            var output = document.ApplyEdit(new PdfEditRequest
            {
                PageNumber = 1, Kind = kind, Bounds = new PdfBounds(20, 90, 180, 120)
            });
            var objects = document.EnumerateObjects(1).Objects;
            var mark = objects.Single(item => item.Kind == PdfObjectKind.Path);
            Check(mark.CanEdit && objects.Count == 2, "标记没有生成可选择和删除的矢量对象。");
            AssertExtractable(document, output, ["MARKED_BODY"], []);
            output = document.ApplyEdit(new PdfEditRequest
            {
                PageNumber = 1, Kind = PdfEditKind.DeleteObject, ObjectIndex = mark.Index
            });
            Check(document.EnumerateObjects(1).Objects.Count == 1, "标记删除后仍留下额外对象。");
            AssertExtractable(document, output, ["MARKED_BODY"], []);
        }
    }

    private static void OwnedBuffersAndDisposal()
    {
        var source = CreatePdf("BT /F1 14 Tf 24 100 Td (OWNED_BODY) Tj ET");
        var document = PdfiumRuntime.Open(source);
        try
        {
            Array.Clear(source);
            Check(document.ExtractText(1).Contains("OWNED_BODY", StringComparison.Ordinal), "文档仍引用调用方的输入数组。");
            var output = document.ApplyEdit(new PdfEditRequest
            {
                PageNumber = 1, Kind = PdfEditKind.ReplaceText,
                ObjectIndex = FindText(document, "OWNED_BODY").Index, Text = "OWNED_RESULT"
            });
            Array.Clear(output);
            var saved = document.SaveAsCopy();
            using (var reopened = PdfiumRuntime.Open(saved))
                Check(reopened.ExtractText(1).Contains("OWNED_RESULT", StringComparison.Ordinal), "编辑器仍引用调用方的返回数组。");
            Array.Clear(saved);
            Check(document.ExtractText(1).Contains("OWNED_RESULT", StringComparison.Ordinal), "保存副本与内部缓冲区共用数组。");
        }
        finally { document.Dispose(); }
        document.Dispose();
        Expect<ObjectDisposedException>(() => _ = document.PageCount);
        Expect<ObjectDisposedException>(() => document.Render(1));
        Expect<ObjectDisposedException>(() => document.EnumerateObjects(1));
        Expect<ObjectDisposedException>(() => document.ExtractText(1));
        Expect<ObjectDisposedException>(() => document.SaveAsCopy());
    }

    private static PdfPageObjectInfo FindText(IPdfEditableDocument document, string text) =>
        document.EnumerateObjects(1).Objects.Single(item => item.Kind == PdfObjectKind.Text
            && item.Text.Contains(text, StringComparison.Ordinal));

    private static void AssertExtractable(IPdfEditableDocument document, byte[] saved,
        IReadOnlyList<string> present, IReadOnlyList<string> absent)
    {
        using var independent = PdfDocument.Open(saved);
        using var reopened = PdfiumRuntime.Open(saved);
        foreach (var text in new[] { document.ExtractText(1), reopened.ExtractText(1), independent.GetPage(1).Text })
        {
            foreach (var expected in present)
                Check(text.Contains(expected, StringComparison.Ordinal), "编辑结果未能被完整提取或重新打开。");
            foreach (var removed in absent)
                Check(!text.Contains(removed, StringComparison.Ordinal), "替换或删除后的原文字仍然可提取。");
        }
    }

    private static int ImageCount(byte[] bytes)
    {
        using var independent = PdfDocument.Open(bytes);
        return independent.GetPage(1).GetImages().Count();
    }

    private static (string Text, PdfPageObjectInfo[] Objects, byte[] Pixels) Snapshot(IPdfEditableDocument document) =>
        (document.ExtractText(1), document.EnumerateObjects(1).Objects.ToArray(),
            document.Render(1, maxWidth: 300, maxHeight: 200).Bgra);

    private static void AssertUnchanged(IPdfEditableDocument document,
        (string Text, PdfPageObjectInfo[] Objects, byte[] Pixels) before)
    {
        Check(document.ExtractText(1) == before.Text, "失败操作改变了原文字。");
        Check(document.EnumerateObjects(1).Objects.SequenceEqual(before.Objects), "失败操作改变了原对象或属性。");
        Check(document.Render(1, maxWidth: 300, maxHeight: 200).Bgra.AsSpan().SequenceEqual(before.Pixels),
            "失败操作改变了原页面像素。");
    }

    private static PdfBounds BoundsFromPreview(IPdfEditableDocument document, PdfBitmap rendered,
        int rotation, PdfNormalizedBounds bounds)
    {
        var first = document.DeviceToPdf(1, rotation, rendered.Width, rendered.Height,
            bounds.Left * rendered.Width, bounds.Top * rendered.Height);
        var second = document.DeviceToPdf(1, rotation, rendered.Width, rendered.Height,
            (bounds.Left + bounds.Width) * rendered.Width, (bounds.Top + bounds.Height) * rendered.Height);
        return new PdfBounds(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
            Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
    }

    private static PdfPoint ExpectedPoint(int rotation, double x, double y, double left, double bottom,
        double width, double height) => rotation switch
    {
        0 => new PdfPoint(left + width * x, bottom + height * (1 - y)),
        90 => new PdfPoint(left + width * y, bottom + height * x),
        180 => new PdfPoint(left + width * (1 - x), bottom + height * y),
        270 => new PdfPoint(left + width * (1 - y), bottom + height * (1 - x)),
        _ => throw new InvalidOperationException("测试旋转夹具无效。")
    };

    private static void AssertPoint(PdfPoint actual, PdfPoint expected) =>
        Check(Math.Abs(actual.X - expected.X) < 0.001 && Math.Abs(actual.Y - expected.Y) < 0.001,
            "设备坐标没有正确换算至 PDF 原始坐标。");

    private static void AssertNormalizedBounds(PdfNormalizedBounds actual, PdfNormalizedBounds expected) =>
        Check(Math.Abs(actual.Left - expected.Left) < 0.00001 && Math.Abs(actual.Top - expected.Top) < 0.00001
            && Math.Abs(actual.Width - expected.Width) < 0.00001 && Math.Abs(actual.Height - expected.Height) < 0.00001,
            "插入图片的旋转预览边界与选定范围不一致。");

    private static void AssertQuadrants(PdfBitmap bitmap, PdfNormalizedBounds bounds)
    {
        AssertPixel(bitmap, bounds.Left + bounds.Width * 0.25, bounds.Top + bounds.Height * 0.25, new PdfColor(255, 0, 0));
        AssertPixel(bitmap, bounds.Left + bounds.Width * 0.75, bounds.Top + bounds.Height * 0.25, new PdfColor(0, 255, 0));
        AssertPixel(bitmap, bounds.Left + bounds.Width * 0.25, bounds.Top + bounds.Height * 0.75, new PdfColor(0, 0, 255));
        AssertPixel(bitmap, bounds.Left + bounds.Width * 0.75, bounds.Top + bounds.Height * 0.75, new PdfColor(255, 255, 0));
    }

    private static void AssertPixel(PdfBitmap bitmap, double normalizedX, double normalizedY, PdfColor expected)
    {
        var offset = (int)(normalizedY * bitmap.Height) * bitmap.Stride + (int)(normalizedX * bitmap.Width) * 4;
        Check(Math.Abs(bitmap.Bgra[offset] - expected.Blue) <= 3
            && Math.Abs(bitmap.Bgra[offset + 1] - expected.Green) <= 3
            && Math.Abs(bitmap.Bgra[offset + 2] - expected.Red) <= 3 && bitmap.Bgra[offset + 3] == 255,
            "保存后的图片颜色、方向、位置或删除结果错误。");
    }

    private static bool HasBlueInk(PdfBitmap bitmap)
    {
        for (var index = 0; index < bitmap.Bgra.Length; index += 4)
            if (bitmap.Bgra[index] > 100 && bitmap.Bgra[index] > bitmap.Bgra[index + 2] * 2
                && bitmap.Bgra[index] > bitmap.Bgra[index + 1] * 2) return true;
        return false;
    }

    private static byte[] ExtractPatch(PdfBitmap bitmap, int x, int y, int width, int height)
    {
        var patch = new byte[width * height * 3];
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
            Buffer.BlockCopy(bitmap.Bgra, (y + row) * bitmap.Stride + (x + column) * 4,
                patch, (row * width + column) * 3, 3);
        return patch;
    }

    private static PdfBgraImage SolidImage(PdfColor color, bool paddedStride = false)
    {
        var stride = paddedStride ? 20 : 16;
        var bytes = new byte[stride * 4];
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            SetPixel(bytes, row * stride + column * 4, color);
        return new PdfBgraImage(bytes, 4, 4, stride);
    }

    private static PdfBgraImage QuadrantImage()
    {
        var bytes = new byte[8 * 8 * 4];
        var colors = new[] { new PdfColor(255, 0, 0), new PdfColor(0, 255, 0), new PdfColor(0, 0, 255), new PdfColor(255, 255, 0) };
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 8; column++)
            SetPixel(bytes, (row * 8 + column) * 4, colors[(row / 4) * 2 + column / 4]);
        return new PdfBgraImage(bytes, 8, 8, 32);
    }

    private static void SetPixel(byte[] bytes, int offset, PdfColor color)
    {
        bytes[offset] = color.Blue;
        bytes[offset + 1] = color.Green;
        bytes[offset + 2] = color.Red;
        bytes[offset + 3] = color.Alpha;
    }

    private static byte[] CreateImagePdf(string prefix = "", string suffix = "", bool transparency = false)
    {
        var extra = new List<string>
        {
            MakeStream(string.Concat(Enumerable.Repeat("FF0000", 16)) + ">",
                "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode")
        };
        if (transparency) extra.Add("<< /Type /ExtGState /ca 0.5 /CA 1 >>");
        return CreatePdf(prefix + "q 60 0 0 50 20 25 cm /Im1 Do Q" + suffix
            + "\nBT /F1 12 Tf 24 156 Td (UNCHANGED_TEXT) Tj ET",
            resources: "/XObject << /Im1 6 0 R >>" + (transparency ? " /ExtGState << /GS1 7 0 R >>" : ""),
            extraObjects: extra);
    }

    private static byte[] CreatePdf(string content, int rotation = 0, string pageExtra = "",
        string resources = "", IReadOnlyList<string>? extraObjects = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [4 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Rotate {rotation} "
                + $"/Resources << /Font << /F1 3 0 R >> {resources} >> /Contents 5 0 R {pageExtra} >>",
            MakeStream(content)
        };
        if (extraObjects is not null) objects.AddRange(extraObjects);
        using var output = new MemoryStream();
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n%LOCAL_SYNTHETIC_NATIVE_EDIT_TEST\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var crossReference = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Root 1 0 R /Size {objects.Count + 1} >>\nstartxref\n{crossReference}\n%%EOF\n");
        return output.ToArray();
    }

    private static string MakeStream(string content, string extra = "") =>
        $"<< /Length {Encoding.ASCII.GetByteCount(content)} {extra} >>\nstream\n{content}\nendstream";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"预期出现 {typeof(T).Name}，操作却成功。");
    }
}
