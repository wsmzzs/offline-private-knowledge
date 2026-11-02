# 首次配置与 HTTP 示例

## 页面行为

新用户先进入引导，不会开窗就下载模型。已有旧版设置继续兼容，设置页可重新打开引导。每次进入准备检查都应用当前地址/密钥草稿，不能拿旧连接的成功状态通过新地址。

本机流程：选择方式 → 检查环境和准备模型 → 选择资料目录 → 查看整理状态并进入问答。

企业流程：选择方式 → 输入 IT 地址和密钥并检查 → 查看服务端知识库。企业不下载或启动本机模型，不选择本机资料目录。

完成标记只有成功保存后才生效；未完成配置会在下次继续。没有资料时可以先进入界面稍后添加，状态不会谎称资料已经就绪。首次选择的回答模型会随配置记住。

## 查询准备状态

```http
GET /api/setup
```

```json
{
  "ollamaReady": true,
  "chatModel": "qwen3:1.7b",
  "chatInstalled": true,
  "embeddingModel": "qwen3-embedding:0.6b",
  "embeddingRequired": true,
  "embeddingInstalled": false,
  "message": "请准备缺少的模型，或由维护者修正模型配置。"
}
```

`ollamaReady` 是服务电脑的运行环境状态；`chatModel`/`embeddingModel` 来自服务配置；`Installed` 状态来自本地模型元数据和能力核验。`embeddingRequired=false` 表示仅关键词检索。此接口不代表知识文件已整理好，资料状态另查 `/api/knowledge-bases`。

## 主动准备检索模型

```http
POST /api/setup/download
Content-Type: application/json
```

```json
{"role":"embedding"}
```

```json
{"status":"downloading","message":"正在下载当前文件……","completed":40,"total":100}
{"status":"checking","message":"文件已接收，正在校验完整性……","completed":0,"total":0}
{"status":"success","message":"检索模型下载完成，已确认支持本地向量计算。","completed":0,"total":0}
```

这是多行 JSON 进度示例，不是一次普通 JSON 对象。`role` 只能是 `chat` 或 `embedding`；服务自己读取配置中的模型名，客户端不能给任意网址。`completed`/`total` 是当前文件层的字节数，不是整个模型总进度。只有收到 `success` 并再次检查安装状态才算完成，断线不算成功。

企业服务端会拒绝这个下载入口，即使请求带正确共享密钥。模型下载流只发送模型名，不发送知识文件或问题。

## 一次问答

```http
POST /api/questions
Content-Type: application/json
```

```json
{"knowledgeBaseId":"温度采集与监控软件","question":"设备已连接，为什么没有温度曲线？","model":"qwen3:1.7b"}
```

```json
{
  "requestId":"example-request",
  "status":"answered",
  "knowledgeBaseId":"温度采集与监控软件",
  "knowledgeRevision":1,
  "answer":"连接成功仅表示通信已建立。请先启用通道，点击开始采集，再勾选要显示的曲线通道。",
  "citations":[{"documentId":"example-document-id","title":"操作注意事项.md","section":"操作顺序","excerpt":"在实时监控点击开始采集，再在曲线图例勾选需要显示的通道。"}],
  "model":"qwen3:1.7b"
}
```

这些为手写协议示例，不表示模型每次生成同样文字。`knowledgeBaseId` 来自一级目录；`question` 来自输入框；`model` 是已安装本地回答模型。`knowledgeRevision` 表示资料版本；`citations` 的原文由服务从索引读取；`requestId` 用于排障。无依据时 `status=insufficient_evidence`、引用为空；未调用回答模型时 `model=null`。

资料列表：`GET /api/knowledge-bases`；原文：`GET /api/documents/{documentId}`；健康状态：`GET /health`。资料未就绪可能返回 503，资料在回答期间变化返回 409。接口格式和业务结论应分开理解。
