# 构建、验证与发布

## 开发环境

现有工程使用 .NET 9，`global.json` 指定 SDK 9.0.300。源码构建需要兼容 SDK，普通用户运行 Windows 自带运行时包不需要 SDK。Visual Studio 需要支持此 SDK；Avalonia 预览插件的账号和授权不影响命令行构建或程序运行。

```powershell
dotnet restore .\PrivateKnowledge.sln
dotnet build .\PrivateKnowledge.sln -c Release --no-restore
dotnet test .\PrivateKnowledge.sln -c Release --no-build
```

默认测试不要求 Ollama，不下载模型。修改布局或启动流程后还应在 Visual Studio 中正常启动客户端，检查标题栏、首次配置、最大化和关窗行为。

## 生成公开源码与运行包

PowerShell 7 中运行：

```powershell
pwsh -File .\scripts\build-release.ps1 -Version 0.1.1
```

脚本先导出允许清单内的源码，在另一个构建副本中还原依赖和发布，输出两个 ZIP、`SHA256SUMS.txt`、`release-info.json` 和公开内容审查清单。每次使用新的时间戳目录，旧发布不会被删除。首次还原 Windows 运行时依赖需要联网。当前发布单独固定运行时补丁 `9.0.22`，不要求修改开发 SDK；后续维护者应核对补丁与依赖更新并重新验收。

按现有开发环境保留 .NET 9。[微软支持表](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)列出的 .NET 9 支持结束时间为 2026-11-10；长期维护和正式部署前应安排升级到受支持的版本。当前包仍定位为公开原型。

- `*-source.zip`：可用于创建公开 GitHub 仓库，包含虚构样例、MIT、公开文档、测试和 CI。
- `*-win-x64.zip`：客户端和服务均自带 .NET 运行时，必须完整解压；不包含 Ollama 和模型权重。
- `source/OfflinePrivateKnowledge`：未构建的干净源码副本。
- `build/OfflinePrivateKnowledge`：仅用于本次编译的工作副本，有 `bin/obj`，不要整体上传。

开发目录里的 `SampleKnowledge`、索引数据库、个人设置、历史日志、发布目录和历史排障文档不在导出清单里。运行包的每份样例还会与 `PublicSamples` 比对文件名和 SHA256。检查会拦截常见密钥及本机路径，但不能识别所有个人信息；发布前仍应人工检查新增加的源码、测试夹具和文档。

只需要源码时：

```powershell
pwsh -File .\scripts\export-source.ps1 -Destination 'D:\PublicBuild\OfflinePrivateKnowledge'
```

目标必须不存在。请把导出的干净副本作为公开仓库，不要在包含真实私库的旧开发目录里直接执行“全部上传”。GitHub 地址和发布动作由项目维护者决定，脚本不会创建仓库或推送代码。

## GitHub 自动发布

已配置 `.github/workflows/release.yml`。修改 `.github/release.json` 的版本号并提交到 `main` 后，发布任务会先运行无模型测试，再执行同一套公开源码导出、自带运行时构建和内容检查，通过后创建对应标签及 GitHub Release，上传两个 ZIP、SHA256 和版本信息。也可在 Actions 中手动运行 `Publish Windows release`。

日常只有发布任务拥有仓库内容写权限，使用 GitHub 提供的临时任务令牌；无需保存个人 Token。已有同名 Release 不会被覆盖。当前公开版本是 `v0.1.1`，之后发布新版本需更新版本配置、代码版本和发布说明。工作流不会安装 Ollama、下载模型或读取开发电脑上的资料。

## 真实模型与运行包验收

先在本机安装并启动 Ollama，准备默认回答模型和检索模型。然后指定完整运行包目录：

```powershell
$env:PRIVATEKNOWLEDGE_PACKAGE = 'D:\PublicBuild\OfflinePrivateKnowledge'
dotnet test .\checks\PrivateKnowledge.DesktopChecks\PrivateKnowledge.DesktopChecks.csproj --filter FullyQualifiedName~FirstRunReleaseCheck
```

该检查使用独立设置和随机本机端口，只读取运行包内的虚构样例，走首次配置、真实问答、引用、未知错误拒答和退出后服务停止。会在 `TestResults/first-run-release` 输出结果和截图。真实模型输出具有不确定性，失败应检查实际回答，不能把预期答案写死冒充模型响应。

建议再在全新 Windows 虚拟机验收完整 ZIP：没有预装 SDK/.NET，解压后打开客户端，检查缺少 Ollama 和模型时的提示，按引导完成安装与模型准备，断网后重新问答。开发机上的隔离设置和运行时测试不等同于全新电脑验收。

## 许可与贡献

项目代码采用 MIT。运行包的 `licenses` 保存实际 NuGet 依赖声明和可取得的许可文件；依赖、原生组件、Ollama 与模型分别遵守其上游许可证。模型权重不随包分发。

提交问题时请提供软件版本、操作步骤、脱敏错误和可公开的最小样例；不要上传公司资料、个人档案、数据库或访问密钥。修复应附能说明问题的测试，中文注释应说明目的、输入输出和真实行为。普通 CI 只做构建、无模型自动测试及公开源码导出检查；发布任务仅在明确修改发布版本配置或手动触发时运行。

本仓库还保留一次性旧版撤回工作流，只有修改明确的撤回清单才触发。它只处理清单中的旧 Release、标签及对应构建记录，使用临时任务令牌，不处理当前版本。已经被他人保存的副本无法撤回，服务器未引用对象及缓存也可能需要平台进一步清理。
