using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

namespace CRTVUP.Unify.LocalPdf.Desktop;

/// <summary>所有预览与编辑共用同一个本地 PDFium 生命周期及锁。</summary>
public sealed class PdfPreviewRenderer : IDisposable
{
    private readonly Dictionary<string, PdfiumDocument> _documents = [];
    private bool _disposed;

    public void Add(string id, ReadOnlyMemory<byte> content)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var replacement = PdfiumRuntime.Open(content);
            if (_documents.Remove(id, out var previous)) previous.Dispose();
            _documents.Add(id, replacement);
        }
    }

    public BitmapSource Render(string id, int pageNumber, int rotation, int maxWidth = 300, int maxHeight = 360)
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_documents.TryGetValue(id, out var document)) throw new PdfToolException("文档已关闭。");
            return ToBitmapSource(document.Render(pageNumber, rotation, maxWidth, maxHeight));
        }
    }

    public static BitmapSource ToBitmapSource(PdfBitmap pixels)
    {
        try
        {
            var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96,
                PixelFormats.Bgra32, null, pixels.Bgra, pixels.Stride);
            image.Freeze();
            return image;
        }
        finally { CryptographicOperations.ZeroMemory(pixels.Bgra); }
    }

    public void Remove(string id)
    {
        lock (PdfiumRuntime.SyncRoot)
            if (_documents.Remove(id, out var document)) document.Dispose();
    }

    public void Clear()
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            foreach (var document in _documents.Values) document.Dispose();
            _documents.Clear();
        }
    }

    public void Dispose()
    {
        lock (PdfiumRuntime.SyncRoot)
        {
            if (_disposed) return;
            Clear();
            _disposed = true;
        }
    }
}