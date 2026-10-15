# 本地 PDF 工具开发规则

- 始终用简体中文与用户沟通。
- Windows 原生 .NET 10 / WPF 应用；Abstractions 定义模型和接口，Implement 实现 PDF 处理，Desktop 负责界面。
- 所有文档处理在本机内存中完成。禁止 HTTP、遥测、AI、自动更新、外部字体、CDN、外部网页或任何远程接口。
- 依赖只从 vendor 离线目录恢复；运行构建时设置 DOTNET_CLI_TELEMETRY_OPTOUT=1 和 NUGET_CERT_REVOCATION_MODE=offline。
- 不写文档内容、文件名、路径到日志或使用历史；不自动打开导出文件的外部查看器。
- 不覆盖源文件。导出仅允许新建本地文件；拒绝网络路径。处理失败不修改源文件。
- 默认不添加水印。页面操作必须保持文字、图形的清晰度，并针对不支持的交互文档明确报错。
- 使用合成 PDF 测试，不读取用户文档，不提交业务文件。
