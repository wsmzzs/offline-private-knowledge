// 用途：说明 HTTP 请求、回答与知识索引使用的数据结构；接口会按 camelCase 输出 JSON。
namespace PrivateKnowledge.Api.Contracts;

/// <summary>一次独立问答请求；本版没有隐式聊天记忆。</summary>
/// <param name="KnowledgeBaseId">客户端选择的一级目录名，例如 demo-a。</param>
/// <param name="Question">本次完整问题，例如“连接后为什么没有温度曲线？”。</param>
/// <param name="Model">下拉框选择的本地聊天模型，例如 qwen3:1.7b；省略时使用服务默认值。</param>
public sealed record QuestionRequest(string KnowledgeBaseId, string Question, string? Model = null);

/// <summary>已安装且支持聊天的本地模型，不包含云端或仅生成向量的模型。</summary>
/// <param name="Name">Ollama 中的完整模型名。</param>
/// <param name="SupportsThinking">是否支持思考开关；支持时关闭思考以减少等待。</param>
public sealed record ChatModelInfo(string Name, bool SupportsThinking);

/// <summary>客户端模型下拉框的数据。</summary>
/// <param name="DefaultModel">服务配置的默认名称，即使模型文件缺失也保留以便说明问题。</param>
/// <param name="Models">已经确认可以在本地聊天的模型。</param>
public sealed record ModelCatalog(string DefaultModel, IReadOnlyList<ChatModelInfo> Models);

/// <summary>回答中的可核对依据，原文由服务读取，不能由模型自行伪造。</summary>
/// <param name="DocumentId">按文件内容生成的文档编号，用于打开对应原文。</param>
/// <param name="Title">文档文件名，不包含服务器绝对路径。</param>
/// <param name="Section">原文标题、PDF 物理页码或 Excel 工作表/行号。</param>
/// <param name="Excerpt">本次提供给模型的真实原文段落。</param>
public sealed record Citation(string DocumentId, string Title, string Section, string Excerpt);

/// <summary>正常的业务回答；证据不足也属于正常结果，而非网络故障。</summary>
/// <param name="RequestId">本次请求编号，便于排查。</param>
/// <param name="Status">answered、insufficient_evidence 或 needs_clarification。</param>
/// <param name="KnowledgeBaseId">实际检索的知识库。</param>
/// <param name="KnowledgeRevision">回答使用的资料修订号，不是生产软件版本。</param>
/// <param name="Answer">用户可阅读的中文说明。</param>
/// <param name="Citations">回答使用的资料原文；证据不足时为空。</param>
/// <param name="Model">本次实际调用的模型；检索无依据且未调用模型时为 null。</param>
public sealed record QuestionResponse(string RequestId, string Status, string KnowledgeBaseId,
    long KnowledgeRevision, string Answer, IReadOnlyList<Citation> Citations, string? Model = null);

/// <summary>服务故障的统一返回结构，不向用户泄露异常堆栈或磁盘绝对路径。</summary>
/// <param name="RequestId">请求编号。</param>
/// <param name="Code">程序可判断的错误名称，例如 model_unavailable。</param>
/// <param name="Message">中文错误说明。</param>
public sealed record ApiError(string RequestId, string Code, string Message);

/// <summary>一份已读取的资料快照；旧文件被覆盖后，仍可用内容指纹识别变化。</summary>
/// <param name="Id">知识库、相对路径和内容共同决定的编号。</param>
/// <param name="RelativePath">知识库内相对路径，例如 guide/操作.md。</param>
/// <param name="Hash">文件原始字节的 SHA256，用于增量处理。</param>
/// <param name="Text">按文件格式提取的阅读文字，带出处标记；不是 Office/PDF 版式预览。</param>
/// <param name="Chunks">本文件的可检索段落。</param>
public sealed record KnowledgeDocument(string Id, string RelativePath, string Hash, string Text, List<KnowledgeChunk> Chunks);

/// <summary>一条可检索段落，保留标题与正文；语义向量可为空。</summary>
/// <param name="Id">文档内段落编号。</param>
/// <param name="DocumentId">所属文档编号。</param>
/// <param name="Title">可展示的文件名称。</param>
/// <param name="Section">原始标题。</param>
/// <param name="Text">段落正文。</param>
/// <param name="Vector">嵌入模型生成的数字数组；关键词模式为 null。</param>
public sealed record KnowledgeChunk(string Id, string DocumentId, string Title, string Section, string Text, float[]? Vector);

/// <summary>已发布的完整知识库快照；问答始终使用同一修订号的内容。</summary>
/// <param name="Id">知识库标识。</param>
/// <param name="Revision">每次发布新内容递增。</param>
/// <param name="Fingerprint">文件集合与处理规则共同生成的指纹。</param>
/// <param name="Pipeline">拆分规则和嵌入模型配置标记。</param>
/// <param name="Documents">本次发布的全部文档。</param>
public sealed record KnowledgeSnapshot(string Id, long Revision, string Fingerprint, string Pipeline, List<KnowledgeDocument> Documents);

/// <summary>知识库当前状态，与旧快照分开保存；更新失败时旧快照不会当成最新内容回答。</summary>
public sealed class BaseState
{
    /// <summary>一级目录名。</summary>
    public string Id { get; set; } = "";
    /// <summary>checking、updating、ready、error、unavailable、missing 或 empty。</summary>
    public string Status { get; set; } = "checking";
    /// <summary>最近成功发布的修订号。</summary>
    public long Revision { get; set; }
    /// <summary>最近成功发布的文件集合指纹。</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>当前状态的中文说明；不保存问答正文。</summary>
    public string Message { get; set; } = "等待首次扫描。";
    /// <summary>本次处理状态更新时间，统一使用 UTC。</summary>
    public string UpdatedAt { get; set; } = "";
    /// <summary>已发布资料数量。</summary>
    public int DocumentCount { get; set; }
}

/// <summary>内部可预期故障；最外层转换成一致的 HTTP 错误。</summary>
/// <param name="statusCode">HTTP 状态码，例如知识库未就绪时为 503。</param>
/// <param name="code">稳定错误编号，例如 knowledge_not_ready。</param>
/// <param name="message">面向调用者的安全中文说明。</param>
public sealed class ServiceException(int statusCode, string code, string message) : Exception(message)
{
    /// <summary>HTTP 响应状态码。</summary>
    public int StatusCode { get; } = statusCode;
    /// <summary>供客户端识别的故障名称。</summary>
    public string Code { get; } = code;
}
