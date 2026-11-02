// 用途：描述客户端实际收发的 JSON；与服务内部数据库对象分开，字段示例见 docs/客户端使用与代码导读.md。
namespace PrivateKnowledge.Client;

/// <summary>服务发现的一套项目资料。</summary>
/// <param name="Id">一级目录名，例如 demo-a。</param>
/// <param name="Status">ready 表示可用，updating 表示正在更新。</param>
/// <param name="Revision">服务端已发布的资料修订号。</param>
/// <param name="DocumentCount">最近成功发布的文件数量。</param>
/// <param name="Message">服务提供的中文状态说明。</param>
public sealed record KnowledgeBaseInfo(string Id, string Status, long Revision, int DocumentCount, string Message)
{
    /// <summary>显示给操作人员的状态；错误仍保留服务原文供排查。</summary>
    public string DisplayStatus => Status switch
    {
        "ready" => "资料已就绪", "checking" => "正在核对资料", "updating" => "正在更新资料",
        "empty" => "尚无有效资料", "missing" => "资料目录已移除", "unavailable" => "资料目录无法访问",
        "error" => "资料处理失败", _ => "等待检查"
    };
}

/// <summary>一次独立问题，不包含聊天历史。</summary>
/// <param name="KnowledgeBaseId">来自左侧项目选择，例如 demo-a。</param>
/// <param name="Question">来自输入框的完整问题，例如“连接后为什么没有温度曲线？”。</param>
/// <param name="Model">下拉框选择的完整名称；省略时由服务使用默认模型。</param>
public sealed record AskRequest(string KnowledgeBaseId, string Question, string? Model = null);

/// <summary>服务确认的已安装本地聊天模型。</summary>
/// <param name="Name">Ollama 模型完整名称，例如 qwen3:1.7b。</param>
/// <param name="SupportsThinking">模型是否支持思考开关，由服务决定请求参数。</param>
public sealed record ChatModelInfo(string Name, bool SupportsThinking);

/// <summary>模型下拉框的数据来源。</summary>
/// <param name="DefaultModel">服务配置的默认名称。</param>
/// <param name="Models">允许选择的本地聊天模型。</param>
public sealed record ModelCatalog(string DefaultModel, IReadOnlyList<ChatModelInfo> Models);

/// <summary>可以点击查看原文的一条引用。</summary>
/// <param name="DocumentId">服务生成的原文编号，不是磁盘路径。</param>
/// <param name="Title">原文件名，如操作流程.md。</param>
/// <param name="Section">引用段落所属的标题。</param>
/// <param name="Excerpt">服务返回的原文片段，不是模型编写的摘要。</param>
public sealed record SourceCitation(string DocumentId, string Title, string Section, string Excerpt);

/// <summary>服务正常处理后的问答结果；依据不足也是正常结果。</summary>
/// <param name="RequestId">排查服务日志时使用的请求编号。</param>
/// <param name="Status">answered、insufficient_evidence 或 needs_clarification。</param>
/// <param name="KnowledgeBaseId">实际使用的知识库，必须和请求相同。</param>
/// <param name="KnowledgeRevision">回答所依据的资料修订号。</param>
/// <param name="Answer">供用户阅读的完整回答。</param>
/// <param name="Citations">经过服务检查的来源列表。</param>
/// <param name="Model">实际调用的模型；没有检索依据且未调用模型时为 null。</param>
public sealed record AnswerResult(string RequestId, string Status, string KnowledgeBaseId,
    long KnowledgeRevision, string Answer, IReadOnlyList<SourceCitation> Citations, string? Model = null);

/// <summary>引用对应的完整资料内容。</summary>
/// <param name="DocumentId">请求中的资料编号。</param>
/// <param name="KnowledgeBaseId">资料所属项目。</param>
/// <param name="KnowledgeRevision">当前资料修订号。</param>
/// <param name="RelativePath">知识库内部路径，不是本机绝对路径。</param>
/// <param name="Text">服务保存的原文全文。</param>
public sealed record DocumentResult(string DocumentId, string KnowledgeBaseId, long KnowledgeRevision, string RelativePath, string Text);

/// <summary>服务健康信息；即使 HTTP 503，也应读取正文解释具体原因。</summary>
/// <param name="Status">ready 或 degraded。</param>
/// <param name="Catalog">ready 或知识目录的错误代码。</param>
/// <param name="Model">ready、unavailable、chat_model_missing 等模型状态。</param>
/// <param name="KnowledgeRootPath">服务实际监视的绝对资料路径；旧版服务不返回时为 null。</param>
/// <param name="ScanSeconds">自动核对间隔，例如 5 秒；旧服务为 0。</param>
/// <param name="Retrieval">检索方式和检索模型；兼容旧服务，未返回时为 null。</param>
public sealed record HealthInfo(string Status, string Catalog, string Model, string? KnowledgeRootPath = null, int ScanSeconds = 0, RetrievalInfo? Retrieval = null)
{
    /// <summary>只有目录和模型都可用，才允许在就绪项目中发送问题。</summary>
    public bool IsReady => Status == "ready" && Catalog == "ready" && Model == "ready";
    /// <summary>解释状态，不把服务可连接误报成模型可回答。</summary>
    public string Description => Model switch
    {
        "unavailable" => "问答环境已连接，但 Ollama 无法连接；请安装或启动 Ollama 后重新准备。",
        "chat_model_missing" => "问答服务已连接，但未找到配置的聊天模型。",
        "embedding_model_missing" => $"未找到检索模型 {Retrieval?.EmbeddingModel}，请在服务所在电脑安装；个人用户可查看项目部署说明。",
        "embedding_model_invalid" => "检索模型配置无效：必须使用已安装的本地嵌入模型，不能使用聊天模型或远程别名。",
        _ => IsReady ? "服务和模型已就绪" : "服务已连接，资料尚未就绪或正在更新。"
    };
}

/// <summary>服务返回的检索配置，只读展示，客户端不会更改共享服务的检索模型。</summary>
/// <param name="Mode">hybrid 为混合检索，keyword 为关键词检索。</param>
/// <param name="EmbeddingModel">服务实际使用的嵌入模型名称。</param>
/// <param name="EmbeddingOnCpu">是否为嵌入请求指定 CPU。</param>
/// <param name="EmbeddingKeepAliveSeconds">嵌入计算完成后的驻留秒数。</param>
public sealed record RetrievalInfo(string Mode, string? EmbeddingModel, bool EmbeddingOnCpu, int EmbeddingKeepAliveSeconds);

/// <summary>统一错误正文，仅用于把服务错误转换成可读提示。</summary>
/// <param name="RequestId">服务日志中的请求编号。</param>
/// <param name="Code">如 knowledge_changed，供程序判断错误类型。</param>
/// <param name="Message">服务返回的中文说明。</param>
internal sealed record ErrorBody(string RequestId, string Code, string Message);

/// <summary>客户端可预期的连接、协议或业务故障，不把堆栈直接显示给操作人员。</summary>
/// <param name="message">面向用户的说明。</param>
/// <param name="code">稳定错误代码，便于测试和后续接入。</param>
/// <param name="requestId">服务返回的请求编号；连接失败时为空。</param>
public sealed class KnowledgeApiException(string message, string code = "client_error", string? requestId = null) : Exception(message)
{
    /// <summary>错误类型，例如 timeout。</summary>
    public string Code { get; } = code;
    /// <summary>可复制给维护人员的服务请求编号。</summary>
    public string? RequestId { get; } = requestId;
}
