using System.Windows;
using System.Windows.Controls;
using CRTVUP.Unify.LocalPdf.Abstractions;
using CRTVUP.Unify.LocalPdf.Implement;
using Microsoft.Win32;

namespace CRTVUP.Unify.LocalPdf.DesktopV2.Views;

/// <summary>
/// JSON / XML 本地工具：格式化、压缩、校验、转义、JSON/XML/CSV 互转、
/// 路径查询、结构统计、数组整理，以及 C#/TypeScript/Java/Python/Go/SQL 模型生成。
/// </summary>
public partial class JsonTab : UserControl
{
    private readonly IStructuredTextTool _tool = new StructuredTextTool();
    private readonly IStructuredTextConverter _converter = new StructuredTextConverter();
    private readonly ICodeModelGenerator _models = new CodeModelGenerator();
    private string _resultExtension = ".json";

    public JsonTab() => InitializeComponent();

    private StructuredFormat Format => FormatChoice.SelectedIndex == 1 ? StructuredFormat.Xml : StructuredFormat.Json;

    private FormatOptions Options => new()
    {
        IndentSize = IndentChoice.SelectedIndex == 1 ? 4 : 2,
        UseTabs = IndentChoice.SelectedIndex == 2,
        SortKeys = SortKeys.IsChecked == true
    };

    private CodeLanguage TargetLanguage => ModelLanguage.SelectedIndex switch
    {
        1 => CodeLanguage.TypeScript,
        2 => CodeLanguage.Java,
        3 => CodeLanguage.Python,
        4 => CodeLanguage.Go,
        5 => CodeLanguage.Sql,
        _ => CodeLanguage.CSharp
    };

    // ---------------------------------------------------------------- 解析与文本处理

    private void Format_Click(object sender, RoutedEventArgs e) => Run(() => _tool.Format(InputBox.Text, Format, Options));
    private void Minify_Click(object sender, RoutedEventArgs e) => Run(() => _tool.Minify(InputBox.Text, Format));

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _tool.Validate(InputBox.Text, Format);
            StatusText.Text = result.IsValid
                ? result.Message
                : result.Line > 0 ? $"{result.Message}（第 {result.Line} 行第 {result.Column} 列）" : result.Message;
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private void Escape_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultBox.Text = _tool.EscapeJsonString(InputBox.Text);
            _resultExtension = ".txt";
            StatusText.Text = "已转换为 JSON 字符串转义形式。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private void Unescape_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultBox.Text = _tool.UnescapeJsonString(InputBox.Text);
            _resultExtension = ".txt";
            StatusText.Text = "已还原转义文本。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    // ---------------------------------------------------------------- 格式互转

    private void ToXml_Click(object sender, RoutedEventArgs e) => RunConversion(_converter.JsonToXml(InputBox.Text), ".xml");
    private void ToJson_Click(object sender, RoutedEventArgs e) => RunConversion(_converter.XmlToJson(InputBox.Text), ".json");
    private void ToCsv_Click(object sender, RoutedEventArgs e) => RunConversion(_converter.JsonToCsv(InputBox.Text), ".csv");
    private void FromCsv_Click(object sender, RoutedEventArgs e) => RunConversion(_converter.CsvToJson(InputBox.Text), ".json");

    // ---------------------------------------------------------------- 结构查询与数组整理

    private void Describe_Click(object sender, RoutedEventArgs e) => RunConversion(_converter.Describe(InputBox.Text), ".txt");

    private void Query_Click(object sender, RoutedEventArgs e) =>
        RunConversion(_converter.Query(InputBox.Text, QueryPathBox.Text), ".json");

    private void Deduplicate_Click(object sender, RoutedEventArgs e) =>
        RunConversion(_converter.DeduplicateArray(InputBox.Text, ignoreCase: true), ".json");

    private void SortArray_Click(object sender, RoutedEventArgs e) =>
        RunConversion(_converter.SortArray(InputBox.Text, descending: false), ".json");

    // ---------------------------------------------------------------- 模型生成

    private void Classes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var language = TargetLanguage;
            ResultBox.Text = _models.Generate(InputBox.Text, language, new CodeModelOptions
            {
                RootName = ClassNameBox.Text,
                Namespace = NamespaceBox.Text,
                ClassSuffix = language == CodeLanguage.CSharp ? "Model" : "",
                StringLength = int.TryParse(StringLengthBox.Text, out var length) && length > 0 ? length : 0
            });
            _resultExtension = language switch
            {
                CodeLanguage.TypeScript => ".ts",
                CodeLanguage.Java => ".java",
                CodeLanguage.Python => ".py",
                CodeLanguage.Go => ".go",
                CodeLanguage.Sql => ".sql",
                _ => ".cs"
            };
            StatusText.Text = language == CodeLanguage.CSharp
                ? "已按 CRTVUP 模型风格生成：块级命名空间、Model 后缀、逐成员注释。"
                : $"已生成 {ModelLanguage.Text} 类型定义；字段名按该语言的命名习惯转换，可自行调整。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "生成失败，请检查 JSON 结构。"; }
    }

    // ---------------------------------------------------------------- 通用执行与文件

    private void Run(Func<FormatResult> action)
    {
        try
        {
            var result = action();
            ResultBox.Text = result.Output;
            _resultExtension = Format == StructuredFormat.Xml ? ".xml" : ".json";
            StatusText.Text = result.Summary;
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "处理失败，请检查输入内容。"; }
    }

    private void RunConversion(ConversionText result, string extension)
    {
        ResultBox.Text = result.Output;
        _resultExtension = extension;
        StatusText.Text = result.Summary;
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "文本文件 (*.json;*.xml;*.csv;*.txt)|*.json;*.xml;*.csv;*.txt|所有文件 (*.*)|*.*",
            Title = "读取本地文本",
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        LoadFiles([dialog.FileName]);
    }

    /// <summary>从主窗口拖拽进来时调用：读取第一个文本文件到输入框。</summary>
    public void LoadFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        try
        {
            InputBox.Text = LocalTextFile.Read(files[0]);
            StatusText.Text = files.Count > 1
                ? $"已读取 {System.IO.Path.GetFileName(files[0])}；一次只处理一个文本，其余文件已忽略。"
                : "已读取本地文本；内容只在本次窗口中显示。";
        }
        catch (PdfToolException error) { StatusText.Text = error.Message; }
    }

    private void SaveResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultBox.Text.Length == 0) { StatusText.Text = "结果为空，没有可保存的内容。"; return; }
        var dialog = new SaveFileDialog
        {
            Filter = $"结果文件 (*{_resultExtension})|*{_resultExtension}|所有文件 (*.*)|*.*",
            DefaultExt = _resultExtension,
            AddExtension = true,
            FileName = "结果" + _resultExtension,
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

    private void UseResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultBox.Text.Length == 0) { StatusText.Text = "结果为空，没有可回填的内容。"; return; }
        InputBox.Text = ResultBox.Text;
        StatusText.Text = "已把结果作为新的输入。";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        ResultBox.Clear();
        StatusText.Text = "已清空输入和结果。";
    }
}
