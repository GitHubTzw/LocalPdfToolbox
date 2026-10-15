using System.IO;
using System.Security.Cryptography;
using System.Text;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;

/// <summary>
/// V2 扩充功能的本地检查：结构化互转、多语言模型生成、文本变形、单位换算、
/// 令牌与加密、目录对比和 PDF 附加操作。全部使用合成内容，不读取用户文档。
/// </summary>
internal static class ToolkitExtrasChecks
{
    public static void Run(string directory)
    {
        StructuredConversion();
        CodeModels();
        TextTransforms();
        Units();
        TokensAndCrypto();
        DirectoryCompare(directory);
        PdfExtras(directory);
        Console.WriteLine("通过：结构化互转、多语言模型、文本变形、单位换算、加密与目录/PDF 附加操作。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (PdfToolException) { return; }
        throw new InvalidOperationException(message);
    }

    // ------------------------------------------------------------ 结构化互转

    private static void StructuredConversion()
    {
        var tool = new StructuredTextConverter();
        const string source = "{\"name\":\"张三\",\"age\":30,\"tags\":[\"a\",\"b\"],\"address\":{\"city\":\"上海\"}}";

        var xml = tool.JsonToXml(source);
        Check(xml.Output.Contains("<name>张三</name>", StringComparison.Ordinal), "JSON 转 XML 未保留中文文本。");
        Check(xml.Output.Contains("<item>a</item>", StringComparison.Ordinal), "JSON 数组未转成 item 元素。");

        var back = tool.XmlToJson(xml.Output);
        Check(back.Output.Contains("\"name\": \"张三\"", StringComparison.Ordinal), "XML 转 JSON 未还原中文文本。");
        Check(back.Output.Contains("\"age\": 30", StringComparison.Ordinal), "XML 转 JSON 未还原数字类型。");

        var csv = tool.JsonToCsv("[{\"a\":1,\"b\":\"x,y\"},{\"a\":2,\"b\":\"z\"}]");
        Check(csv.Output.Contains("\"x,y\"", StringComparison.Ordinal), "CSV 未对含逗号的值加引号。");
        var fromCsv = tool.CsvToJson("a,b\r\n1,x\r\n2,\"z\"\"q\"\r\n");
        Check(fromCsv.Output.Contains("\"a\": \"1\"", StringComparison.Ordinal), "CSV 转 JSON 未生成对象数组。");
        Check(fromCsv.Output.Contains("z\\\"q", StringComparison.Ordinal), "CSV 转义引号未正确还原。");

        var query = tool.Query(source, "address.city");
        Check(query.Output.Contains("上海", StringComparison.Ordinal), "路径查询未取到嵌套值。");
        var indexed = tool.Query("[{\"id\":7},{\"id\":9}]", "[1].id");
        Check(indexed.Output.Contains("9", StringComparison.Ordinal), "路径查询未支持数组下标。");
        Throws(() => tool.Query(source, "address.missing"), "不存在的路径应当报错。");

        var described = tool.Describe(source);
        Check(described.Output.Contains("最大深度", StringComparison.Ordinal), "结构统计缺少深度信息。");
        Check(described.Output.Contains("字符串", StringComparison.Ordinal), "结构统计缺少类型分布。");

        var deduped = tool.DeduplicateArray("[\"a\",\"A\",\"b\",\"a\"]", ignoreCase: true);
        Check(deduped.Summary.Contains("已去掉 2 个重复项", StringComparison.Ordinal), "数组去重结果不正确。");
        Check(deduped.Output.Contains("\"a\"", StringComparison.Ordinal)
            && !deduped.Output.Contains("\"A\"", StringComparison.Ordinal), "数组去重未忽略大小写。");
        var sorted = tool.SortArray("[3,1,10,2]", descending: false);
        Check(sorted.Output.IndexOf("10", StringComparison.Ordinal) > sorted.Output.IndexOf("3", StringComparison.Ordinal),
            "数组排序未按数值大小。");
    }

    // ------------------------------------------------------------ 多语言模型

    private static void CodeModels()
    {
        var generator = new CodeModelGenerator();
        const string source = "{\"user_name\":\"a\",\"age\":3,\"tags\":[\"x\"],\"items\":[{\"id\":1}]}";
        var options = new CodeModelOptions { RootName = "User", Namespace = "Demo.Models" };

        var csharp = generator.Generate(source, CodeLanguage.CSharp, options with { ClassSuffix = "Model" });
        Check(csharp.Contains("namespace Demo.Models", StringComparison.Ordinal)
            && csharp.Contains("public class UserModel", StringComparison.Ordinal), "C# 模型生成不正确。");

        var typescript = generator.Generate(source, CodeLanguage.TypeScript, options);
        Check(typescript.Contains("export interface User", StringComparison.Ordinal)
            && typescript.Contains("userName: string;", StringComparison.Ordinal),
            "TypeScript 模型生成不正确：" + typescript.Replace(Environment.NewLine, " | ", StringComparison.Ordinal));
        Check(typescript.Contains("tags: string[];", StringComparison.Ordinal), "TypeScript 数组类型不正确。");
        Check(typescript.Contains("items: Items[];", StringComparison.Ordinal), "TypeScript 嵌套类型不正确。");

        var java = generator.Generate(source, CodeLanguage.Java, options);
        Check(java.Contains("package Demo.Models;", StringComparison.Ordinal)
            && java.Contains("public class User", StringComparison.Ordinal), "Java 模型生成不正确。");
        Check(java.Contains("private long age;", StringComparison.Ordinal), "Java 数字类型不正确。");
        Check(java.Contains("public long getAge()", StringComparison.Ordinal), "Java 访问器未生成。");

        var python = generator.Generate(source, CodeLanguage.Python, options);
        Check(python.Contains("@dataclass", StringComparison.Ordinal)
            && python.Contains("class User:", StringComparison.Ordinal), "Python 模型生成不正确。");
        Check(python.Contains("user_name: str", StringComparison.Ordinal), "Python 字段名未转成下划线风格。");
        Check(python.Contains("tags: List[str]", StringComparison.Ordinal), "Python 列表类型不正确。");

        var go = generator.Generate(source, CodeLanguage.Go, options);
        Check(go.Contains("type User struct {", StringComparison.Ordinal), "Go 结构体生成不正确。");
        Check(go.Contains("`json:\"user_name\"`", StringComparison.Ordinal), "Go 未生成 json 标签。");
        Check(go.Contains("Age int64", StringComparison.Ordinal), "Go 数字类型不正确。");

        var sql = generator.Generate(source, CodeLanguage.Sql, options);
        Check(sql.Contains("CREATE TABLE user (", StringComparison.Ordinal), "SQL 建表语句生成不正确。");
        Check(sql.Contains("user_name TEXT NOT NULL", StringComparison.Ordinal), "SQL 列定义不正确。");

        Throws(() => generator.Generate("not json", CodeLanguage.TypeScript, options), "非法 JSON 应当报错。");
    }

    // ------------------------------------------------------------ 文本变形

    private static void TextTransforms()
    {
        var tool = new TextTransformTool();
        Check(tool.ApplyNaming("user name", NamingStyle.CamelCase) == "userName", "camelCase 转换不正确。");
        Check(tool.ApplyNaming("user_name", NamingStyle.PascalCase) == "UserName", "PascalCase 转换不正确。");
        Check(tool.ApplyNaming("userName", NamingStyle.SnakeCase) == "user_name", "snake_case 未拆开驼峰。");
        Check(tool.ApplyNaming("user name", NamingStyle.KebabCase) == "user-name", "kebab-case 转换不正确。");
        Check(tool.ApplyNaming("user name", NamingStyle.ConstantCase) == "USER_NAME", "CONSTANT_CASE 转换不正确。");
        Check(tool.ApplyNaming("a\nb", NamingStyle.UpperCase) == "A\nB", "整体大写转换不正确。");

        var newline = Environment.NewLine;
        Check(tool.ApplyLineOperation("b\na\nc", LineOperation.SortAscending, false) == "a" + newline + "b" + newline + "c",
            "行排序不正确。");
        Check(tool.ApplyLineOperation("a\nA\nb", LineOperation.Deduplicate, true) == "a" + newline + "b", "行去重未忽略大小写。");
        Check(tool.ApplyLineOperation("a\n\nb", LineOperation.RemoveEmptyLines, false) == "a" + newline + "b", "删除空行不正确。");
        Check(tool.ApplyLineOperation("a\nb", LineOperation.NumberLines, false).StartsWith("1. a", StringComparison.Ordinal),
            "加行号不正确。");

        var stats = tool.Describe("abc\n中文");
        Check(stats.Contains("字符数：6", StringComparison.Ordinal), "文本统计字符数不正确。");
        Check(stats.Contains("中文字符数：2", StringComparison.Ordinal), "文本统计中文数不正确。");

        var regex = tool.TestRegex("a1 b22 c333", @"\d+", false, false, false, 10);
        Check(regex.Success && regex.Matches.Count == 3, "正则匹配数量不正确。");
        Check(regex.Matches[1].Value == "22", "正则匹配内容不正确。");
        var grouped = tool.TestRegex("2026-10-15", @"(\d{4})-(\d{2})-(\d{2})", false, false, false, 10);
        Check(grouped.Matches[0].Groups[0] == "2026", "正则捕获组未返回。");
        Throws(() => tool.TestRegex("x", "([", false, false, false, 10), "非法正则应当报错。");
    }

    // ------------------------------------------------------------ 单位换算

    private static void Units()
    {
        var tool = new UnitConverter();
        var hex = tool.ConvertBase("0xFF", 16, 2);
        Check(hex.Contains("11111111", StringComparison.Ordinal), "进制转换（16→2）不正确。");
        Check(tool.ConvertBase("255", 10, 16).Contains("FF", StringComparison.Ordinal), "进制转换（10→16）不正确。");
        Check(tool.ConvertBase("-10", 10, 2).Contains("-1010", StringComparison.Ordinal), "负数进制转换不正确。");
        Throws(() => tool.ConvertBase("9", 8, 10), "越界数字应当报错。");

        var color = tool.ConvertColor("#1E88E5");
        Check(color.Contains("rgb(30, 136, 229)", StringComparison.Ordinal), "颜色 HEX→RGB 不正确。");
        Check(color.Contains("hsl(208", StringComparison.Ordinal), "颜色 HEX→HSL 不正确。");
        Check(tool.ConvertColor("rgb(0,0,0)").Contains("#000000", StringComparison.Ordinal), "颜色 RGB→HEX 不正确。");
        Check(tool.ConvertColor("hsl(0, 100%, 50%)").Contains("#FF0000", StringComparison.Ordinal), "颜色 HSL→HEX 不正确。");

        var url = tool.DescribeUrl("https://example.com:8443/a/b?x=1&y=%E4%B8%AD#top");
        Check(url.Contains("主机：example.com", StringComparison.Ordinal), "URL 主机解析不正确。");
        Check(url.Contains("端口：8443", StringComparison.Ordinal), "URL 端口解析不正确。");
        Check(url.Contains("y = 中", StringComparison.Ordinal), "URL 查询参数未解码。");
        Throws(() => tool.DescribeUrl("not a url"), "非法 URL 应当报错。");

        var guids = tool.GenerateGuids(3, upperCase: true, braces: true, noHyphen: true).Split(Environment.NewLine);
        Check(guids.Length == 3, "GUID 生成数量不正确。");
        Check(guids[0].StartsWith('{') && guids[0].EndsWith('}') && guids[0].Length == 34, "GUID 格式选项未生效。");
        Check(!guids[0].Contains('-'), "无连字符选项未生效。");
    }

    // ------------------------------------------------------------ 令牌与加密

    private static void TokensAndCrypto()
    {
        var tool = new TokenCryptoTool();
        const string text = "本地工具箱 Base32 往返";
        var encoded = tool.Base32Encode(text, TextCharset.Utf8);
        Check(encoded.Length % 8 == 0, "Base32 输出未按 8 字符对齐。");
        Check(tool.Base32Decode(encoded, TextCharset.Utf8) == text, "Base32 往返未还原原文。");
        Throws(() => tool.Base32Decode("0189", TextCharset.Utf8), "非法 Base32 字符应当报错。");

        // 合成一个 JWT：头部与载荷是本地拼出来的 Base64URL，不涉及任何服务。
        var header = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url("{\"sub\":\"tester\",\"iat\":1760000000,\"exp\":1760003600}");
        var jwt = tool.DescribeJwt($"{header}.{payload}.signature");
        Check(jwt.Contains("\"alg\": \"HS256\"", StringComparison.Ordinal), "JWT 头部未解码。");
        Check(jwt.Contains("tester", StringComparison.Ordinal), "JWT 载荷未解码。");
        Check(jwt.Contains("过期时间", StringComparison.Ordinal), "JWT 时间字段未换算。");
        Check(jwt.Contains("不校验签名", StringComparison.Ordinal), "JWT 结果未说明不校验签名。");

        var hmac = tool.ComputeHmac("message", "secret", HashKind.Sha256, TextCharset.Utf8);
        Check(hmac.Contains("十六进制：", StringComparison.Ordinal) && hmac.Contains("Base64：", StringComparison.Ordinal),
            "HMAC 输出格式不正确。");
        Check(tool.ComputeHmac("message", "secret", HashKind.Sha256, TextCharset.Utf8) == hmac, "HMAC 结果不稳定。");
        Check(tool.ComputeHmac("message", "other", HashKind.Sha256, TextCharset.Utf8) != hmac, "不同密钥应当得到不同签名。");

        var cipher = tool.Encrypt("机密内容 secret", "口令123");
        Check(cipher.StartsWith("LPDF1:", StringComparison.Ordinal), "密文缺少版本前缀。");
        Check(tool.Decrypt(cipher, "口令123") == "机密内容 secret", "AES 往返未还原原文。");
        Throws(() => tool.Decrypt(cipher, "错误口令"), "错误口令应当解密失败。");
        var second = tool.Encrypt("机密内容 secret", "口令123");
        Check(second != cipher, "相同明文两次加密应当得到不同密文（随机盐与随机向量）。");
    }

    // ------------------------------------------------------------ 目录对比

    private static void DirectoryCompare(string directory)
    {
        var left = Path.Combine(directory, "目录对比-左");
        var right = Path.Combine(directory, "目录对比-右");
        Directory.CreateDirectory(Path.Combine(left, "sub"));
        Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(left, "same.txt"), "same");
        File.WriteAllText(Path.Combine(right, "same.txt"), "same");
        File.WriteAllText(Path.Combine(left, "changed.txt"), "before");
        File.WriteAllText(Path.Combine(right, "changed.txt"), "after-longer");
        File.WriteAllText(Path.Combine(left, "left-only.txt"), "x");
        File.WriteAllText(Path.Combine(right, "right-only.txt"), "y");
        File.WriteAllText(Path.Combine(left, "sub", "nested.txt"), "nested");

        var tool = new DirectoryDiffTool();
        var bySize = tool.Compare(left, right, compareContent: false);
        Check(bySize.Identical == 1 && bySize.Modified == 1 && bySize.LeftOnly == 2 && bySize.RightOnly == 1,
            $"目录对比统计不正确：相同 {bySize.Identical}，修改 {bySize.Modified}，仅左 {bySize.LeftOnly}，仅右 {bySize.RightOnly}。");
        Check(bySize.Entries.Any(entry => entry.RelativePath == Path.Combine("sub", "nested.txt")),
            "目录对比未递归子文件夹。");

        var byContent = tool.Compare(left, right, compareContent: true);
        Check(byContent.Modified == 1, "按内容比较时修改数不正确。");
        Throws(() => tool.Compare(left, left, compareContent: false), "选择同一个文件夹应当报错。");
    }

    // ------------------------------------------------------------ PDF 附加操作

    private static void PdfExtras(string directory)
    {
        var pdf = TextPdfBuilder.Build(["V2 EXTRAS PAGE ONE", "V2 EXTRAS PAGE TWO", "V2 EXTRAS PAGE THREE"]);
        var originalHash = Convert.ToHexString(SHA256.HashData(pdf));
        var tool = new PdfExtrasTool();

        var info = tool.Inspect(pdf);
        Check(info.PageCount == 3 && info.ByteCount == pdf.Length, "PDF 信息页数或大小不正确。");
        Check(info.Pages.Count == 3 && info.Pages[0].Width > 0, "PDF 信息缺少每页尺寸。");

        var text = tool.ExtractText(pdf);
        Check(text.Contains("V2 EXTRAS PAGE TWO", StringComparison.Ordinal), "PDF 文字提取缺少内容。");
        Check(text.Contains("第 3 页", StringComparison.Ordinal), "PDF 文字提取缺少分页标记。");

        var output = Path.Combine(directory, "PDF拆分");
        Directory.CreateDirectory(output);
        var every = tool.SplitEvery(pdf, "合成文档.pdf", output, 2);
        Check(every.Count == 2, "按每 2 页拆分应得到 2 个文件。");
        foreach (var path in every) Check(File.Exists(path), "拆分文件未生成。");
        using (var first = PdfiumRuntime.Open(File.ReadAllBytes(every[0])))
            Check(first.PageCount == 2 && first.ExtractText(1).Contains("PAGE ONE", StringComparison.Ordinal),
                "拆分后的第一个文件页数或内容不正确。");
        using (var second = PdfiumRuntime.Open(File.ReadAllBytes(every[1])))
            Check(second.PageCount == 1 && second.ExtractText(1).Contains("PAGE THREE", StringComparison.Ordinal),
                "拆分后的第二个文件页数或内容不正确。");

        var grouped = tool.SplitByGroups(pdf, "合成文档.pdf", output, "1;2-3");
        Check(grouped.Count == 2, "按页码范围拆分应得到 2 个文件。");
        using (var tail = PdfiumRuntime.Open(File.ReadAllBytes(grouped[1])))
            Check(tail.PageCount == 2, "按页码范围拆分页数不正确。");
        Throws(() => tool.SplitByGroups(pdf, "合成文档.pdf", output, "0-1"), "越界页码应当报错。");
        Throws(() => tool.SplitByGroups(pdf, "合成文档.pdf", output, "3-1"), "倒序页码范围应当报错。");

        // 源内容必须保持原样：这些操作只读内存里的副本，不修改调用方传入的字节。
        Check(Convert.ToHexString(SHA256.HashData(pdf)) == originalHash, "PDF 附加操作改动了输入内容。");
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
