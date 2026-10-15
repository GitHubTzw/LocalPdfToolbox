using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.DesktopV2;

/// <summary>
/// 本机 PNG 编码器：支持 8 位真彩（RGB / RGBA）和索引色两种模式，
/// 逐行自适应滤波后用 zlib 最高压缩级别打包。
/// 全部是托管代码，不调用外部程序、不访问网络、不写入任何中间文件。
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>把 BGRA 像素写成真彩 PNG；withAlpha 为 false 时丢掉 Alpha 通道，体积更小。</summary>
    public static byte[] EncodeTruecolor(ReadOnlySpan<byte> bgra, int width, int height, bool withAlpha)
    {
        RequireSize(width, height, bgra.Length, 4);
        var channels = withAlpha ? 4 : 3;
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        var previous = new byte[stride];
        var current = new byte[stride];
        var candidate = new byte[stride];
        var best = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var target = x * channels;
                current[target] = bgra[source + 2];
                current[target + 1] = bgra[source + 1];
                current[target + 2] = bgra[source];
                if (withAlpha) current[target + 3] = bgra[source + 3];
                source += 4;
            }
            var offset = y * (stride + 1);
            raw[offset] = (byte)ChooseFilter(current, previous, channels, candidate, best);
            best.CopyTo(raw.AsSpan(offset + 1));
            current.CopyTo(previous);
        }
        return Assemble(width, height, 8, withAlpha ? (byte)6 : (byte)2, raw, null, null);
    }

    /// <summary>把索引和调色板写成索引色 PNG；bitDepth 取 1、2、4 或 8。</summary>
    public static byte[] EncodeIndexed(ReadOnlySpan<byte> indices, int width, int height, int bitDepth,
        byte[] paletteRgb, byte[]? paletteAlpha)
    {
        RequireSize(width, height, indices.Length, 1);
        if (bitDepth is not (1 or 2 or 4 or 8)) throw new PdfToolException("索引色位深只能是 1、2、4 或 8。");
        var rowBytes = (width * bitDepth + 7) / 8;
        var raw = new byte[(rowBytes + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var target = y * (rowBytes + 1);
            raw[target] = 0;
            for (var x = 0; x < width; x++)
            {
                var index = indices[y * width + x];
                var bit = x * bitDepth;
                raw[target + 1 + (bit >> 3)] |= (byte)(index << (8 - bitDepth - (bit & 7)));
            }
        }
        return Assemble(width, height, (byte)bitDepth, 3, raw, paletteRgb, paletteAlpha);
    }

    private static byte[] Assemble(int width, int height, byte bitDepth, byte colorType, byte[] raw,
        byte[]? paletteRgb, byte[]? paletteAlpha)
    {
        using var output = new MemoryStream(raw.Length / 2 + 1024);
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR", header);
        if (paletteRgb is not null)
        {
            WriteChunk(output, "PLTE", paletteRgb);
            if (paletteAlpha is not null)
            {
                // tRNS 只写到最后一个非全不透明项，尾部的不透明项可以省略。
                var last = paletteAlpha.Length - 1;
                while (last >= 0 && paletteAlpha[last] == 255) last--;
                if (last >= 0) WriteChunk(output, "tRNS", paletteAlpha.AsSpan(0, last + 1));
            }
        }
        using (var compressed = new MemoryStream(raw.Length / 3 + 256))
        {
            using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(raw);
            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>逐行挑选滤波方式：取滤波后字节绝对值之和最小的那一种，和 libpng 的启发式一致。</summary>
    private static int ChooseFilter(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp,
        Span<byte> candidate, Span<byte> best)
    {
        var bestCost = long.MaxValue;
        var bestFilter = 0;
        for (var filter = 0; filter <= 4; filter++)
        {
            var cost = 0L;
            for (var index = 0; index < current.Length; index++)
            {
                var left = index >= bpp ? current[index - bpp] : 0;
                var up = previous[index];
                var upLeft = index >= bpp ? previous[index - bpp] : 0;
                var value = filter switch
                {
                    1 => current[index] - left,
                    2 => current[index] - up,
                    3 => current[index] - ((left + up) >> 1),
                    4 => current[index] - Paeth(left, up, upLeft),
                    _ => current[index]
                };
                candidate[index] = (byte)value;
                cost += Math.Abs((int)(sbyte)value);
            }
            if (cost >= bestCost) continue;
            bestCost = cost;
            bestFilter = filter;
            candidate.CopyTo(best);
        }
        return bestFilter;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        Span<byte> name = stackalloc byte[4];
        for (var index = 0; index < 4; index++) name[index] = (byte)type[index];
        output.Write(name);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(name, data));
        output.Write(crc);
    }

    private static void RequireSize(int width, int height, int length, int bytesPerPixel)
    {
        if (width < 1 || height < 1) throw new PdfToolException("图片尺寸无效，无法编码。");
        if ((long)length < (long)width * height * bytesPerPixel) throw new PdfToolException("像素数据不完整，无法编码。");
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < 256; value++)
        {
            var current = value;
            for (var bit = 0; bit < 8; bit++)
                current = (current & 1) != 0 ? 0xEDB88320u ^ (current >> 1) : current >> 1;
            table[value] = current;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in second) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
