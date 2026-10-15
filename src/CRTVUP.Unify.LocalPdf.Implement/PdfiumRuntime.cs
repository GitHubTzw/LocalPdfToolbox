using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>统一 PDFium 生命周期与锁。只加载程序目录中的离线引擎，不初始化脚本或表单环境。</summary>
public static class PdfiumRuntime
{
    public const int MAX_RENDER_DIMENSION = 8192;
    public const int MAX_RENDER_PIXELS = 32 * 1024 * 1024;
    public static object SyncRoot { get; } = new();
    private static bool _initialized;

    static PdfiumRuntime()
    {
        NativeLibrary.SetDllImportResolver(typeof(PdfiumRuntime).Assembly, (name, _, _) =>
            name == "pdfium.dll"
                ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "pdfium.dll"))
                : nint.Zero);
    }

    public static PdfiumDocument Open(ReadOnlyMemory<byte> content) => new(content);

    internal static void EnsureInitialized()
    {
        // 所有调用（包括构造、关闭与最终回收）均在同一把锁中进行。
        lock (SyncRoot)
        {
            if (_initialized) return;
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PdfToolException("本地 PDF 引擎需要 64 位 Windows。");
            try
            {
                PdfiumNative.FPDF_InitLibrary();
                _initialized = true;
            }
            catch (Exception exception) when (exception is DllNotFoundException
                or BadImageFormatException or EntryPointNotFoundException)
            {
                throw new PdfToolException("离线 PDF 引擎缺失或版本不匹配，请使用完整的程序文件夹。");
            }
        }
    }

    internal static int QuarterTurns(int rotation)
    {
        if (rotation % 90 != 0)
            throw new PdfToolException("旋转角度必须是 90 度的整数倍。");
        return ((rotation % 360) + 360) % 360 / 90;
    }

    internal static void ValidatePixelSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MAX_RENDER_DIMENSION || height > MAX_RENDER_DIMENSION
            || (long)width * height > MAX_RENDER_PIXELS)
            throw new PdfToolException("图片尺寸超出上限：单边最多 8192 像素，总计最多 3200 万像素。");
    }
}

/// <summary>
/// 在内存中持有 PDF 及其固定地址；销毁后清零输入副本。编辑先在隔离副本中完成，保存成功才换入新文档。
/// 不访问文档路径、不写临时 PDF、不注册 JavaScript、网络、外链或交互表单回调。
/// </summary>
public sealed partial class PdfiumDocument : IPdfEditableDocument
{
    private const int MAX_PAGES = 5000;
    private const int MAX_PAGE_OBJECTS = 100_000;
    private const int MAX_TEXT_CHARACTERS = 2_000_000;
    private const int MAX_EDIT_CHARACTERS = 32_768;
    private const int NORMALIZED_DEVICE_SIZE = 1_000_000;
    private nint _handle;
    private byte[] _bytes = [];
    private GCHandle _pin;
    private int _pageCount;
    private string? _editingRestriction;
    private Dictionary<PdfTextFont, LoadedFont> _fonts = [];
    private bool _disposed;

    public PdfiumDocument(ReadOnlyMemory<byte> content)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            if (content.IsEmpty)
                throw new PdfToolException("PDF 不能为空。");
            PdfiumRuntime.EnsureInitialized();
            try
            {
                _bytes = content.ToArray();
                _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
                _handle = PdfiumNative.FPDF_LoadMemDocument64(_pin.AddrOfPinnedObject(),
                    (nuint)_bytes.Length, nint.Zero);
                if (_handle == nint.Zero)
                    throw new PdfToolException("无法打开此 PDF，它可能已损坏、带有密码或采用不支持的格式。");
                _pageCount = PdfiumNative.FPDF_GetPageCount(_handle);
                if (_pageCount <= 0 || _pageCount > MAX_PAGES)
                    throw new PdfToolException("PDF 页数无效；一次最多处理 5000 页。");
                _editingRestriction = GetDocumentRestriction();
            }
            catch
            {
                ReleaseCore();
                _disposed = true;
                throw;
            }
        }
    }

    ~PdfiumDocument()
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            if (_disposed) return;
            ReleaseCore();
            _disposed = true;
        }
    }

    public int PageCount
    {
        get
        {
            lock (PdfiumRuntime.SyncRoot)
            {
                ThrowIfDisposed();
                return _pageCount;
            }
        }
    }

    public (double Width, double Height) GetPageSize(int pageNumber, int rotation = 0)
    {
        lock (PdfiumRuntime.SyncRoot)
            return WithPage(pageNumber, page => GetPageSize(page, PdfiumRuntime.QuarterTurns(rotation)));
    }

    public PdfBitmap Render(int pageNumber, int rotation = 0, int maxWidth = 1400, int maxHeight = 1800)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            var turns = PdfiumRuntime.QuarterTurns(rotation);
            if (maxWidth <= 0 || maxHeight <= 0 || maxWidth > PdfiumRuntime.MAX_RENDER_DIMENSION
                || maxHeight > PdfiumRuntime.MAX_RENDER_DIMENSION)
                throw new PdfToolException("渲染边长必须在 1 到 8192 像素之间。");
            return WithPage(pageNumber, page =>
            {
                var (width, height) = GetPageSize(page, turns);
                var scale = Math.Min(maxWidth / width, maxHeight / height);
                var pixelsWide = Math.Max(1, (int)Math.Ceiling(width * scale));
                var pixelsHigh = Math.Max(1, (int)Math.Ceiling(height * scale));
                PdfiumRuntime.ValidatePixelSize(pixelsWide, pixelsHigh);
                nint bitmap = nint.Zero;
                byte[]? result = null;
                try
                {
                    bitmap = PdfiumNative.FPDFBitmap_Create(pixelsWide, pixelsHigh, 1);
                    RequireHandle(bitmap, "渲染内存不足，请降低预览尺寸。");
                    Require(PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, pixelsWide, pixelsHigh, 0xFFFFFFFF),
                        "无法准备页面预览。");
                    // FPDF_ANNOT 显示普通批注；没有表单环境，绝不执行任何文档动作。
                    PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelsWide, pixelsHigh, turns, 1);
                    var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
                    if (stride < pixelsWide * 4 || (long)stride * pixelsHigh > PdfiumRuntime.MAX_RENDER_PIXELS * 4L)
                        throw new PdfToolException("渲染器返回了无效的图片缓冲区。");
                    var pointer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
                    RequireHandle(pointer, "无法取得页面预览。");
                    result = GC.AllocateUninitializedArray<byte>(checked(stride * pixelsHigh));
                    Marshal.Copy(pointer, result, 0, result.Length);
                    var output = new PdfBitmap(result, pixelsWide, pixelsHigh, stride);
                    result = null;
                    return output;
                }
                finally
                {
                    if (result is not null) CryptographicOperations.ZeroMemory(result);
                    if (bitmap != nint.Zero) PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            });
        }
    }

    public PdfPageObjectCollection EnumerateObjects(int pageNumber, int rotation = 0)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            var turns = PdfiumRuntime.QuarterTurns(rotation);
            return WithPage(pageNumber, page =>
            {
                var (width, height) = GetPageSize(page, turns);
                var count = PdfiumNative.FPDFPage_CountObjects(page);
                if (count < 0 || count > MAX_PAGE_OBJECTS)
                    throw new PdfToolException("页面对象过多或结构无效，无法列出可编辑内容。");
                var objects = new List<PdfPageObjectInfo>(count);
                var warnings = new List<string>();
                if (_editingRestriction is not null) warnings.Add(_editingRestriction);
                nint textPage = nint.Zero;
                try
                {
                    textPage = PdfiumNative.FPDFText_LoadPage(page);
                    var textLength = 0;
                    for (var index = 0; index < count; index++)
                    {
                        var item = PdfiumNative.FPDFPage_GetObject(page, index);
                        if (item == nint.Zero) continue;
                        var kind = GetObjectKind(item);
                        var limitation = _editingRestriction ?? GetObjectRestriction(item, kind);
                        var hasBounds = TryGetBounds(item, out var bounds);
                        if (!hasBounds) limitation ??= "此对象没有有效的边界，暂不支持编辑。";
                        var text = kind == PdfObjectKind.Text && textPage != nint.Zero
                            ? GetObjectText(item, textPage) : string.Empty;
                        textLength = checked(textLength + text.Length);
                        if (textLength > MAX_TEXT_CHARACTERS)
                            throw new PdfToolException("此页面包含过多文字，请使用更小的文档。");
                        var size = 0f;
                        if (kind == PdfObjectKind.Text) PdfiumNative.FPDFTextObj_GetFontSize(item, out size);
                        objects.Add(new PdfPageObjectInfo(index, kind, text, size, bounds,
                            hasBounds ? NormalizeBounds(page, turns, bounds) : default,
                            limitation is null, limitation));
                    }
                }
                finally
                {
                    if (textPage != nint.Zero) PdfiumNative.FPDFText_ClosePage(textPage);
                }
                if (objects.Any(item => item.Kind == PdfObjectKind.Form))
                    warnings.Add("此页含嵌套表单图形：只显示外框，不深入修改其内容，以免影响共用该图形的其他页面。");
                if (!objects.Any(item => item.Kind == PdfObjectKind.Text && item.Text.Length > 0))
                    warnings.Add("未发现可直接修改的文字。扫描件中的字属于图片；此版本不提供 OCR 文字识别。");
                if (objects.Any(item => item.Kind == PdfObjectKind.Text))
                    warnings.Add("替换文字会嵌入本机黑体或 Arial；保留位置、字号和颜色，字形、字宽可能与原文不同。");
                return new PdfPageObjectCollection(pageNumber, width, height, objects.AsReadOnly(), warnings.AsReadOnly());
            });
        }
    }

    public string ExtractText(int pageNumber)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            return WithPage(pageNumber, page =>
            {
                var textPage = PdfiumNative.FPDFText_LoadPage(page);
                RequireHandle(textPage, "无法读取此页文字。");
                byte[]? buffer = null;
                GCHandle bufferPin = default;
                try
                {
                    var count = PdfiumNative.FPDFText_CountChars(textPage);
                    if (count < 0 || count > MAX_TEXT_CHARACTERS)
                        throw new PdfToolException("此页文字结构无效或文字量超过处理上限。");
                    if (count == 0) return string.Empty;
                    // 每个字符预留两个 UTF-16 码元，兼容 PDF 中的补充平面字符。
                    buffer = new byte[checked((count * 2 + 1) * 2)];
                    bufferPin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    var written = PdfiumNative.FPDFText_GetText(textPage, 0, count, bufferPin.AddrOfPinnedObject());
                    if (written <= 0 || written > buffer.Length / 2)
                        throw new PdfToolException("无法完整提取此页文字。");
                    return DecodeUtf16(buffer, written * 2);
                }
                finally
                {
                    if (buffer is not null) CryptographicOperations.ZeroMemory(buffer);
                    if (bufferPin.IsAllocated) bufferPin.Free();
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }
            });
        }
    }

    public PdfPoint DeviceToPdf(int pageNumber, int rotation, int pixelWidth, int pixelHeight, double x, double y)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            PdfiumRuntime.ValidatePixelSize(pixelWidth, pixelHeight);
            var turns = PdfiumRuntime.QuarterTurns(rotation);
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > pixelWidth || y > pixelHeight)
                throw new PdfToolException("请选择页面预览范围内的位置。");
            return WithPage(pageNumber, page =>
            {
                // 先由 PDFium 求三点构成的逆变换，再插值得到亚像素精度，兼容 CropBox 和原始旋转。
                Require(PdfiumNative.FPDF_DeviceToPage(page, 0, 0, pixelWidth, pixelHeight, turns,
                    0, 0, out var x0, out var y0), "无法换算页面坐标。");
                Require(PdfiumNative.FPDF_DeviceToPage(page, 0, 0, pixelWidth, pixelHeight, turns,
                    pixelWidth, 0, out var x1, out var y1), "无法换算页面坐标。");
                Require(PdfiumNative.FPDF_DeviceToPage(page, 0, 0, pixelWidth, pixelHeight, turns,
                    0, pixelHeight, out var x2, out var y2), "无法换算页面坐标。");
                return new PdfPoint(x0 + (x1 - x0) * x / pixelWidth + (x2 - x0) * y / pixelHeight,
                    y0 + (y1 - y0) * x / pixelWidth + (y2 - y0) * y / pixelHeight);
            });
        }
    }

    public byte[] ApplyEdit(PdfEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (PdfiumRuntime.SyncRoot)
        {
            ThrowIfDisposed();
            EnsureEditable();
            byte[]? imageCopy = null;
            byte[]? output = null;
            try
            {
                if (request.Image is not null)
                {
                    ValidateImage(request.Image);
                    imageCopy = request.Image.Bgra.ToArray();
                    request = request with { Image = request.Image with { Bgra = imageCopy } };
                }
                using var candidate = new PdfiumDocument(_bytes);
                candidate.WithPage(request.PageNumber, page =>
                {
                    candidate.ApplyEditCore(page, request);
                    Require(PdfiumNative.FPDFPage_GenerateContent(page), "无法生成修改后的页面内容，原内容已保留。");
                    return 0;
                });
                output = candidate.SaveAsCopyCore();
                // 必须重新打开保存结果成功后才能提交，避免 UI 接受了一个无法再次读取的 PDF。
                using var replacement = new PdfiumDocument(output);
                SwapState(replacement);
                var completed = output;
                output = null;
                return completed;
            }
            catch (OutOfMemoryException)
            {
                throw new PdfToolException("内存不足，修改没有保存，请使用更小的图片或文档。");
            }
            finally
            {
                if (imageCopy is not null) CryptographicOperations.ZeroMemory(imageCopy);
                if (output is not null) CryptographicOperations.ZeroMemory(output);
            }
        }
    }

    public byte[] SaveAsCopy()
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            ThrowIfDisposed();
            EnsureEditable();
            return SaveAsCopyCore();
        }
    }

    public void Dispose()
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            if (_disposed) return;
            ReleaseCore();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private T WithPage<T>(int pageNumber, Func<nint, T> action)
    {
        ThrowIfDisposed();
        if (pageNumber < 1 || pageNumber > _pageCount)
            throw new PdfToolException("页码超出文档范围，请重新选择页面。");
        var page = PdfiumNative.FPDF_LoadPage(_handle, pageNumber - 1);
        RequireHandle(page, "无法读取此页内容。");
        try
        {
            GetPageSize(page, 0);
            return action(page);
        }
        finally { PdfiumNative.FPDF_ClosePage(page); }
    }

    private void ApplyEditCore(nint page, PdfEditRequest request)
    {
        switch (request.Kind)
        {
            case PdfEditKind.DeleteObject:
                var deleted = GetEditableObject(page, request.ObjectIndex);
                Require(PdfiumNative.FPDFPage_RemoveObject(page, deleted), "无法删除选中对象。");
                PdfiumNative.FPDFPageObj_Destroy(deleted);
                break;
            case PdfEditKind.ReplaceText:
                var text = GetEditableObject(page, request.ObjectIndex, PdfObjectKind.Text);
                var textMatrix = GetMatrix(text);
                Require(PdfiumNative.FPDFTextObj_GetFontSize(text, out var sourceSize), "无法读取原文字字号。");
                InsertText(page, request, textMatrix, request.FontSize ?? sourceSize, text);
                break;
            case PdfEditKind.InsertText:
                var position = request.Position ?? throw new PdfToolException("请先选择插入文字的位置。");
                ValidateCoordinate(position.X);
                ValidateCoordinate(position.Y);
                var axes = GetInsertionAxes(page, request.ViewRotation);
                InsertText(page, request, new PdfiumNative.Matrix(axes.Xx, axes.Xy, axes.Yx, axes.Yy, (float)position.X, (float)position.Y),
                    request.FontSize ?? 16, nint.Zero);
                break;
            case PdfEditKind.ReplaceImage:
                var original = GetEditableObject(page, request.ObjectIndex, PdfObjectKind.Image);
                var replacementImage = CreateImage(request.Image, GetMatrix(original));
                ReplaceObject(page, original, replacementImage, request.ObjectIndex);
                break;
            case PdfEditKind.InsertImage:
                var imageBounds = ValidateBounds(request.Bounds);
                var image = CreateImage(request.Image, GetInsertionMatrix(page, request.ViewRotation, imageBounds));
                InsertOwnedObject(page, image);
                break;
            case PdfEditKind.AddHighlight:
            case PdfEditKind.AddRectangle:
                InsertOwnedObject(page, CreateRectangle(request));
                break;
            default:
                throw new PdfToolException("不支持此编辑操作。");
        }
    }

    private static (float Xx, float Xy, float Yx, float Yy) GetInsertionAxes(nint page, int rotation)
    {
        var turns = PdfiumRuntime.QuarterTurns(rotation);
        Require(PdfiumNative.FPDF_DeviceToPage(page, 0, 0, 1000, 1000, turns, 0, 0, out var x0, out var y0), "无法换算插入方向。");
        Require(PdfiumNative.FPDF_DeviceToPage(page, 0, 0, 1000, 1000, turns, 1000, 0, out var x1, out var y1), "无法换算插入方向。");
        Require(PdfiumNative.FPDF_DeviceToPage(page, 0, 0, 1000, 1000, turns, 0, 1000, out var x2, out var y2), "无法换算插入方向。");
        var horizontal = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        var vertical = Math.Sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2));
        if (horizontal <= 0 || vertical <= 0) throw new PdfToolException("页面坐标无法用于插入内容。");
        return ((float)Math.Round((x1 - x0) / horizontal), (float)Math.Round((y1 - y0) / horizontal),
            (float)Math.Round((x0 - x2) / vertical), (float)Math.Round((y0 - y2) / vertical));
    }

    private static PdfiumNative.Matrix GetInsertionMatrix(nint page, int rotation, PdfBounds bounds)
    {
        var axes = GetInsertionAxes(page, rotation);
        var width = axes.Xx == 0 ? bounds.Height : bounds.Width;
        var height = axes.Yx == 0 ? bounds.Height : bounds.Width;
        var a = (float)(axes.Xx * width);
        var b = (float)(axes.Xy * width);
        var c = (float)(axes.Yx * height);
        var d = (float)(axes.Yy * height);
        return new PdfiumNative.Matrix(a, b, c, d, (float)bounds.Left - Math.Min(0, a) - Math.Min(0, c),
            (float)bounds.Bottom - Math.Min(0, b) - Math.Min(0, d));
    }

    private void InsertText(nint page, PdfEditRequest request, PdfiumNative.Matrix matrix,
        double size, nint original)
    {
        var lines = PrepareText(request.Text);
        if (!double.IsFinite(size) || size < 1 || size > 1000)
            throw new PdfToolException("字号必须在 1 到 1000 点之间。");
        var font = GetFont(request.Font, string.Join('\n', lines));
        var fill = request.Color ?? (original == nint.Zero ? new PdfColor(0, 0, 0) : GetFillColor(original));
        var mode = original == nint.Zero ? 0 : PdfiumNative.FPDFTextObj_GetTextRenderMode(original);
        if (mode < 0 || mode > 3)
            throw new PdfToolException("此文字使用裁剪绘制方式，暂不支持安全替换。");
        var stroke = new PdfColor(0, 0, 0);
        var strokeWidth = 1f;
        if (original != nint.Zero && mode is 1 or 2)
        {
            stroke = request.Color ?? GetStrokeColor(original);
            Require(PdfiumNative.FPDFPageObj_GetStrokeWidth(original, out strokeWidth), "无法读取文字轮廓宽度。");
        }
        var insertionIndex = original == nint.Zero ? PdfiumNative.FPDFPage_CountObjects(page) : request.ObjectIndex;
        var added = 0;
        for (var line = 0; line < lines.Length; line++)
        {
            if (lines[line].Length == 0) continue;
            var item = PdfiumNative.FPDFPageObj_CreateTextObj(_handle, font.Handle, (float)size);
            RequireHandle(item, "无法创建文字对象。");
            var owned = false;
            try
            {
                SetText(item, lines[line]);
                var lineMatrix = matrix;
                var offset = -line * size * 1.35;
                lineMatrix.E += (float)(matrix.C * offset);
                lineMatrix.F += (float)(matrix.D * offset);
                Require(PdfiumNative.FPDFPageObj_SetMatrix(item, ref lineMatrix), "无法设置文字位置。");
                SetFillColor(item, fill);
                Require(PdfiumNative.FPDFTextObj_SetTextRenderMode(item, mode), "无法保留文字绘制方式。");
                if (mode is 1 or 2)
                {
                    SetStrokeColor(item, stroke);
                    Require(PdfiumNative.FPDFPageObj_SetStrokeWidth(item, strokeWidth), "无法设置文字轮廓宽度。");
                }
                Require(PdfiumNative.FPDFPage_InsertObjectAtIndex(page, item, (nuint)(insertionIndex + added)),
                    "无法插入文字对象。");
                owned = true;
                added++;
            }
            finally
            {
                if (!owned) PdfiumNative.FPDFPageObj_Destroy(item);
            }
        }
        if (original != nint.Zero)
        {
            Require(PdfiumNative.FPDFPage_RemoveObject(page, original), "无法移除原文字对象。");
            PdfiumNative.FPDFPageObj_Destroy(original);
        }
    }

    private nint CreateImage(PdfBgraImage? image, PdfiumNative.Matrix matrix)
    {
        if (image is null) throw new PdfToolException("请先选择要插入的本地图片。");
        ValidateImage(image);
        var item = PdfiumNative.FPDFPageObj_NewImageObj(_handle);
        RequireHandle(item, "无法创建图片对象。");
        nint bitmap = nint.Zero;
        var completed = false;
        try
        {
            // 让 PDFium 自己拥有像素内存，避免对象保留调用方数组的悬空地址。
            bitmap = PdfiumNative.FPDFBitmap_Create(image.Width, image.Height, 1);
            RequireHandle(bitmap, "图片内存不足，请降低图片分辨率。");
            var pointer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
            var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            RequireHandle(pointer, "无法准备图片缓冲区。");
            if (stride < image.Width * 4) throw new PdfToolException("图片缓冲区无效。");
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(image.Bgra, row * image.Stride, pointer + row * stride, image.Width * 4);
            Require(PdfiumNative.FPDFImageObj_SetBitmap(nint.Zero, 0, item, bitmap), "无法写入图片内容。");
            Require(PdfiumNative.FPDFPageObj_SetMatrix(item, ref matrix), "无法设置图片位置。");
            completed = true;
            return item;
        }
        finally
        {
            if (bitmap != nint.Zero) PdfiumNative.FPDFBitmap_Destroy(bitmap);
            if (!completed) PdfiumNative.FPDFPageObj_Destroy(item);
        }
    }

    private static nint CreateRectangle(PdfEditRequest request)
    {
        var bounds = ValidateBounds(request.Bounds);
        var item = PdfiumNative.FPDFPageObj_CreateNewRect((float)bounds.Left, (float)bounds.Bottom,
            (float)bounds.Width, (float)bounds.Height);
        RequireHandle(item, "无法创建矩形标记。");
        var completed = false;
        try
        {
            if (request.Kind == PdfEditKind.AddHighlight)
            {
                SetFillColor(item, request.Color ?? new PdfColor(255, 225, 0, 72));
                PdfiumNative.FPDFPageObj_SetBlendMode(item, "Multiply");
                Require(PdfiumNative.FPDFPath_SetDrawMode(item, 2, 0), "无法设置高亮样式。");
            }
            else
            {
                if (!double.IsFinite(request.StrokeWidth) || request.StrokeWidth <= 0 || request.StrokeWidth > 100)
                    throw new PdfToolException("边框宽度必须大于 0 且不超过 100 点。");
                SetStrokeColor(item, request.Color ?? new PdfColor(220, 45, 40));
                Require(PdfiumNative.FPDFPageObj_SetStrokeWidth(item, (float)request.StrokeWidth), "无法设置边框宽度。");
                Require(PdfiumNative.FPDFPath_SetDrawMode(item, 0, 1), "无法设置边框样式。");
            }
            completed = true;
            return item;
        }
        finally { if (!completed) PdfiumNative.FPDFPageObj_Destroy(item); }
    }

    private static void InsertOwnedObject(nint page, nint item)
    {
        if (PdfiumNative.FPDFPage_InsertObject(page, item) != 0) return;
        PdfiumNative.FPDFPageObj_Destroy(item);
        throw new PdfToolException("无法把对象加入页面。");
    }

    private static void ReplaceObject(nint page, nint original, nint replacement, int index)
    {
        var owned = false;
        try
        {
            Require(PdfiumNative.FPDFPage_InsertObjectAtIndex(page, replacement, (nuint)index), "无法替换页面对象。");
            owned = true;
            Require(PdfiumNative.FPDFPage_RemoveObject(page, original), "无法移除原页面对象。");
            PdfiumNative.FPDFPageObj_Destroy(original);
        }
        finally { if (!owned) PdfiumNative.FPDFPageObj_Destroy(replacement); }
    }

    private nint GetEditableObject(nint page, int index, PdfObjectKind? expectedKind = null)
    {
        var count = PdfiumNative.FPDFPage_CountObjects(page);
        if (index < 0 || index >= count) throw new PdfToolException("选中的对象已失效，请刷新页面后重新选择。");
        var item = PdfiumNative.FPDFPage_GetObject(page, index);
        RequireHandle(item, "选中的对象已失效，请重新选择。");
        var kind = GetObjectKind(item);
        if (expectedKind is not null && kind != expectedKind)
            throw new PdfToolException("选中对象类型与此操作不一致，请重新选择。");
        var restriction = _editingRestriction ?? GetObjectRestriction(item, kind);
        if (restriction is not null) throw new PdfToolException(restriction);
        return item;
    }

    private string? GetDocumentRestriction()
    {
        if (PdfiumNative.FPDF_GetSecurityHandlerRevision(_handle) >= 0)
            return "此文档已加密；当前版本只编辑未加密的普通 PDF。";
        if (PdfiumNative.FPDF_GetSignatureCount(_handle) > 0)
            return "此文档有数字签名；修改会使签名失效，当前版本只提供预览。";
        if (PdfiumNative.FPDF_GetFormType(_handle) != 0)
            return "此文档含交互表单；当前版本不修改表单文档，以免改变填写内容。";
        return null;
    }

    private static string? GetObjectRestriction(nint item, PdfObjectKind kind)
    {
        if (kind == PdfObjectKind.Form)
            return "嵌套表单图形可能被其他页面共用，暂不支持直接修改其内部内容。";
        if (kind == PdfObjectKind.Other)
            return "此类页面对象暂不支持直接编辑。";
        if (kind == PdfObjectKind.Text && PdfiumNative.FPDFTextObj_GetTextRenderMode(item) is < 0 or > 3)
            return "此文字参与页面裁剪，暂不支持直接替换或删除。";
        var clip = PdfiumNative.FPDFPageObj_GetClipPath(item);
        // 文字轮廓构成的裁剪可以没有路径（CountPaths == 0）；真正没有裁剪时返回 -1。
        if (clip != nint.Zero && PdfiumNative.FPDFClipPath_CountPaths(clip) >= 0)
            return "此对象带裁剪蒙版，当前版本不修改，以免改变可见范围。";
        if (kind is PdfObjectKind.Text or PdfObjectKind.Image && PdfiumNative.FPDFPageObj_HasTransparency(item) != 0)
            return "此文字或图片使用透明度、混合方式或软蒙版，当前版本不修改，以免改变显示效果。";
        return null;
    }

    private static PdfObjectKind GetObjectKind(nint item) => PdfiumNative.FPDFPageObj_GetType(item) switch
    {
        1 => PdfObjectKind.Text,
        2 => PdfObjectKind.Path,
        3 => PdfObjectKind.Image,
        5 => PdfObjectKind.Form,
        _ => PdfObjectKind.Other
    };

    private static (double Width, double Height) GetPageSize(nint page, int quarterTurns)
    {
        var width = (double)PdfiumNative.FPDF_GetPageWidthF(page);
        var height = (double)PdfiumNative.FPDF_GetPageHeightF(page);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || width > 1_000_000 || height > 1_000_000)
            throw new PdfToolException("页面尺寸无效或超过处理上限。");
        return quarterTurns % 2 == 0 ? (width, height) : (height, width);
    }

    private static bool TryGetBounds(nint item, out PdfBounds bounds)
    {
        bounds = default;
        if (PdfiumNative.FPDFPageObj_GetBounds(item, out var left, out var bottom, out var right, out var top) == 0
            || !float.IsFinite(left) || !float.IsFinite(bottom) || !float.IsFinite(right) || !float.IsFinite(top))
            return false;
        if (Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), Math.Max(Math.Abs(bottom), Math.Abs(top))) > 10_000_000)
            return false;
        bounds = new PdfBounds(Math.Min(left, right), Math.Min(bottom, top), Math.Max(left, right), Math.Max(bottom, top));
        return true;
    }

    private static PdfNormalizedBounds NormalizeBounds(nint page, int turns, PdfBounds bounds)
    {
        var points = new[]
        {
            new PdfPoint(bounds.Left, bounds.Bottom), new PdfPoint(bounds.Right, bounds.Bottom),
            new PdfPoint(bounds.Left, bounds.Top), new PdfPoint(bounds.Right, bounds.Top)
        };
        var minX = 1d;
        var minY = 1d;
        var maxX = 0d;
        var maxY = 0d;
        foreach (var point in points)
        {
            Require(PdfiumNative.FPDF_PageToDevice(page, 0, 0, NORMALIZED_DEVICE_SIZE, NORMALIZED_DEVICE_SIZE,
                turns, point.X, point.Y, out var x, out var y), "无法计算对象的预览位置。");
            var normalizedX = Math.Clamp(x / (double)NORMALIZED_DEVICE_SIZE, 0, 1);
            var normalizedY = Math.Clamp(y / (double)NORMALIZED_DEVICE_SIZE, 0, 1);
            minX = Math.Min(minX, normalizedX);
            maxX = Math.Max(maxX, normalizedX);
            minY = Math.Min(minY, normalizedY);
            maxY = Math.Max(maxY, normalizedY);
        }
        return new PdfNormalizedBounds(minX, minY, maxX - minX, maxY - minY);
    }

    private static PdfiumNative.Matrix GetMatrix(nint item)
    {
        Require(PdfiumNative.FPDFPageObj_GetMatrix(item, out var matrix), "无法读取对象的变换矩阵。");
        if (!float.IsFinite(matrix.A) || !float.IsFinite(matrix.B) || !float.IsFinite(matrix.C)
            || !float.IsFinite(matrix.D) || !float.IsFinite(matrix.E) || !float.IsFinite(matrix.F))
            throw new PdfToolException("对象坐标无效，无法替换。");
        return matrix;
    }

    private static string GetObjectText(nint item, nint textPage)
    {
        var size = PdfiumNative.FPDFTextObj_GetText(item, textPage, nint.Zero, 0);
        if (size <= 2) return string.Empty;
        if (size > MAX_TEXT_CHARACTERS * 4 || size % 2 != 0)
            throw new PdfToolException("文字对象过大或编码无效。");
        var buffer = new byte[(int)size];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var written = PdfiumNative.FPDFTextObj_GetText(item, textPage, pin.AddrOfPinnedObject(), size);
            if (written > size || written % 2 != 0) throw new PdfToolException("无法完整读取文字对象。");
            return DecodeUtf16(buffer, (int)written);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            pin.Free();
        }
    }

    private static string DecodeUtf16(byte[] buffer, int byteCount)
    {
        if (byteCount >= 2 && buffer[byteCount - 1] == 0 && buffer[byteCount - 2] == 0) byteCount -= 2;
        return Encoding.Unicode.GetString(buffer, 0, byteCount);
    }

    private static string[] PrepareText(string? text)
    {
        if (string.IsNullOrEmpty(text)) throw new PdfToolException("请输入新文字；若需移除内容，请使用删除对象。");
        if (text.Length > MAX_EDIT_CHARACTERS) throw new PdfToolException("一次最多输入 32768 个字符。");
        foreach (var character in text)
        {
            if (char.IsSurrogate(character))
                throw new PdfToolException("当前文字引擎暂不支持表情或补充平面字符，请使用常用中文、数字和字母。");
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                throw new PdfToolException("文字包含不支持的控制字符。");
            if (character is >= '\u0590' and <= '\u0FFF')
                throw new PdfToolException("当前文字引擎暂不支持需要复杂字形组合或从右向左排版的文字。");
        }
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 512) throw new PdfToolException("一次最多插入 512 行文字，请分批操作。");
        if (lines.All(string.IsNullOrWhiteSpace)) throw new PdfToolException("请输入可见文字；若需移除内容，请使用删除对象。");
        return lines;
    }

    private static void SetText(nint item, string text)
    {
        var buffer = Encoding.Unicode.GetBytes(text + '\0');
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { Require(PdfiumNative.FPDFText_SetText(item, pin.AddrOfPinnedObject()), "无法设置文字内容。"); }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            pin.Free();
        }
    }

    private LoadedFont GetFont(PdfTextFont requested, string text)
    {
        if (!Enum.IsDefined(requested)) throw new PdfToolException("字体选择无效。");
        var selected = requested == PdfTextFont.Auto
            ? (text.Any(IsChineseCharacter) ? PdfTextFont.SimHei : PdfTextFont.Arial) : requested;
        if (!_fonts.TryGetValue(selected, out var font))
        {
            font = LoadedFont.Load(_handle, selected);
            try { _fonts.Add(selected, font); }
            catch
            {
                // 登记失败时字体尚未归文档管理，必须回收句柄与固定的完整 TTF 缓冲区。
                font.CloseHandle();
                font.ReleaseBytes();
                throw;
            }
        }
        foreach (var character in text.Distinct())
        {
            if (character is '\r' or '\n' or '\t') continue;
            if (!font.HasGlyph(character))
                throw new PdfToolException("所选本机字体不包含部分文字，请改用黑体或调整字符；修改尚未保存。");
        }
        return font;
    }

    private static bool IsChineseCharacter(char character) => character is
        >= '\u2E80' and <= '\u2FFF' or >= '\u3000' and <= '\u303F'
        or >= '\u3100' and <= '\u312F' or >= '\u31A0' and <= '\u31BF'
        or >= '\u3200' and <= '\u33FF' or >= '\u3400' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F'
        or >= '\uFF00' and <= '\uFFEF';

    private static PdfColor GetFillColor(nint item)
    {
        Require(PdfiumNative.FPDFPageObj_GetFillColor(item, out var r, out var g, out var b, out var a),
            "此文字使用特殊填充颜色，无法原样保留，请显式选择新颜色。");
        return new PdfColor((byte)r, (byte)g, (byte)b, (byte)a);
    }

    private static PdfColor GetStrokeColor(nint item)
    {
        Require(PdfiumNative.FPDFPageObj_GetStrokeColor(item, out var r, out var g, out var b, out var a),
            "此文字使用特殊轮廓颜色，无法原样保留，请显式选择新颜色。");
        return new PdfColor((byte)r, (byte)g, (byte)b, (byte)a);
    }

    private static void SetFillColor(nint item, PdfColor color) =>
        Require(PdfiumNative.FPDFPageObj_SetFillColor(item, color.Red, color.Green, color.Blue, color.Alpha), "无法设置填充颜色。");

    private static void SetStrokeColor(nint item, PdfColor color) =>
        Require(PdfiumNative.FPDFPageObj_SetStrokeColor(item, color.Red, color.Green, color.Blue, color.Alpha), "无法设置轮廓颜色。");

    private static PdfBounds ValidateBounds(PdfBounds? value)
    {
        if (value is not { } bounds) throw new PdfToolException("请先在页面中选定矩形范围。");
        ValidateCoordinate(bounds.Left);
        ValidateCoordinate(bounds.Bottom);
        ValidateCoordinate(bounds.Right);
        ValidateCoordinate(bounds.Top);
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 1_000_000 || bounds.Height > 1_000_000)
            throw new PdfToolException("矩形范围无效，请从页面中重新选择。");
        return bounds;
    }

    private static void ValidateCoordinate(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 10_000_000)
            throw new PdfToolException("页面坐标无效或超过上限。");
    }

    private static void ValidateImage(PdfBgraImage image)
    {
        PdfiumRuntime.ValidatePixelSize(image.Width, image.Height);
        if (image.Bgra is null || image.Stride < image.Width * 4
            || (long)image.Stride * image.Height > image.Bgra.LongLength
            || image.Bgra.LongLength > PdfiumRuntime.MAX_RENDER_PIXELS * 4L)
            throw new PdfToolException("图片必须是有效的 BGRA32 像素数据，且不能超过像素数量上限。");
    }

    private byte[] SaveAsCopyCore()
    {
        using var buffer = new SaveBuffer();
        PdfiumNative.WriteBlockDelegate callback = buffer.Write;
        var writer = new PdfiumNative.FileWrite
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
        };
        try
        {
            // FPDF_NO_INCREMENTAL：重写页面内容，而非把修改追加到原始文件末尾。
            var saved = PdfiumNative.FPDF_SaveAsCopy(_handle, ref writer, 2);
            if (buffer.LimitExceeded) throw new PdfToolException("修改后文件超出单次可载入内存的范围。");
            if (saved == 0 || buffer.Failed) throw new PdfToolException("无法在内存中保存修改，原文件未改变。");
            return buffer.ToArray();
        }
        finally { GC.KeepAlive(callback); }
    }

    private void SwapState(PdfiumDocument other)
    {
        (_handle, other._handle) = (other._handle, _handle);
        (_bytes, other._bytes) = (other._bytes, _bytes);
        (_pin, other._pin) = (other._pin, _pin);
        (_pageCount, other._pageCount) = (other._pageCount, _pageCount);
        (_editingRestriction, other._editingRestriction) = (other._editingRestriction, _editingRestriction);
        (_fonts, other._fonts) = (other._fonts, _fonts);
    }

    private void ReleaseCore()
    {
        // 新建字体的缓冲区一直保留到文档关闭；页面与文档不会持有已经释放的地址。
        foreach (var font in _fonts.Values) font.CloseHandle();
        if (_handle != nint.Zero)
        {
            PdfiumNative.FPDF_CloseDocument(_handle);
            _handle = nint.Zero;
        }
        foreach (var font in _fonts.Values) font.ReleaseBytes();
        _fonts.Clear();
        CryptographicOperations.ZeroMemory(_bytes);
        if (_pin.IsAllocated) _pin.Free();
        _bytes = [];
    }

    private void EnsureEditable()
    {
        if (_editingRestriction is not null) throw new PdfToolException(_editingRestriction);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void Require(int result, string message) { if (result == 0) throw new PdfToolException(message); }
    private static void RequireHandle(nint handle, string message) { if (handle == nint.Zero) throw new PdfToolException(message); }

    private sealed class SaveBuffer : IDisposable
    {
        private const int CHUNK_SIZE = 64 * 1024;
        private readonly List<byte[]> _chunks = [];
        private int _length;
        public bool LimitExceeded { get; private set; }
        public bool Failed { get; private set; }

        public int Write(nint _, nint data, uint size)
        {
            if (Failed || LimitExceeded) return 0;
            if (size > int.MaxValue - (long)_length)
            {
                LimitExceeded = true;
                return 0;
            }
            if (size == 0) return 1;
            if (data == nint.Zero) { Failed = true; return 0; }
            try
            {
                var copied = 0;
                while (copied < (int)size)
                {
                    var chunkOffset = _length % CHUNK_SIZE;
                    if (chunkOffset == 0) _chunks.Add(new byte[CHUNK_SIZE]);
                    var count = Math.Min(CHUNK_SIZE - chunkOffset, (int)size - copied);
                    Marshal.Copy(data + copied, _chunks[^1], chunkOffset, count);
                    copied += count;
                    _length += count;
                }
                return 1;
            }
            catch (Exception)
            {
                // 异常不能越过非托管回调边界；由上层返回不含文档内容的统一错误。
                Failed = true;
                return 0;
            }
        }

        public byte[] ToArray()
        {
            if (_length == 0) throw new PdfToolException("PDF 引擎返回了空文档。");
            var output = GC.AllocateUninitializedArray<byte>(_length);
            var offset = 0;
            foreach (var chunk in _chunks)
            {
                var count = Math.Min(CHUNK_SIZE, _length - offset);
                chunk.AsSpan(0, count).CopyTo(output.AsSpan(offset));
                offset += count;
            }
            return output;
        }

        public void Dispose()
        {
            foreach (var chunk in _chunks) CryptographicOperations.ZeroMemory(chunk);
            _chunks.Clear();
            _length = 0;
        }
    }

    private sealed class LoadedFont
    {
        private const int MAX_FONT_BYTES = 32 * 1024 * 1024;
        private byte[] _bytes;
        private GCHandle _pin;
        private readonly int _cmapOffset;
        private readonly int _cmapFormat;
        public nint Handle { get; private set; }

        private LoadedFont(byte[] bytes, GCHandle pin, nint handle, int cmapOffset, int cmapFormat)
        {
            _bytes = bytes;
            _pin = pin;
            Handle = handle;
            _cmapOffset = cmapOffset;
            _cmapFormat = cmapFormat;
        }

        public static LoadedFont Load(nint document, PdfTextFont font)
        {
            byte[]? bytes = null;
            GCHandle pin = default;
            nint handle = nint.Zero;
            try
            {
                var fontFile = font == PdfTextFont.SimHei ? "simhei.ttf" : "arial.ttf";
                var fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                using (var input = new FileStream(Path.Combine(fontsFolder, fontFile), FileMode.Open, FileAccess.Read,
                           FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                {
                    if (input.Length <= 0 || input.Length > MAX_FONT_BYTES)
                        throw new PdfToolException("本机字体大小无效，无法嵌入文字。");
                    bytes = new byte[(int)input.Length];
                    input.ReadExactly(bytes);
                }
                var (cmapOffset, cmapFormat) = FindUnicodeCmap(bytes);
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                handle = PdfiumNative.FPDFText_LoadFont(document, pin.AddrOfPinnedObject(), (uint)bytes.Length, 2, 1);
                RequireHandle(handle, "无法加载本机完整字体。");
                return new LoadedFont(bytes, pin, handle, cmapOffset, cmapFormat);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (handle != nint.Zero) PdfiumNative.FPDFFont_Close(handle);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                if (pin.IsAllocated) pin.Free();
                throw new PdfToolException("本机缺少可用的黑体或 Arial 字体，请在 Windows 中安装对应字体后重试。");
            }
            catch
            {
                if (handle != nint.Zero) PdfiumNative.FPDFFont_Close(handle);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                if (pin.IsAllocated) pin.Free();
                throw;
            }
        }

        public bool HasGlyph(char character) => GlyphIndex(character) != 0;

        private uint GlyphIndex(char character)
        {
            var bytes = _bytes.AsSpan();
            if (_cmapFormat == 12)
            {
                var groups = checked((int)ReadUInt32(bytes, _cmapOffset + 12));
                var lo = 0;
                var hi = groups - 1;
                while (lo <= hi)
                {
                    var mid = lo + (hi - lo) / 2;
                    var group = _cmapOffset + 16 + mid * 12;
                    var start = ReadUInt32(bytes, group);
                    var end = ReadUInt32(bytes, group + 4);
                    if (character < start) hi = mid - 1;
                    else if (character > end) lo = mid + 1;
                    else return ReadUInt32(bytes, group + 8) + character - start;
                }
                return 0;
            }
            var segments = ReadUInt16(bytes, _cmapOffset + 6) / 2;
            var endCodes = _cmapOffset + 14;
            var startCodes = endCodes + segments * 2 + 2;
            var deltas = startCodes + segments * 2;
            var rangeOffsets = deltas + segments * 2;
            for (var segment = 0; segment < segments; segment++)
            {
                if (character > ReadUInt16(bytes, endCodes + segment * 2)) continue;
                var start = ReadUInt16(bytes, startCodes + segment * 2);
                if (character < start) return 0;
                var delta = ReadUInt16(bytes, deltas + segment * 2);
                var rangeAddress = rangeOffsets + segment * 2;
                var range = ReadUInt16(bytes, rangeAddress);
                if (range == 0) return (uint)((character + delta) % 65536);
                var glyph = ReadUInt16(bytes, rangeAddress + range + (character - start) * 2);
                return glyph == 0 ? 0 : (uint)((glyph + delta) % 65536);
            }
            return 0;
        }

        public double AdvanceWidth(char character, double size)
        {
            int Table(uint tag)
            {
                var tables = ReadUInt16(_bytes, 4);
                for (var index = 0; index < tables; index++)
                {
                    var record = 12 + index * 16;
                    if (ReadUInt32(_bytes, record) == tag) return checked((int)ReadUInt32(_bytes, record + 8));
                }
                throw new PdfToolException("本机字体缺少排版度量信息。");
            }
            var units = ReadUInt16(_bytes, Table(0x68656164) + 18);
            var metrics = ReadUInt16(_bytes, Table(0x68686561) + 34);
            if (units == 0 || metrics == 0) throw new PdfToolException("本机字体度量无效。");
            var glyph = GlyphIndex(character);
            var width = ReadUInt16(_bytes, checked(Table(0x686D7478) + (int)Math.Min(glyph, (uint)metrics - 1) * 4));
            return width * size / units;
        }

        public void CloseHandle()
        {
            if (Handle == nint.Zero) return;
            PdfiumNative.FPDFFont_Close(Handle);
            Handle = nint.Zero;
        }

        public void ReleaseBytes()
        {
            CryptographicOperations.ZeroMemory(_bytes);
            if (_pin.IsAllocated) _pin.Free();
            _bytes = [];
        }

        private static (int Offset, int Format) FindUnicodeCmap(ReadOnlySpan<byte> bytes)
        {
            var tables = ReadUInt16(bytes, 4);
            var cmap = -1;
            for (var table = 0; table < tables; table++)
            {
                var record = 12 + table * 16;
                if (ReadUInt32(bytes, record) == 0x636D6170)
                {
                    cmap = checked((int)ReadUInt32(bytes, record + 8));
                    break;
                }
            }
            if (cmap < 0) throw new PdfToolException("本机字体缺少 Unicode 字符映射。");
            var count = ReadUInt16(bytes, cmap + 2);
            var bestOffset = -1;
            var bestFormat = 0;
            var bestScore = 0;
            for (var entry = 0; entry < count; entry++)
            {
                var record = cmap + 4 + entry * 8;
                var platform = ReadUInt16(bytes, record);
                var encoding = ReadUInt16(bytes, record + 2);
                if (platform != 0 && !(platform == 3 && encoding is 1 or 10)) continue;
                var subtable = checked(cmap + (int)ReadUInt32(bytes, record + 4));
                var format = ReadUInt16(bytes, subtable);
                if (format is not (4 or 12)) continue;
                var score = (format == 12 ? 10 : 1) + (platform == 3 ? 2 : 0);
                if (score <= bestScore) continue;
                bestOffset = subtable;
                bestFormat = format;
                bestScore = score;
            }
            if (bestOffset < 0) throw new PdfToolException("本机字体的字符映射格式不受支持。");
            return (bestOffset, bestFormat);
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 2) throw new PdfToolException("本机字体结构无效。");
            return BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) throw new PdfToolException("本机字体结构无效。");
            return BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
        }
    }
}

/// <summary>PDFium 151 Windows x64 ABI：C 的 unsigned long 仍为 32 位，size_t 为指针宽度。</summary>
internal static class PdfiumNative
{
    private const string LIBRARY_NAME = "pdfium.dll";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Matrix(float a, float b, float c, float d, float e, float f)
    {
        internal float A = a;
        internal float B = b;
        internal float C = c;
        internal float D = d;
        internal float E = e;
        internal float F = f;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int WriteBlockDelegate(nint writer, nint data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileWrite
    {
        internal int Version;
        internal nint WriteBlock;
    }

    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDF_InitLibrary();
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDF_LoadMemDocument64(nint data, nuint size, nint password);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDF_CloseDocument(nint document);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_GetPageCount(nint document);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFPage_New(nint document, int index, double width, double height);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDF_ClosePage(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern float FPDF_GetPageWidthF(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern float FPDF_GetPageHeightF(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_GetSecurityHandlerRevision(nint document);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_GetSignatureCount(nint document);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_GetFormType(nint document);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotation, int flags);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDFBitmap_Destroy(nint bitmap);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotation, double pageX, double pageY, out int x, out int y);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotation, int x, int y, out double pageX, out double pageY);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPage_CountObjects(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFPage_GetObject(nint page, int index);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_GetType(nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_HasTransparency(nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_GetBounds(nint item, out float left, out float bottom, out float right, out float top);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_GetMatrix(nint item, out Matrix matrix);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_SetMatrix(nint item, ref Matrix matrix);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFPageObj_GetClipPath(nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFClipPath_CountPaths(nint clipPath);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPage_InsertObject(nint page, nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPage_InsertObjectAtIndex(nint page, nint item, nuint index);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPage_RemoveObject(nint page, nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDFPageObj_Destroy(nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPage_GenerateContent(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFText_LoadPage(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDFText_ClosePage(nint page);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFText_CountChars(nint textPage);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFText_GetText(nint textPage, int startIndex, int count, nint buffer);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern uint FPDFTextObj_GetText(nint item, nint textPage, nint buffer, uint length);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFTextObj_GetFontSize(nint item, out float size);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFTextObj_GetTextRenderMode(nint item);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFTextObj_SetTextRenderMode(nint item, int mode);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFText_SetText(nint item, nint text);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFText_LoadFont(nint document, nint bytes, uint size, int fontType, int cid);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDFFont_Close(nint font);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFPageObj_CreateTextObj(nint document, nint font, float size);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFPageObj_NewImageObj(nint document);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFImageObj_SetBitmap(nint pages, int pageCount, nint item, nint bitmap);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern nint FPDFPageObj_CreateNewRect(float x, float y, float width, float height);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_GetFillColor(nint item, out uint r, out uint g, out uint b, out uint a);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_SetFillColor(nint item, uint r, uint g, uint b, uint a);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_GetStrokeColor(nint item, out uint r, out uint g, out uint b, out uint a);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_SetStrokeColor(nint item, uint r, uint g, uint b, uint a);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_GetStrokeWidth(nint item, out float width);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPageObj_SetStrokeWidth(nint item, float width);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void FPDFPageObj_SetBlendMode(nint item, [MarshalAs(UnmanagedType.LPStr)] string mode);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDFPath_SetDrawMode(nint item, int fillMode, int stroke);
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int FPDF_SaveAsCopy(nint document, ref FileWrite writer, uint flags);
}
