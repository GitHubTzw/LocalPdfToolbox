# PDFgear 2.1.20 静态证据说明

审查范围：D:\开发工具\PDF编辑器 内的 CommonLib.dll、pdfconverter.exe、pdfeditor.exe、PDFLauncher.exe、PDFKit.dll、FileWatcher.exe。

方式：使用本机 System.Reflection.Metadata/PEReader 解析程序集元数据及 IL，不加载执行目标程序集，不启动 PDFgear，不联网，不打开用户 PDF，不读取用户日志。只在审计临时目录新建脚本和本说明。IL 偏移针对当前二进制；方法令牌是程序集内的元数据编号。

## 结论

- 现有 PDFgear 不符合“处理过程中不调用任何外部接口、纯本地”的严格要求：合并入口会调用操作统计发送逻辑。
- 所查核心合并路径采用本机文件流和 PDFium 导入/保存页面；没有在这条路径中发现向外传送 PDF 内容的指令。合并统计参数也未见文件路径或正文。
- 程序含有向外发送文档文本的 AI 功能、上传文件流的在线转换实现，以及用户选择附带样本时上传源文件的反馈流程。
- 以上证明程序有相应行为实现及部分调用链，不能证明你的历史文件已经泄露，也不能证明整套软件和全部第三方库在所有路径上都不传文件。未做软件运行或抓包试验。

## 1. 合并核心在本地执行，入口会安排统计请求

文件：pdfconverter.exe。

- MergePDFUCViewModel+<DoMergePDFFiles>d__71.MoveNext，0x0600086D：
  - IL_0026/002B/0030 加载固定字符串 MergePDF / BtnClick / Count。
  - IL_0037 调用 CommonLib.Common.GAManager.SendEvent。调用在文件列表及输出路径检查之前。
- MergePDFUCViewModel+<>c__DisplayClass71_0+<<DoMergePDFFiles>b__1>d.MoveNext，0x0600095B：
  - IL_0011/0016/001B 加载 MergePDF / Begin / Count；IL_0022 发统计。
  - IL_0051 调用 PdfiumNetHelper.MergeAsync。
  - 失败分支 IL_019A/019F/01A4 加载 MergePDF / Failed / Count；IL_01AB 发统计。
- PdfiumNetHelper+<MergeAsync>d__1.MoveNext，0x06000799：
  - IL_0076：Patagames.Pdf.Net.PdfDocument.CreateNew。
  - IL_00FB：创建 System.IO.FileStream；IL_0108：PdfDocument.Load。
  - IL_0167：PdfPageCollection.ImportPages。
  - IL_0219：System.IO.File.OpenWrite；IL_0235：PdfDocument.Save。
  - 该方法中未见 HTTP/上传调用。

可以使用的表述：“点击合并会尝试上报操作统计；目前没有发现合并核心上传 PDF 正文。”

## 2. 统计发送条件与载荷

文件：CommonLib.dll。

- GAManager.SendEvent，0x060005DA，IL_0004：无条件转发 GA4Manager.SendEvent。
- GA4Manager.SendEvent，0x060005CB，IL_0033：TaskFactory.StartNew 排队执行统计。
- GA4Manager 静态构造，0x060005CA：
  - IL_0043/0048 保存目标 analytics.google.com 的 /g/collect 地址。
  - IL_0057 调用 Init。
  - IsSendDebugEvent 被初始化为 false，但在上述六个第一方程序集的 IL 搜索中未发现它被读取来阻止发送；不能将该字段视为关闭统计的开关。
- GA4Manager.Init，0x060005CE：
  - IL_0000 调用 UtilManager.GetUUID，IL_000A 获取应用版本。
  - IL_0014–002D：UUID 或版本为空时初始化失败；否则构造基础查询参数。
  - 参数键：v、tid、cid、ul、_et、ep.app_version_2；首次使用还可能添加 _fv。
  - cid 来源于本地 UUID。UtilManager.GetUUID（0x060006EB）在缺少标识时调用 ConfigManager.GenerateUserUUID（0x06000478）；后者用 Guid.NewGuid 保存到配置。
- GA4Manager+<>c__DisplayClass10_0.<SendEvent>b__0，0x06000B33：
  - 添加 UserType、UserCID、UAEventCategory、UAEventAction、UAEventLabel、UAEventValue，事件名 UAEvents。
  - 合并入口传入的类别/动作/标签/数值是固定统计值；这一调用链没有读取 PDF 文件正文或完整路径来填这些参数。
- GA4Manager.GenerateRequestUrl，0x060005CF，拼接基础参数与事件参数；参数值经 EscapeDataString 转义（辅助方法 0x06000B31）。
- GA4Manager+<TrackAsync>d__12.MoveNext，0x06000B38，IL_002F：调用 RequestUrlAsync。
- GA4Manager+<RequestUrlAsync>d__15.MoveNext，0x06000B36，IL_003F：HttpClient.PostAsync。

已核对的发送链没有看到用户允许/拒绝统计的判断。网络不可达、防火墙拦截或初始化失败会影响实际送达；本审查不能判定服务器是否收到某次历史事件。

## 3. AI 聊天/摘要会把 PDF 的文本数据发到外部服务

文件：pdfeditor.exe。完整类型前缀为 pdfeditor.Utils.Copilot.CopilotHelper。

- GetPageContent，0x06000C2E，IL_00B8：调用 PDFium 的 FPDFText_GetText 提取页面文本。
- AnalyzeTask+<AnalyzeAsync>d__15.MoveNext，0x06003889：
  - IL_00FE 调用 GetPageContentAsync。
  - IL_0182 把结果传给 InternalCopilotHelper.AnalyzeAsync。
- InternalCopilotHelper.get_HttpClient，0x06002FAF，IL_0021/002B：BaseAddress 指向 chatapi.pdfgear.com。
- InternalCopilotHelper+<AnalyzeAsync>d__4.MoveNext，0x060038B7：
  - IL_001D 把文本放入 AnalyzeRequestModel.Text；IL_0028 放入 User（本地 UUID）。
  - IL_0033 路径 /pdf/analyze，IL_0039 构造 JSON，IL_0044 调用 HttpClient.PostAsync。
- InternalCopilotHelper.BuildJsonContent，0x06002FB8，IL_0015/0024：JsonConvert.SerializeObject + UTF-8 StringContent(application/json)。
- <BuildPdfModel>d__37.MoveNext，0x0600307B：
  - IL_0137 获取页面文本，IL_0300 截取文本片段。
  - IL_033C 填 PdfPageModel.Content，IL_0348 填页序号。
  - IL_03A8 填总页数；IL_03B9 用 Path.GetFileNameWithoutExtension，IL_03BE 填文件名。
- <ChatAsync>d__35.MoveNext，0x0600307D：IL_02CB 创建上述模型，IL_0448 调用内部聊天请求。
- InternalCopilotHelper+<ChatAsync>d__6.MoveNext，0x060038B9：
  - 填 Messages、Pdf、Stream、User、Language。
  - IL_005E 路径 /pdf/chat；IL_006A JSON；IL_0082 HttpClient.SendAsync。
- InternalCopilotHelper+<GetSummaryAsync>d__5.MoveNext，0x060038C1：
  - 填 Pdf、Stream、User、Language。
  - IL_004D 路径 /pdf/getsummary；IL_0071 HttpClient.SendAsync。
- ChatPanel+<NavigatedFromWelcome>d__17.MoveNext（0x060036D8，IL_00A1）调用 InitializeAsync；其状态机（0x0600308B，IL_008B）启动 AnalyzeTask。

因此“没有直接上传整个 PDF 文件”并不等于“没有发送文件内容”；这里有明确的提取文本并发送的实现。文本覆盖哪些页面、实际是否被触发、服务器后续保存策略未通过运行验证。

## 4. 在线转换包含上传文件流的实现

文件：pdfconverter.exe。

- OnlineConvertUtils 静态构造，0x060004FE：认证与转换目标为 apiw.pdfgear.com 的 /v1/auth 与 /v1/convert。
- OnlineConvertUtils+<RequestConvertFile>d__5.MoveNext，0x060008F1：
  - IL_006F 创建源文件 FileStream。
  - IL_00B2 创建 MultipartFormDataContent。
  - IL_00DD 创建 StreamContent；IL_00E2 字段名 attachment；IL_00ED 获取文件名；IL_00F2 添加文件内容。
  - IL_0109 使用 POST；IL_011E 设置请求内容；IL_014C HttpClient.SendAsync。
- MainWindow+<doConvertOnline_SingleFile>d__31.MoveNext，0x060007B3，IL_01EA 调用上述上传方法。
- MainWindow.doConvert，0x0600010B，含直接在线分支；本地转换失败时，IL_0102/023F 显示确认窗口，检查结果后才调用在线分支（IL_0137/0269）。

这是在线转换调用链，不能移作“合并会上传文件”的证据。该版本实际界面是否展示全部旧转换入口未做动态验证。

## 5. 合并失败反馈可附带源文件，但附件默认不勾选

文件：pdfconverter.exe 与 CommonLib.dll。

- 合并失败分支可打开 FeedbackWindow；0x0600086C 会把选中的 MergeFileItem.FilePath 加入反馈窗口 flist，再调用 showAttachmentCB(true)。
- FeedbackWindow.showAttachmentCB，0x060000B3：IL_0080/0086 将 sendSampleCB.IsChecked 设置为 false；隐藏附件控件分支也设为 false。
- FeedbackWindow+<SendBtn_Click>d__7.MoveNext，0x06000788：IL_00FB 读取该复选框，IL_010D 保存 shoudSendAttachment。
- 反馈发送任务，0x06000784：IL_0036 判断 shoudSendAttachment，选中时 IL_0043 调用 CreateZipFile；后续把 ZIP 放入 attachment 字段并通过 WebRequest 发出。
- FeedbackWindow.CreateZipFile，0x060000B4：遍历 flist，复制源文件到本地缓存，压缩后供反馈发送。未在报告保留内置压缩口令。
- CommonLib.Common.RequestHelper.CreateRequest，0x0600066E：目标由 fds.pdfgear.com 的 /1/ 加请求名构成，此处请求名 feedback；HTTP 方法为 POST。

结论：不能说合并失败自动把文件发走；需要用户点击发送，且附件复选框被选中。对敏感文件应避免发送样本反馈。

## 6. 其他所查模块

- PDFLauncher.exe 的 App.AppLaunchEvents（0x06000045，IL_0011）等位置也调用 GAManager.SendEvent，统计不只出现在合并操作中。
- PDFKit.dll 搜索命中的网址主要是 XML/XAML 命名空间；不能把这种字符串当作网络请求证据。未在该模块找到所搜 HTTP/上传调用。
- FileWatcher.exe 未找到所搜直接 HTTP/上传调用；这不等于其调用的全部共享库已经被排除风险。

本报告不包含用户 PDF 内容、历史日志、完整用户标识、令牌或密钥。

## 7. 本机只读检查补充

以下由主审查补充，日期为 2026-10-09（Asia/Shanghai），不属于上述反编译子审查：

- 安装文件版本信息显示 PDFgear 2.1.20.0。
- `NLog.config` 第 13–18 行配置日志写入 LocalAppData 下的 PDFGEAR/Logs，记录 Info 及以上消息与异常，最多保留 30 个归档文件。
- 本机 PDFgear 数据目录包含 AppData、LocalCache、Logs、TempState。配置数据库含 `HistoryFiles` 字段，本次只检查字段存在，未展示或导出其中的文档路径。
- LocalCache 中存在 `debug.zip`，仅查看归档条目类型，其中有日志文件；这不能证明该归档已被上传。
- 检查时发现 FileWatcher.exe 与 PDFLauncher.exe 正在运行，其进程树当时没有 TCP 连接或 UDP 端点。这只是一次快照，不代表运行全程、过去或以后不会联网。
- 当时的两份日志仅作本机关键词计数，未发现 PDF 正文或 PDF 文件名的匹配，也未展示日志原文；没有命中并不能排除其他情况下记录相关内容。
- 未启动 PDFgear、未加载真实用户 PDF、未联网验证服务、未更改软件设置或防火墙。
