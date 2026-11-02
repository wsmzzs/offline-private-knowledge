// 用途：统一调用本机模型并限制并发；不提供执行命令、联网搜索或自动下载模型的能力。
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Models;

/// <summary>模型生成的中间结果；还需由问答服务核验引用。</summary>
/// <param name="Status">answered、insufficient_evidence 或 needs_clarification。</param>
/// <param name="Answer">中文回答。</param>
/// <param name="SourceIds">模型选择的本次证据编号。</param>
/// <param name="Model">由调用器填写的实际模型名，不采信模型自己生成的名称。</param>
public sealed record ModelAnswer(string Status, string Answer, string[] SourceIds, string? Model = null);

/// <summary>可替换的模型接口；集成测试使用明确的测试服务，不伪装成真实模型。</summary>
public interface IModelClient
{
    /// <summary>根据原文回答一个完整问题。</summary>
    /// <param name="question">来自 HTTP 输入的问题。</param>
    /// <param name="chunks">已经限定知识库的证据段落。</param>
    /// <param name="cancellationToken">客户端断开时的取消信号。</param>
    /// <returns>待校验的模型回答。</returns>
    /// <param name="modelName">本次选择的模型；null 使用服务默认模型，不修改全局配置。</param>
    Task<ModelAnswer> AnswerAsync(string question, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken, string? modelName = null);
    /// <summary>读取本地聊天模型列表，只检查元数据，不生成或下载模型。</summary>
    /// <param name="cancellationToken">状态查询被取消时停止读取。</param>
    /// <returns>服务默认模型和可选的本地模型。</returns>
    Task<ModelCatalog> GetModelsAsync(CancellationToken cancellationToken);
    /// <summary>计算一段文字的向量。</summary>
    /// <param name="text">来自资料段落或本次问题。</param>
    /// <param name="cancellationToken">停止后台任务或取消请求的信号。</param>
    /// <returns>用于比较含义的有限浮点数数组。</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
    /// <summary>计算问题向量；适配器可添加检索指令，原问题仍原样交给回答模型。</summary>
    /// <param name="question">用户输入，例如“我在哪所学校读的书”。</param>
    /// <param name="cancellationToken">本次提问的取消信号。</param>
    /// <returns>与资料处于同一空间的问题向量。</returns>
    Task<float[]> EmbedQueryAsync(string question, CancellationToken cancellationToken) => EmbedAsync(question, cancellationToken);
    /// <summary>小批计算资料向量；默认实现兼容已有适配器，Ollama 实现一次发送整批。</summary>
    /// <param name="texts">包含文件名、章节和正文的片段。</param>
    /// <param name="cancellationToken">后台停止信号。</param>
    /// <returns>与输入顺序相同的向量。</returns>
    async Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var result = new List<float[]>();
        foreach (var text in texts) result.Add(await EmbedAsync(text, cancellationToken));
        return result.ToArray();
    }
    /// <summary>获取嵌入模型的实际文件摘要，模型同名更新后也能使旧向量失效。</summary>
    /// <param name="cancellationToken">后台扫描或问答的取消信号。</param>
    /// <returns>模型名称加文件摘要；无法确认时拒绝使用旧向量。</returns>
    Task<string> EmbeddingIdentityAsync(CancellationToken cancellationToken);
    /// <summary>检查模型服务及配置的模型是否存在，不生成回答。</summary>
    /// <param name="cancellationToken">本次状态请求的取消信号。</param>
    /// <returns>可供状态接口展示的文字。</returns>
    Task<string> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>通过本机 HTTP 调用 Ollama；复用连接，并把模型任务串行执行。</summary>
public sealed partial class OllamaClient : IModelClient, IDisposable
{
    /// <summary>复用 HTTP 连接，客户端销毁时释放。</summary>
    private readonly HttpClient _http;
    /// <summary>限制本机模型并发为 1；排队也受超时限制。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>排队中的前台请求数量；后台每批开始前让前台先执行。</summary>
    private int _interactiveWaiting;
    /// <summary>已验证的模型配置。</summary>
    private readonly ModelOptions _options;
    /// <summary>判断状态检查是否还应检查嵌入模型。</summary>
    private readonly KnowledgeOptions _knowledge;

    /// <summary>建立本机直连客户端，避免代理将私有资料发送到其他地址。</summary>
    /// <param name="options">模型名称和本机地址。</param>
    /// <param name="knowledge">是否使用嵌入模型的配置。</param>
    /// <param name="handler">协议测试使用的本地模拟传输；正式部署省略。</param>
    public OllamaClient(ModelOptions options, KnowledgeOptions knowledge, HttpMessageHandler? handler = null)
    {
        _options = options;
        _knowledge = knowledge;
        var uri = new Uri(options.BaseUrl);
        if (!uri.IsLoopback || uri.Scheme != "http" || !string.IsNullOrEmpty(uri.UserInfo) ||
            options.TimeoutSeconds is < 1 or > 600 || string.IsNullOrWhiteSpace(options.ChatModel) ||
            options.ChatModel.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
            options.EmbeddingModel.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
            (knowledge.UseEmbeddings && string.IsNullOrWhiteSpace(options.EmbeddingModel)) ||
            options.EmbeddingKeepAliveSeconds is < 0 or > 3600 || options.ChatKeepAliveSeconds is < 0 or > 3600)
            throw new InvalidOperationException("模型配置无效：第一版仅允许本机 HTTP 和本地模型，启用向量时须填写嵌入模型。");
        _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc />
    public async Task<ModelAnswer> AnswerAsync(string question, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken, string? modelName = null)
    {
        // 每次请求独立选择并重新核对，避免多个客户端互相改动默认配置。
        var selected = modelName ?? _options.ChatModel;
        var catalog = await GetModelsAsync(cancellationToken);
        var model = catalog.Models.FirstOrDefault(item => item.Name == selected)
            ?? throw new ServiceException(400, "invalid_chat_model", "所选模型未安装或不是本地聊天模型，请刷新模型列表后重新选择。");
        var format = new
        {
            type = "object",
            properties = new
            {
                answer = new { type = "string", minLength = 1 },
                sourceIds = new { type = "array", items = new { type = "string" } },
                status = new { type = "string", @enum = new[] { "answered", "insufficient_evidence", "needs_clarification" } }
            },
            required = new[] { "status", "answer", "sourceIds" }, additionalProperties = false
        };
        var request = new
        {
            model = model.Name, stream = false, format, keep_alive = _options.ChatKeepAliveSeconds,
            options = new { temperature = 0, num_ctx = 8192, num_predict = 800 },
            messages = new[]
            {
                new { role = "system", content = AnswerPrompt.SystemMessage },
                // 保留模型实际看到的中文；若内层 JSON 把中文全转成反斜杠 u 编号，小模型很难理解。
                new { role = "user", content = AnswerPrompt.UserMessage(question, chunks) }
            }
        };
        // 字节预算不是精确 token 数，而是给 8192 上下文保留模板和输出空间的保守限制。
        // 超限时明确失败，避免截掉前置条件后仍返回看似成功的业务答案。
        var promptBytes = request.messages.Sum(message => Encoding.UTF8.GetByteCount(message.content));
        if (promptBytes > 6000)
            throw new ServiceException(413, "context_too_large", "问题和相关资料过长，请缩短问题，或调小 TopChunks 并细分资料章节。");
        // 不支持思考的模型不发送 think 字段，兼容普通聊天模型。
        var body = JsonSerializer.SerializeToNode(request)!.AsObject();
        if (model.SupportsThinking) body["think"] = false;
        using var json = await PostAsync("api/chat", body, cancellationToken);
        try
        {
            if (!json.RootElement.GetProperty("done").GetBoolean() ||
                (json.RootElement.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length"))
                throw new JsonException("模型没有完整结束。");
            var content = json.RootElement.GetProperty("message").GetProperty("content").GetString();
            var result = JsonSerializer.Deserialize<ModelAnswer>(content ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (result ?? throw new JsonException("空回答。")) with { Model = model.Name };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ServiceException(502, "invalid_model_response", "模型没有返回完整且有效的结构化回答。"); }
    }

    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        return (await EmbedBatchAsync([text], false, cancellationToken))[0];
    }

    /// <inheritdoc />
    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await _http.GetAsync("api/tags", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var names = json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToHashSet();
            if (_knowledge.UseEmbeddings && !names.Contains(_options.EmbeddingModel)) return "embedding_model_missing";
            if (_knowledge.UseEmbeddings) await ValidateEmbeddingModelAsync(timeout.Token);
            if (!names.Contains(_options.ChatModel)) return "chat_model_missing";
            return "ready";
        }
        catch (ServiceException) { return "embedding_model_invalid"; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        { return "unavailable"; }
    }

    /// <inheritdoc />
    public async Task<string> EmbeddingIdentityAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await _http.GetAsync("api/tags", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var item = json.RootElement.GetProperty("models").EnumerateArray()
                .First(m => m.GetProperty("name").GetString() == _options.EmbeddingModel);
            var digest = item.GetProperty("digest").GetString();
            if (string.IsNullOrWhiteSpace(digest)) throw new InvalidOperationException();
            await ValidateEmbeddingModelAsync(timeout.Token);
            return _options.EmbeddingModel + ":" + digest + ":" + EmbeddingInputVersion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ServiceException(503, "embedding_unavailable", $"无法确认嵌入模型 {_options.EmbeddingModel}；请在服务所在电脑安装该本地模型，或关闭 Knowledge:UseEmbeddings 后重启。"); }
    }

    /// <summary>串行发送模型任务，取消与超时覆盖排队、请求及响应读取全过程。</summary>
    /// <param name="path">固定接口路径，例如 api/chat；不接受用户地址。</param>
    /// <param name="body">待转换为 JSON 的请求对象。</param>
    /// <param name="cancellationToken">调用者的取消信号。</param>
    /// <param name="background">资料索引为 true；前台提问为 false，前台优先。</param>
    /// <returns>已读取的 JSON 文档，调用者负责释放。</returns>
    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken, bool background = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var entered = false;
        if (!background) Interlocked.Increment(ref _interactiveWaiting);
        var counted = !background;
        try
        {
            // 不强行中断正在计算的一批；下一批开始前给已排队的提问让出名额。
            while (!entered)
            {
                await _gate.WaitAsync(timeout.Token);
                if (background && Volatile.Read(ref _interactiveWaiting) > 0)
                {
                    _gate.Release();
                    await Task.Delay(25, timeout.Token);
                }
                else entered = true;
            }
            if (counted) { Interlocked.Decrement(ref _interactiveWaiting); counted = false; }
            using var response = await _http.PostAsJsonAsync(path, body, timeout.Token);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ServiceException(504, "model_timeout", "等待本地模型超时，请稍后再试。"); }
        catch (HttpRequestException)
        { throw new ServiceException(502, "model_unavailable", "本地模型调用失败，请检查 Ollama 和模型配置。"); }
        catch (JsonException)
        { throw new ServiceException(502, "invalid_model_response", "本地模型返回的内容不是有效 JSON。"); }
        finally
        {
            // 只有成功进入的请求才释放名额，取消排队不会错误地增加并发数量。
            if (counted) Interlocked.Decrement(ref _interactiveWaiting);
            if (entered) _gate.Release();
        }
    }

    /// <summary>服务停止后释放连接与并发控制资源。</summary>
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
