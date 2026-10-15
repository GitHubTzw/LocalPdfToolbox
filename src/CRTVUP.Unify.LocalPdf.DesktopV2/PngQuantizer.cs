namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 把真彩像素量化成不超过 256 色的调色板（中位切分法），用于生成索引色 PNG。
/// 纯托管实现，不联网、不写临时文件。截图、图标、图表这类颜色较少的图收益最大。
/// </summary>
internal static class PngQuantizer
{
    /// <summary>统计用的颜色桶：每通道取高 5 位，最多 32768 个桶。</summary>
    private const int BucketBits = 5;
    private const int BucketShift = 8 - BucketBits;

    /// <summary>量化结果：调色板（RGB 三元组）与每个像素的调色板下标。</summary>
    public sealed record Result(byte[] PaletteRgb, byte[] Indices, int ColorCount);

    /// <summary>
    /// 把 BGRA 像素量化到最多 maxColors 种颜色。withAlpha 为 true 时保留 Alpha 通道。
    /// 颜色种类本身不超过上限时直接复用原色，不引入任何失真。
    /// </summary>
    public static Result Quantize(ReadOnlySpan<byte> bgra, int width, int height, int maxColors, bool withAlpha)
    {
        var pixels = width * height;
        var limit = Math.Clamp(maxColors, 2, 256);

        // 直方图：桶号 -> 像素数；同时记录桶内的通道累加值，用于算平均色。
        var counts = new int[1 << (BucketBits * 3)];
        var sumR = new long[counts.Length];
        var sumG = new long[counts.Length];
        var sumB = new long[counts.Length];
        var sumA = new long[counts.Length];
        for (var index = 0; index < pixels; index++)
        {
            var offset = index * 4;
            var b = bgra[offset];
            var g = bgra[offset + 1];
            var r = bgra[offset + 2];
            var a = bgra[offset + 3];
            var bucket = ((r >> BucketShift) << (BucketBits * 2)) | ((g >> BucketShift) << BucketBits) | (b >> BucketShift);
            counts[bucket]++;
            sumR[bucket] += r;
            sumG[bucket] += g;
            sumB[bucket] += b;
            sumA[bucket] += a;
        }

        var used = new List<int>();
        for (var bucket = 0; bucket < counts.Length; bucket++)
            if (counts[bucket] > 0) used.Add(bucket);
        if (used.Count == 0) return new Result([0, 0, 0], new byte[pixels], 1);

        var palette = new List<(byte R, byte G, byte B, byte A)>(Math.Min(used.Count, limit));
        if (used.Count <= limit)
        {
            // 颜色本来就不多：直接用每个桶的平均色，零失真。
            foreach (var bucket in used)
            {
                var count = counts[bucket];
                palette.Add(((byte)(sumR[bucket] / count), (byte)(sumG[bucket] / count),
                    (byte)(sumB[bucket] / count), (byte)(sumA[bucket] / count)));
            }
        }
        else
        {
            palette.AddRange(MedianCut(used, counts, sumR, sumG, sumB, sumA, limit));
        }

        // 用 5 位查找表把像素映射到最近的调色板项，避免逐像素全表扫描。
        var lookup = new byte[1 << (BucketBits * 3)];
        var assigned = new bool[lookup.Length];
        for (var index = 0; index < pixels; index++)
        {
            var offset = index * 4;
            var bucket = ((bgra[offset + 2] >> BucketShift) << (BucketBits * 2))
                | ((bgra[offset + 1] >> BucketShift) << BucketBits) | (bgra[offset] >> BucketShift);
            if (!assigned[bucket])
            {
                lookup[bucket] = (byte)Nearest(palette, bgra[offset + 2], bgra[offset + 1], bgra[offset]);
                assigned[bucket] = true;
            }
        }

        var indices = new byte[pixels];
        for (var index = 0; index < pixels; index++)
        {
            var offset = index * 4;
            var bucket = ((bgra[offset + 2] >> BucketShift) << (BucketBits * 2))
                | ((bgra[offset + 1] >> BucketShift) << BucketBits) | (bgra[offset] >> BucketShift);
            indices[index] = lookup[bucket];
        }

        var rgb = new byte[palette.Count * 3];
        for (var index = 0; index < palette.Count; index++)
        {
            rgb[index * 3] = palette[index].R;
            rgb[index * 3 + 1] = palette[index].G;
            rgb[index * 3 + 2] = palette[index].B;
        }
        return new Result(rgb, indices, palette.Count);
    }

    /// <summary>中位切分：每次挑出跨度最大的盒子，沿最长通道按像素数中位切开，直到达到颜色上限。</summary>
    private static List<(byte R, byte G, byte B, byte A)> MedianCut(List<int> used, int[] counts,
        long[] sumR, long[] sumG, long[] sumB, long[] sumA, int limit)
    {
        var boxes = new List<List<int>> { used };
        while (boxes.Count < limit)
        {
            var target = -1;
            var widest = -1;
            var axis = 0;
            for (var index = 0; index < boxes.Count; index++)
            {
                if (boxes[index].Count < 2) continue;
                var (span, longest) = Span(boxes[index]);
                if (span <= widest) continue;
                widest = span;
                target = index;
                axis = longest;
            }
            if (target < 0) break;
            var box = boxes[target];
            box.Sort((left, right) => Channel(left, axis).CompareTo(Channel(right, axis)));
            var half = Total(box, counts) / 2;
            var running = 0;
            var split = 1;
            for (var index = 0; index < box.Count - 1; index++)
            {
                running += counts[box[index]];
                if (running >= half) { split = index + 1; break; }
                split = index + 2;
            }
            split = Math.Clamp(split, 1, box.Count - 1);
            var tail = box.GetRange(split, box.Count - split);
            box.RemoveRange(split, box.Count - split);
            boxes.Add(tail);
        }

        var palette = new List<(byte R, byte G, byte B, byte A)>(boxes.Count);
        foreach (var box in boxes)
        {
            long r = 0, g = 0, b = 0, a = 0;
            var total = 0L;
            foreach (var bucket in box)
            {
                r += sumR[bucket];
                g += sumG[bucket];
                b += sumB[bucket];
                a += sumA[bucket];
                total += counts[bucket];
            }
            if (total == 0) total = 1;
            palette.Add(((byte)(r / total), (byte)(g / total), (byte)(b / total), (byte)(a / total)));
        }
        return palette;

        static long Total(List<int> box, int[] counts)
        {
            long total = 0;
            foreach (var bucket in box) total += counts[bucket];
            return total;
        }

        static int Channel(int bucket, int axis) => axis switch
        {
            0 => (bucket >> (BucketBits * 2)) & ((1 << BucketBits) - 1),
            1 => (bucket >> BucketBits) & ((1 << BucketBits) - 1),
            _ => bucket & ((1 << BucketBits) - 1)
        };

        static (int Span, int Axis) Span(List<int> box)
        {
            var min = new int[3] { int.MaxValue, int.MaxValue, int.MaxValue };
            var max = new int[3];
            foreach (var bucket in box)
                for (var axis = 0; axis < 3; axis++)
                {
                    var value = Channel(bucket, axis);
                    if (value < min[axis]) min[axis] = value;
                    if (value > max[axis]) max[axis] = value;
                }
            var spans = new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] };
            var longest = spans[0] >= spans[1] && spans[0] >= spans[2] ? 0 : spans[1] >= spans[2] ? 1 : 2;
            return (spans[longest], longest);
        }
    }

    /// <summary>在调色板里找最接近的颜色；带 Alpha 时把透明度差异也算进距离。</summary>
    private static int Nearest(List<(byte R, byte G, byte B, byte A)> palette, int r, int g, int b)
    {
        var best = 0;
        var bestDistance = long.MaxValue;
        for (var index = 0; index < palette.Count; index++)
        {
            var entry = palette[index];
            long dr = entry.R - r;
            long dg = entry.G - g;
            long db = entry.B - b;
            var distance = dr * dr + dg * dg + db * db;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }
}
