using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRTVUP.Unify.LocalPdf.Desktop;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 单线程缩略图队列：只为当前可见的页面渲染小图，结果缓存复用，
/// 快速滚动时跳过已经离开视口的请求，避免占用过多内存和 CPU。
/// </summary>
internal sealed class ThumbnailQueue
{
    private const int MaxCached = 1200;
    private readonly PdfPreviewRenderer _renderer;
    private readonly Dispatcher _dispatcher;
    private readonly Channel<PageItemV2> _channel =
        Channel.CreateUnbounded<PageItemV2>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private bool _disposed;

    public ThumbnailQueue(PdfPreviewRenderer renderer, Dispatcher dispatcher)
    {
        _renderer = renderer;
        _dispatcher = dispatcher;
        _ = Task.Run(ProcessAsync);
    }

    public void Request(PageItemV2 item)
    {
        if (_disposed) return;
        item.IsThumbnailVisible = true;
        if (item.Thumbnail is not null) return;
        var key = item.ThumbnailKey;
        lock (_cache)
            if (_cache.TryGetValue(key, out var cached)) { item.Thumbnail = cached; return; }
        if (_pending.TryAdd(key, 0)) _channel.Writer.TryWrite(item);
    }

    public void Release(PageItemV2 item) => item.IsThumbnailVisible = false;

    public void InvalidateSource(string sourceId)
    {
        var prefix = sourceId + ":";
        lock (_cache)
        {
            foreach (var key in _cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _cache.Remove(key);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_cancellation.Token))
            {
                _pending.TryRemove(item.ThumbnailKey, out _);
                if (!item.IsThumbnailVisible) continue;
                var key = item.ThumbnailKey;
                BitmapSource? cached;
                lock (_cache) _cache.TryGetValue(key, out cached);
                if (cached is not null) { Assign(item, key, cached); continue; }
                try
                {
                    var image = _renderer.Render(item.SourceId, item.PageNumber, item.Rotation, 96, 124);
                    lock (_cache)
                    {
                        if (_cache.Count > MaxCached) _cache.Clear();
                        _cache[key] = image;
                    }
                    Assign(item, key, image);
                }
                catch (Exception) { /* 单页缩略图失败不影响其他页面。 */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Assign(PageItemV2 item, string key, BitmapSource image) =>
        _dispatcher.InvokeAsync(() => { if (item.ThumbnailKey == key) item.Thumbnail = image; });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        _cancellation.Cancel();
        lock (_cache) _cache.Clear();
        _pending.Clear();
    }
}
