namespace CRTVUP.Unify.LocalPdf.Abstractions;

// ---------------------------------------------------------------------------
// 结构化文本互转：JSON / XML / CSV 之间转换、路径查询、结构统计、数组整理。
// 全部在本机内存中完成；XML 解析禁止 DTD 与外部实体，不访问网络。
// ---------------------------------------------------------------------------

public sealed record ConversionText(string Output, string Summary);

public interface IStructuredTextConverter
{
    ConversionText JsonToXml(string json);
    ConversionText XmlToJson(string xml);
    ConversionText JsonToCsv(string json);
    ConversionText CsvToJson(string csv);
    /// <summary>按点号与下标取值，例如 data.items[0].name；支持 * 通配对象属性。</summary>
    ConversionText Query(string json, string path);
    ConversionText Describe(string json);
    ConversionText DeduplicateArray(string json, bool ignoreCase);
    ConversionText SortArray(string json, bool descending);
}

// ---------------------------------------------------------------------------
// 多语言模型生成：把同一份 JSON 转成 C# / TypeScript / Java / Python / Go / SQL。
// 只生成文本，不调用任何外部编译器或解释器。
// ---------------------------------------------------------------------------

public enum CodeLanguage { CSharp, TypeScript, Java, Python, Go, Sql }

public sealed record CodeModelOptions
{
    /// <summary>根类型名（不含后缀）。</summary>
    public string RootName { get; init; } = "Root";

    /// <summary>命名空间或包名；留空表示不生成。</summary>
    public string Namespace { get; init; } = "";

    /// <summary>类型名后缀，例如 Model；留空表示不加。</summary>
    public string ClassSuffix { get; init; } = "";

    /// <summary>字符串属性的最大长度；0 表示不生成长度校验。</summary>
    public int StringLength { get; init; }
}

public interface ICodeModelGenerator
{
    string Generate(string json, CodeLanguage language, CodeModelOptions options);
}

// ---------------------------------------------------------------------------
// 文本变形：命名风格、大小写、行处理、文本统计与正则测试。
// ---------------------------------------------------------------------------

public enum NamingStyle { CamelCase, PascalCase, SnakeCase, KebabCase, ConstantCase, UpperCase, LowerCase, TitleCase }

public enum LineOperation { SortAscending, SortDescending, Deduplicate, Reverse, RemoveEmptyLines, TrimLines, NumberLines }

public sealed record RegexMatchInfo(int Index, int Length, string Value, IReadOnlyList<string> Groups);

public sealed record RegexResult(bool Success, string Summary, IReadOnlyList<RegexMatchInfo> Matches, bool TimedOut);

public interface ITextTransformTool
{
    string ApplyNaming(string input, NamingStyle style);
    string ApplyLineOperation(string input, LineOperation operation, bool ignoreCase);
    /// <summary>统计字符、字节、行、词与中文数量；只返回数字，不保留原文。</summary>
    string Describe(string input);
    RegexResult TestRegex(string input, string pattern, bool ignoreCase, bool multiline, bool singleline, int maxMatches);
}

// ---------------------------------------------------------------------------
// 单位与格式换算：进制、颜色、URL 拆解、GUID 生成。
// ---------------------------------------------------------------------------

public interface IUnitConverter
{
    /// <summary>进制互转，支持 2 / 8 / 10 / 16，允许 0x、0b、下划线分隔。</summary>
    string ConvertBase(string input, int fromBase, int toBase);
    /// <summary>颜色互转，输入 HEX / RGB / HSL 之一，输出三种写法。</summary>
    string ConvertColor(string input);
    /// <summary>拆解 URL 的各组成部分与查询参数；只做本地字符串分析，不发起请求。</summary>
    string DescribeUrl(string input);
    string GenerateGuids(int count, bool upperCase, bool braces, bool noHyphen);
}

// ---------------------------------------------------------------------------
// 令牌与对称加密：Base32、JWT 解析、HMAC、口令加解密。
// 加密只使用本机 .NET 实现，密钥不落盘；JWT 只解码不校验签名。
// ---------------------------------------------------------------------------

public interface ICryptoTextTool
{
    string Base32Encode(string input, TextCharset charset);
    string Base32Decode(string input, TextCharset charset);
    /// <summary>解码 JWT 的头部与载荷；不校验签名、不访问任何服务。</summary>
    string DescribeJwt(string token);
    string ComputeHmac(string input, string key, HashKind kind, TextCharset charset);
    /// <summary>口令派生出密钥后做 AES-GCM 加密，输出可复制的文本载荷。</summary>
    string Encrypt(string plainText, string password);
    string Decrypt(string cipherText, string password);
}

// ---------------------------------------------------------------------------
// 目录对比：递归比较两个本地文件夹，按大小或内容判断是否修改。
// ---------------------------------------------------------------------------

public enum DirectoryEntryState { Identical, Modified, LeftOnly, RightOnly }

public sealed record DirectoryDiffEntry(string RelativePath, DirectoryEntryState State, long LeftBytes, long RightBytes);

public sealed record DirectoryDiffResult(
    IReadOnlyList<DirectoryDiffEntry> Entries, int Identical, int Modified, int LeftOnly, int RightOnly, string Summary);

public interface IDirectoryDiffTool
{
    DirectoryDiffResult Compare(string leftDirectory, string rightDirectory, bool compareContent,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// PDF 附加操作：文档信息、文字提取、拆分。全部只读源文件，输出只新建文件。
// ---------------------------------------------------------------------------

public sealed record PdfDocumentInfo(int PageCount, long ByteCount, IReadOnlyList<PdfPageInfo> Pages, string Summary);

public interface IPdfExtrasTool
{
    /// <summary>读取内存中的 PDF 内容，返回页数与每页尺寸；不写文件。</summary>
    PdfDocumentInfo Inspect(ReadOnlyMemory<byte> content);
    /// <summary>按页导出文字层；没有文字层的页面会明确标注。</summary>
    string ExtractText(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    /// <summary>每 pagesPerFile 页导出为一个新 PDF，返回生成的文件路径。</summary>
    IReadOnlyList<string> SplitEvery(ReadOnlyMemory<byte> content, string displayName, string outputDirectory,
        int pagesPerFile, CancellationToken cancellationToken = default);
    /// <summary>按分组导出，groups 形如 1-3;4-6;7，每组一个新 PDF。</summary>
    IReadOnlyList<string> SplitByGroups(ReadOnlyMemory<byte> content, string displayName, string outputDirectory,
        string groups, CancellationToken cancellationToken = default);
}
