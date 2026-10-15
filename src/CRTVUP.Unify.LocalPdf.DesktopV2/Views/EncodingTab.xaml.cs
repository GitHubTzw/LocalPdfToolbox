using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.DesktopV2.Views;

/// <summary>
/// 编码与转换页签：Base64 / Base32 / URL / Unicode / HTML / 十六进制、哈希、HMAC、AES、
/// JWT 解析、GUID、进制、颜色、命名风格、正则、文本统计与时间戳，全部在本机完成。
/// </summary>
public partial class EncodingTab : UserControl
{
    private readonly ITextCodec _codec = new TextCodec();
    private readonly IHashTool _hashes = new HashTool();
    private readonly ICryptoTextTool _crypto = new TokenCryptoTool();
    private readonly IUnitConverter _units = new UnitConverter();
    private readonly ITextTransformTool _transforms = new TextTransformTool();
    private readonly DispatcherTimer _autoTimer;
    private CodecAction? _lastAction;

    private enum CodecAction
    {
        Base64Encode, Base64Decode, Base64UrlEncode, Base64UrlDecode,
        Base32Encode, Base32Decode,
        UrlEncode, UrlDecode, UnicodeEncode, UnicodeDecode, HtmlEncode, HtmlDecode, HexEncode, HexDecode,
        Hash, Hmac, AesEncrypt, AesDecrypt, Jwt, Guid, BaseConvert, Naming, TextStats, RegexTest,
        ColorConvert, UrlInspect, UnixToTime, TimeToUnix,
        SortLines, DeduplicateLines, ReverseLines, RemoveEmptyLines, NumberLines
    }

    /// <summary>一项转换方式；P1/P2/P3 是参数标签，为空表示不需要该参数。</summary>
    private sealed record CodecOption(string Label, CodecAction Action, string P1 = "", string P2 = "", string P3 = "")
    {
        public override string ToString() => Label;
    }

    public EncodingTab()
    {
        InitializeComponent();
        OperationList.ItemsSource = new CodecOption[]
        {
            new("Base64 编码", CodecAction.Base64Encode),
            new("Base64 解码", CodecAction.Base64Decode),
            new("Base64URL 编码", CodecAction.Base64UrlEncode),
            new("Base64URL 解码", CodecAction.Base64UrlDecode),
            new("Base32 编码", CodecAction.Base32Encode),
            new("Base32 解码", CodecAction.Base32Decode),
            new("URL 编码", CodecAction.UrlEncode),
            new("URL 解码", CodecAction.UrlDecode),
            new("中文 → Unicode", CodecAction.UnicodeEncode),
            new("Unicode → 中文", CodecAction.UnicodeDecode),
            new("HTML 转义", CodecAction.HtmlEncode),
            new("HTML 反转义", CodecAction.HtmlDecode),
            new("十六进制编码", CodecAction.HexEncode),
            new("十六进制解码", CodecAction.HexDecode),
            new("文本哈希", CodecAction.Hash),
            new("HMAC 签名", CodecAction.Hmac, "密钥"),
            new("AES 加密", CodecAction.AesEncrypt, "口令"),
            new("AES 解密", CodecAction.AesDecrypt, "口令"),
            new("JWT 解析", CodecAction.Jwt),
            new("GUID 生成", CodecAction.Guid, "数量", "选项（U 大写 / B 花括号 / N 无连字符）"),
            new("进制转换", CodecAction.BaseConvert, "源进制（2/8/10/16）", "目标进制"),
            new("命名风格转换", CodecAction.Naming, "风格（camel/pascal/snake/kebab/constant/upper/lower/title）"),
            new("颜色转换", CodecAction.ColorConvert),
            new("URL 拆解", CodecAction.UrlInspect),
            new("正则测试", CodecAction.RegexTest, "正则表达式", "选项（i 忽略大小写 / m 多行 / s 单行）"),
            new("文本统计", CodecAction.TextStats),
            new("时间戳 → 时间", CodecAction.UnixToTime),
            new("时间 → 时间戳", CodecAction.TimeToUnix),
            new("行排序", CodecAction.SortLines, "忽略大小写（1 是 / 0 否）"),
            new("行去重", CodecAction.DeduplicateLines, "忽略大小写（1 是 / 0 否）"),
            new("行反转", CodecAction.ReverseLines),
            new("删除空行", CodecAction.RemoveEmptyLines),
            new("加行号", CodecAction.NumberLines)
        };
        OperationList.SelectedIndex = 0;
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _autoTimer.Tick += (_, _) => { _autoTimer.Stop(); Apply(); };
        Loaded += (_, _) => SyncParameters();
    }

    private TextCharset Charset => CharsetChoice.SelectedIndex switch
    {
        1 => TextCharset.Gbk,
        2 => TextCharset.Utf16Le,
        3 => TextCharset.Utf16Be,
        4 => TextCharset.Ascii,
        5 => TextCharset.Latin1,
        _ => TextCharset.Utf8
    };

    private HashKind Hash => HashChoice.SelectedIndex switch
    {
        0 => HashKind.Md5,
        1 => HashKind.Sha1,
        3 => HashKind.Sha512,
        _ => HashKind.Sha256
    };

    // ---------------------------------------------------------------- 参数栏

    private void Operation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SyncParameters();
        Apply();
    }

    /// <summary>按当前转换方式显示需要的参数；切换方式时恢复该方式的默认值。</summary>
    private void SyncParameters()
    {
        if (OperationList.SelectedItem is not CodecOption option) return;
        var labels = new[] { option.P1, option.P2, option.P3 };
        var boxes = new[] { Param1Box, Param2Box, Param3Box };
        var captions = new[] { Param1Label, Param2Label, Param3Label };
        for (var index = 0; index < labels.Length; index++)
        {
            var visible = labels[index].Length > 0;
            captions[index].Text = labels[index];
            captions[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            boxes[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        ParamBar.Visibility = labels.Any(label => label.Length > 0) ? Visibility.Visible : Visibility.Collapsed;

        if (_lastAction == option.Action) return;
        _lastAction = option.Action;
        var defaults = Defaults(option.Action);
        if (defaults.Length > 0) Param1Box.Text = defaults[0];
        if (defaults.Length > 1) Param2Box.Text = defaults[1];
        if (defaults.Length > 2) Param3Box.Text = defaults[2];
    }

    private static string[] Defaults(CodecAction action) => action switch
    {
        CodecAction.BaseConvert => ["16", "2"],
        CodecAction.Naming => ["camel"],
        CodecAction.RegexTest => ["", "i"],
        CodecAction.Guid => ["5", "N"],
        CodecAction.SortLines or CodecAction.DeduplicateLines => ["1"],
        _ => []
    };

    // ---------------------------------------------------------------- 执行

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (AutoRun.IsChecked != true) return;
        _autoTimer.Stop();
        _autoTimer.Start();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (OperationList.SelectedItem is not CodecOption option) return;
        try
        {
            ResultBox.Text = Execute(option);
            StatusText.Text = Describe(option);
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "转换失败，请检查输入内容。"; }
    }

    private string Execute(CodecOption option) => option.Action switch
    {
        CodecAction.Hash => _hashes.Compute(InputBox.Text, Hash, Charset),
        CodecAction.Hmac => _crypto.ComputeHmac(InputBox.Text, Param1Box.Text, Hash, Charset),
        CodecAction.AesEncrypt => _crypto.Encrypt(InputBox.Text, Param1Box.Text),
        CodecAction.AesDecrypt => _crypto.Decrypt(InputBox.Text, Param1Box.Text),
        CodecAction.Jwt => _crypto.DescribeJwt(InputBox.Text),
        CodecAction.Base32Encode => _crypto.Base32Encode(InputBox.Text, Charset),
        CodecAction.Base32Decode => _crypto.Base32Decode(InputBox.Text, Charset),
        CodecAction.Base64UrlEncode => ToBase64Url(_codec.Encode(InputBox.Text, Charset)),
        CodecAction.Base64UrlDecode => _codec.Decode(FromBase64Url(InputBox.Text), Charset),
        CodecAction.Guid => _units.GenerateGuids(Count(Param1Box.Text), Has(Param2Box.Text, 'U'), Has(Param2Box.Text, 'B'), Has(Param2Box.Text, 'N')),
        CodecAction.BaseConvert => _units.ConvertBase(InputBox.Text, Radix(Param1Box.Text), Radix(Param2Box.Text)),
        CodecAction.ColorConvert => _units.ConvertColor(InputBox.Text),
        CodecAction.UrlInspect => _units.DescribeUrl(InputBox.Text),
        CodecAction.Naming => _transforms.ApplyNaming(InputBox.Text, ParseStyle(Param1Box.Text)),
        CodecAction.TextStats => _transforms.Describe(InputBox.Text),
        CodecAction.RegexTest => FormatRegex(_transforms.TestRegex(InputBox.Text, Param1Box.Text,
            Has(Param2Box.Text, 'i'), Has(Param2Box.Text, 'm'), Has(Param2Box.Text, 's'), 500)),
        CodecAction.SortLines => _transforms.ApplyLineOperation(InputBox.Text, LineOperation.SortAscending, IgnoreCase()),
        CodecAction.DeduplicateLines => _transforms.ApplyLineOperation(InputBox.Text, LineOperation.Deduplicate, IgnoreCase()),
        CodecAction.ReverseLines => _transforms.ApplyLineOperation(InputBox.Text, LineOperation.Reverse, false),
        CodecAction.RemoveEmptyLines => _transforms.ApplyLineOperation(InputBox.Text, LineOperation.RemoveEmptyLines, false),
        CodecAction.NumberLines => _transforms.ApplyLineOperation(InputBox.Text, LineOperation.NumberLines, false),
        CodecAction.UnixToTime => FromUnix(InputBox.Text),
        CodecAction.TimeToUnix => ToUnix(InputBox.Text),
        _ => _codec.Apply(InputBox.Text, Map(option.Action), Charset)
    };

    private string Describe(CodecOption option) => option.Action switch
    {
        CodecAction.Hash => $"已计算 {HashChoice.Text} 摘要（字符集：{CharsetChoice.Text}）。",
        CodecAction.Hmac => $"已用 {HashChoice.Text} 计算 HMAC 签名。",
        CodecAction.AesEncrypt => "已用口令加密；请把整段结果连同 LPDF1: 前缀一起保存，口令不落盘。",
        CodecAction.AesDecrypt => "已用口令解密。",
        CodecAction.Jwt => "已在本地解码 JWT；未校验签名，也不代表令牌有效。",
        CodecAction.Guid => "已生成新的随机 GUID。",
        CodecAction.BaseConvert => "已完成进制转换。",
        CodecAction.ColorConvert => "已输出该颜色的三种写法。",
        CodecAction.UrlInspect => "已拆解网址各部分；本工具不会访问该地址。",
        CodecAction.Naming => "已按所选风格转换命名。",
        CodecAction.TextStats => "已统计文本规模，未保留原文。",
        CodecAction.RegexTest => "已在本机执行正则匹配。",
        CodecAction.UnixToTime => "已按本机时区换算时间戳。",
        CodecAction.TimeToUnix => "已输出秒和毫秒两种时间戳。",
        _ => $"已完成「{option.Label}」（字符集：{CharsetChoice.Text}）。"
    };

    private static TextCodecOperation Map(CodecAction action) => action switch
    {
        CodecAction.Base64Encode => TextCodecOperation.Base64Encode,
        CodecAction.Base64Decode => TextCodecOperation.Base64Decode,
        CodecAction.UrlEncode => TextCodecOperation.UrlEncode,
        CodecAction.UrlDecode => TextCodecOperation.UrlDecode,
        CodecAction.UnicodeEncode => TextCodecOperation.UnicodeEncode,
        CodecAction.UnicodeDecode => TextCodecOperation.UnicodeDecode,
        CodecAction.HtmlEncode => TextCodecOperation.HtmlEncode,
        CodecAction.HtmlDecode => TextCodecOperation.HtmlDecode,
        CodecAction.HexEncode => TextCodecOperation.HexEncode,
        _ => TextCodecOperation.HexDecode
    };

    // ---------------------------------------------------------------- 参数解析

    private bool IgnoreCase() => Param1Box.Text.Trim() is not ("0" or "false" or "否");

    private static bool Has(string raw, char flag) => raw.ToUpperInvariant().Contains(char.ToUpperInvariant(flag));

    private static int Count(string raw) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 5;

    private static int Radix(string raw) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new PdfToolException("进制请填 2、8、10 或 16。");

    private static NamingStyle ParseStyle(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "camel" or "驼峰" => NamingStyle.CamelCase,
        "pascal" or "帕斯卡" => NamingStyle.PascalCase,
        "snake" or "下划线" => NamingStyle.SnakeCase,
        "kebab" or "短横线" => NamingStyle.KebabCase,
        "constant" or "常量" => NamingStyle.ConstantCase,
        "upper" or "大写" => NamingStyle.UpperCase,
        "lower" or "小写" => NamingStyle.LowerCase,
        "title" or "标题" => NamingStyle.TitleCase,
        _ => throw new PdfToolException("风格只支持 camel / pascal / snake / kebab / constant / upper / lower / title。")
    };

    private static string FormatRegex(RegexResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Summary);
        foreach (var match in result.Matches)
        {
            builder.AppendLine($"位置 {match.Index}，长度 {match.Length}：{match.Value}");
            for (var index = 0; index < match.Groups.Count; index++)
                builder.AppendLine($"    组 {index + 1}：{match.Groups[index]}");
        }
        return builder.ToString().TrimEnd();
    }

    private static string ToBase64Url(byte[] bytes)
    {
        try { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] FromBase64Url(string input)
    {
        var text = new string(input.Where(ch => !char.IsWhiteSpace(ch)).ToArray())
            .Replace('-', '+').Replace('_', '/');
        var remainder = text.Length % 4;
        if (remainder != 0) text = text.PadRight(text.Length + (4 - remainder), '=');
        try { return Convert.FromBase64String(text); }
        catch (FormatException error) { throw new PdfToolException("Base64URL 内容无效，请检查是否完整。", error); }
    }

    private string FromUnix(string input)
    {
        if (!long.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new PdfToolException("请输入整数时间戳，例如 1760000000。");
        var time = UnixTime.FromUnix(value, Milliseconds.IsChecked == true);
        return $"本地时间：{time:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}"
            + $"UTC 时间：{time.UtcDateTime:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
            + $"星期：{CultureInfo.GetCultureInfo("zh-CN").DateTimeFormat.GetDayName(time.DayOfWeek)}";
    }

    private string ToUnix(string input)
    {
        var text = input.Trim();
        if (!DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var time)
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out time))
            throw new PdfToolException("请输入可识别的日期时间，例如 2026-10-15 12:00:00。");
        return $"秒：{UnixTime.ToUnix(time, false)}{Environment.NewLine}毫秒：{UnixTime.ToUnix(time, true)}";
    }

    // ---------------------------------------------------------------- 结果与文件

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        ResultBox.Text = $"秒：{UnixTime.ToUnix(now, false)}{Environment.NewLine}毫秒：{UnixTime.ToUnix(now, true)}";
        StatusText.Text = "已输出当前本机时间的时间戳。";
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (ResultBox.Text.Length == 0) { StatusText.Text = "结果为空，没有可回填的内容。"; return; }
        InputBox.Text = ResultBox.Text;
        StatusText.Text = "已把结果作为新的输入。";
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultBox.Text.Length == 0) { StatusText.Text = "结果为空，没有可复制的内容。"; return; }
        try
        {
            Clipboard.SetText(ResultBox.Text);
            StatusText.Text = "结果已复制到剪贴板。";
        }
        catch (Exception) { StatusText.Text = "剪贴板暂时不可用，请稍后重试。"; }
    }

    private void SaveResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultBox.Text.Length == 0) { StatusText.Text = "结果为空，没有可保存的内容。"; return; }
        var dialog = new SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = "转换结果.txt",
            Title = "另存为新的本地文件（不覆盖已有文件）",
            OverwritePrompt = false,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            LocalTextFile.WriteNew(dialog.FileName, ResultBox.Text);
            StatusText.Text = "已保存为新文件；原文件未被覆盖。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        ResultBox.Clear();
        StatusText.Text = "已清空输入和结果。";
    }

    private void FileToBase64_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "所有文件 (*.*)|*.*",
            Title = "选择要转成 Base64 的本地文件",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var bytes = LocalFileStore.Read(dialog.FileName);
            try
            {
                ResultBox.Text = Convert.ToBase64String(bytes);
                StatusText.Text = $"已把本地文件转成 Base64（{bytes.Length / 1024d:0.#} KB）；内容只显示在本次窗口中。";
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private void Base64ToFile_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Length > 0 ? InputBox.Text : ResultBox.Text;
        if (text.Length == 0) { StatusText.Text = "请先在输入或结果中放入 Base64 内容。"; return; }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray())); }
        catch (FormatException) { StatusText.Text = "Base64 内容无效，请检查是否完整。"; return; }
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "所有文件 (*.*)|*.*",
                FileName = "还原的文件.bin",
                Title = "还原为新的本地文件（不覆盖已有文件）",
                OverwritePrompt = false,
                AddToRecent = false,
                DereferenceLinks = false
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            LocalFileStore.WriteNew(dialog.FileName, bytes);
            StatusText.Text = $"已还原 {bytes.Length} 字节到新文件。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async void FileHash_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "所有文件 (*.*)|*.*",
            Title = "选择要计算摘要的本地文件",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var kind = Hash;
        StatusText.Text = "正在本地计算文件摘要…";
        try
        {
            var path = dialog.FileName;
            ResultBox.Text = await Task.Run(() => _hashes.ComputeFile(path, kind));
            StatusText.Text = $"已计算 {HashChoice.Text} 摘要；文件内容未被保留或上传。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "无法读取该文件，请检查文件权限。"; }
    }
}
