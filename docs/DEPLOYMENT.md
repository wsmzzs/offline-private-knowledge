# 部署说明

## 个人本机使用

解压整个 Windows x64 包，启动 `PrivateKnowledge.Desktop.exe`。首次配置选择“本机使用”。客户端会启动自己携带的问答服务；关窗时停止自己启动的服务。已由其他程序或 IT 启动的独立服务不会被关闭，Ollama 也不会随客户端退出。

运行包自带 .NET 运行时，用户不用安装 SDK。Ollama 和模型需要另外准备，不在软件 ZIP 内。下载模型需要联网；本机模型和资料准备完成后，知识索引和问答可离线使用。模型管理页的官方模型目录需要联网查询。

客户端设置在当前用户的 `%LOCALAPPDATA%/PrivateKnowledge.Desktop/settings.json`。默认服务端资料在 `service/SampleKnowledge`，默认索引在 `service/data`；选择其他资料目录后，客户端会在自身目录的 `local-data` 中按路径分开保存索引。实际生效位置以客户端和服务健康接口返回的目录为准。软件应解压到当前用户可写的位置，例如 D 盘自己的工具目录，不要放在只读的系统目录。

## 企业内部共享

IT 在一台服务器运行 `service/PrivateKnowledge.Api.exe`，维护资料、索引、Ollama、模型和访问地址。员工在首次配置选择“企业连接”，填写 IT 提供的地址与密钥。员工客户端不启动或关闭服务器，不下载服务器模型，也不修改服务器资料目录。

以下是 IT 在 PowerShell 中配置一个实例的示例。`knowledge.example.internal` 和路径都是示意值，必须换成实际部署信息。密钥应使用独立生成的随机值，通过部署环境注入，不要提交到仓库：

```powershell
$env:Deployment__Mode = 'Enterprise'
$env:Deployment__ApiKey = '<至少32字符的随机共享密钥>'
$env:ASPNETCORE_URLS = 'http://0.0.0.0:5088'
$env:AllowedHosts = 'knowledge.example.internal'
$env:Knowledge__RootPath = 'D:\PrivateLibrary\Knowledge'
$env:Knowledge__DataPath = 'D:\PrivateLibrary\Index'
$env:Model__BaseUrl = 'http://127.0.0.1:11434/'
& '.\service\PrivateKnowledge.Api.exe'
```

这是前台启动示例。随操作系统开机启动、运行账号、进程守护、日志轮转和防火墙由 IT 的部署工具负责，项目没有静默安装 Windows 服务。默认配置只用于本机，企业模式需要显式开启，Host 和监听地址需要同时正确配置。密钥通过 `X-Knowledge-Key` 请求头发送，客户端不把密钥写入设置文件，重新开软件需要重新填写。

**当前共享密钥对应整个实例，所有持有者可读取该实例的全部项目。** 没有个人账号、按项目授权或租户隔离。有权限隔离需求时，需要分别部署实例或继续开发鉴权功能。在不受信任的网络中应由 IT 提供 HTTPS 入口；普通 HTTP 无法保护传输中的资料和密钥。Ollama 默认保持服务器本机可访问，不需要直接暴露给员工电脑。

## 模型配置与资源

配置文件位于 `service/appsettings.json`，环境变量同名层级用双下划线，例如 `Model__ChatModel`。下面只列出常用配置，完整默认值以仓库中的该文件为准：

```json
{
  "Knowledge": {
    "RootPath": "SampleKnowledge",
    "DataPath": "data",
    "ScanSeconds": 5,
    "UseEmbeddings": true,
    "EmbeddingBatchSize": 4,
    "TopChunks": 4,
    "EvidenceMaxBytes": 4300
  },
  "Model": {
    "BaseUrl": "http://127.0.0.1:11434/",
    "ChatModel": "qwen3:1.7b",
    "EmbeddingModel": "qwen3-embedding:0.6b",
    "TimeoutSeconds": 120,
    "EmbeddingOnCpu": true,
    "EmbeddingKeepAliveSeconds": 0,
    "ChatKeepAliveSeconds": 300
  }
}
```

检索模型默认用 CPU、分批生成向量、请求后释放驻留，减少显存压力；这会增加冷启动和首次索引时间。回答模型默认短暂驻留以减少重复加载。两个模型的文件都会占磁盘，加载后的资源占用取决于模型、上下文长度、硬件和 Ollama 配置，不承诺所有电脑都能流畅运行。

同一问答服务中的模型任务由本服务协调；多个独立服务共用 Ollama 时，不能依靠单个实例的协调器限制整体负载。计划多人共享时，请用实际资料和问题测试 1、2、5、10 个并发客户端，再决定排队、超时和机器配置。

## 更新与备份

资料按项目子目录放置，修改后自动扫描，无需客户端重新导入。应等项目状态变为“资料已就绪”、修订号增加后再重新提问；旧回答不会自动重算。备份原始资料、服务配置和部署密钥，索引可根据原始资料重新构建。替换软件版本前退出客户端和服务，不要用新版样例覆盖自己的资料目录。
