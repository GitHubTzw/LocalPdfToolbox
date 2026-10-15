namespace CRTVUP.Unify.LocalPdf.Abstractions;

// ---------------------------------------------------------------------------
// 结构化文本（JSON / XML）：只在本机解析。XML 禁止 DTD 与外部实体，不访问网络。
// ---------------------------------------------------------------------------

public enum StructuredFormat { Json, Xml }

public sealed record FormatOptions
{
    public int IndentSize { get; init; } = 2;
    public bool UseTabs { get; init; }
    public bool SortKeys { get; init; }
}

public sealed record FormatResult(string Output, string Summary);

public sealed record ValidationResult(bool IsValid, string Message, int Line = 0, int Column = 0);

/// <summary>
/// 生成 C# 类时的命名与特性选项；默认值对齐 CRTVUP.BrightBook 项目的模型风格：
/// 块级命名空间、Model 后缀、逐属性注释。
/// </summary>
public sealed record CSharpClassOptions
{
    /// <summary>根类名（不含后缀），例如 Root。</summary>
    public string RootName { get; init; } = "Root";

    /// <summary>命名空间；留空表示不生成命名空间。</summary>
    public string Namespace { get; init; } = "CRTVUP.BrightBook.BrightBookService.Abstractions.Models";

    /// <summary>类名后缀，例如 Model；留空表示不加后缀。</summary>
    public string ClassSuffix { get; init; } = "Model";

    /// <summary>字符串属性的最大长度；0 表示不生成 StringLength 特性。</summary>
    public int StringLength { get; init; }
}

public interface IStructuredTextTool
{
    FormatResult Format(string input, StructuredFormat format, FormatOptions options);
    FormatResult Minify(string input, StructuredFormat format);
    ValidationResult Validate(string input, StructuredFormat format);
    string EscapeJsonString(string input);
    string UnescapeJsonString(string input);
    /// <summary>按 JSON 结构生成本地 C# 类文本；不写文件、不联网。</summary>
    string ToCSharpClasses(string json, CSharpClassOptions options);
}

// ---------------------------------------------------------------------------
// 编码 / 解码 / 哈希
// ---------------------------------------------------------------------------

public enum TextCodecOperation
{
    Base64Encode, Base64Decode,
    UrlEncode, UrlDecode,
    UnicodeEncode, UnicodeDecode,
    HtmlEncode, HtmlDecode,
    HexEncode, HexDecode
}

/// <summary>本地代码页；Gbk 由随 .NET 提供的代码页支持，不联网、不下载。</summary>
public enum TextCharset { Utf8, Utf16Le, Utf16Be, Ascii, Latin1, Gbk }

public interface ITextCodec
{
    string Apply(string input, TextCodecOperation operation, TextCharset charset = TextCharset.Utf8);
    byte[] Encode(string text, TextCharset charset);
    string Decode(ReadOnlyMemory<byte> bytes, TextCharset charset);
}

public enum HashKind { Md5, Sha1, Sha256, Sha512 }

public interface IHashTool
{
    string Compute(string input, HashKind kind, TextCharset charset = TextCharset.Utf8);
    /// <summary>分块读取本地文件计算摘要；不写日志、不保留文件内容。</summary>
    string ComputeFile(string path, HashKind kind, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// 文件对比
// ---------------------------------------------------------------------------

public enum DiffLineKind { Equal, Added, Removed, Changed }

public sealed record DiffOptions
{
    public bool IgnoreCase { get; init; }
    public bool IgnoreWhitespace { get; init; }
    public bool IgnoreBlankLines { get; init; }
    /// <summary>忽略每行末尾的空白字符。</summary>
    public bool IgnoreTrailingWhitespace { get; init; }
    /// <summary>忽略以 // 或 # 开头的注释行。</summary>
    public bool IgnoreCommentLines { get; init; }
}

/// <summary>一行对比结果；并排视图按行对齐，缺失的一侧为 null。</summary>
public sealed record DiffLine(int? LeftNumber, string? LeftText, int? RightNumber, string? RightText, DiffLineKind Kind);

public sealed record DiffResult(IReadOnlyList<DiffLine> Lines, int Added, int Removed, int Changed, bool Identical, string Summary);

/// <summary>行内差异的一个片段；Highlight 为真表示这一侧独有的内容。</summary>
public sealed record InlineSegment(string Text, bool Highlight);

public interface ITextDiffTool
{
    DiffResult Compare(string left, string right, DiffOptions options);
    /// <summary>把两行文本按词切分并标出各自独有的片段，用于并排视图的行内高亮。</summary>
    IReadOnlyList<InlineSegment> CompareInline(string left, string right);
}

public sealed record BinaryDiffResult(bool Identical, long LeftLength, long RightLength, long? FirstDifferenceOffset, string Summary);

public interface IBinaryDiffTool
{
    BinaryDiffResult Compare(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right);
}

// ---------------------------------------------------------------------------
// 压缩 / 解压
// ---------------------------------------------------------------------------

public enum ArchiveLevel { Fastest, Optimal, Smallest }

public sealed record ArchiveEntryInfo(string Path, long Length, long CompressedLength, bool IsDirectory);

public sealed record ArchiveResult(string OutputPath, int EntryCount, long ByteCount);

public interface IArchiveTool
{
    /// <summary>把本地文件或文件夹打包成新的 ZIP；不覆盖已有文件。</summary>
    ArchiveResult CreateZip(string outputPath, IReadOnlyList<string> paths, ArchiveLevel level,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    IReadOnlyList<ArchiveEntryInfo> ListZip(string zipPath);
    /// <summary>解压到新建的本地文件夹；拒绝越界条目和覆盖已有文件。</summary>
    ArchiveResult ExtractZip(string zipPath, string destinationDirectory,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    ArchiveResult CompressGzip(string outputPath, string inputPath, ArchiveLevel level,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    ArchiveResult ExtractGzip(string outputPath, string inputPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
