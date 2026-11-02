// 用途：集中实现 HTTP 接口。界面只调用此接口，不自己拼 URL 或解析 JSON。
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PrivateKnowledge.Client;

/// <summary>界面依赖的网络操作；测试可以提供明确的测试实现。</summary>
public interface IKnowledgeApi : IDisposable
{
    /// <summary>获取知识库列表。</summary>
    /// <param name="token">窗口关闭或更换连接时的取消信号。</param>
    /// <returns>服务实际发现的项目，不能用演示数据替代。</returns>
    Task<IReadOnlyList<KnowledgeBaseInfo>> GetBasesAsync(CancellationToken token);
    /// <summary>检查目录与模型，读取 HTTP 503 中的健康详情。</summary>
    /// <param name="token">调用者的取消信号。</param>
    /// <returns>服务、目录和模型状态。</returns>
    Task<HealthInfo> GetHealthAsync(CancellationToken token);
    /// <summary>读取服务默认模型和已安装的本地聊天模型。</summary>
    /// <param name="token">窗口关闭或切换连接时的取消信号。</param>
    /// <returns>模型下拉框使用的列表，不包含云端模型。</returns>
    Task<ModelCatalog> GetModelsAsync(CancellationToken token);
    /// <summary>发送一个完整问题。</summary>
    /// <param name="request">例如温度采集项目加“连接后为什么没有温度曲线？”。</param>
    /// <param name="token">用户点取消或关闭窗口时触发。</param>
    /// <returns>完整回答及引用，不是流式文字。</returns>
    Task<AnswerResult> AskAsync(AskRequest request, CancellationToken token);
    /// <summary>通过服务编号读取引用原文。</summary>
    /// <param name="documentId">来自回答的 DocumentId，不能传文件路径。</param>
    /// <param name="token">切换引用或关闭窗口时触发。</param>
    /// <returns>当前可用原文；原文过期时明确报错。</returns>
    Task<DocumentResult> GetDocumentAsync(string documentId, CancellationToken token);
}

/// <summary>复用一个 HttpClient；默认本机，显式企业模式才允许远程地址。</summary>
public sealed partial class KnowledgeApiClient : IKnowledgeApi, IModelDownloadsApi, ISetupApi
{
    /// <summary>复用连接池，释放调用库时一起释放。</summary>
    private readonly HttpClient _http;
    /// <summary>按 Web 默认规则读写 camelCase JSON。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>建立连接配置，本方法不发送网络请求。</summary>
    /// <param name="address">设置框中的地址，例如 http://127.0.0.1:5088。</param>
    /// <param name="handler">测试时传入假网络；正常启动不填写。</param>
    /// <param name="enterprise">用户已选择企业模式时为 true；默认 false 继续限制本机。</param>
    /// <param name="apiKey">IT 提供的共享密钥，只在本次客户端内存和请求头中使用。</param>
    public KnowledgeApiClient(string address, HttpMessageHandler? handler = null, bool enterprise = false, string? apiKey = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = ValidateAddress(address, enterprise), Timeout = Timeout.InfiniteTimeSpan };
        // 企业密钥只作为请求头发送，不放进 URL 或错误日志；禁止自动跳转，避免密钥随跳转外发。
        if (enterprise && !string.IsNullOrWhiteSpace(apiKey)) _http.DefaultRequestHeaders.Add("X-Knowledge-Key", apiKey);
    }

    /// <summary>检查服务根地址，防止误把私有问题发给外部网站。</summary>
    /// <param name="address">来自设置输入框的文字。</param>
    /// <param name="enterprise">是否明确允许 IT 提供的远程 HTTP/HTTPS 地址。</param>
    /// <returns>以斜线结束的本机 HTTP 根地址。</returns>
    /// <exception cref="KnowledgeApiException">地址不是本机 HTTP 根地址。</exception>
    public static Uri ValidateAddress(string address, bool enterprise = false)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || (!enterprise && !uri.IsLoopback) ||
            (enterprise ? uri.Scheme is not ("http" or "https") : uri.Scheme != "http") || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new KnowledgeApiException(enterprise ? "请填写 IT 提供的 HTTP/HTTPS 根地址，不含账号、路径或查询参数。" : "本机模式请填写本机 HTTP 根地址，例如 http://127.0.0.1:5088。", "invalid_address");
        return uri;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeBaseInfo>> GetBasesAsync(CancellationToken token) =>
        await SendAsync<List<KnowledgeBaseInfo>>(HttpMethod.Get, "api/knowledge-bases", null, 8, token);

    /// <inheritdoc />
    public Task<HealthInfo> GetHealthAsync(CancellationToken token) =>
        SendAsync<HealthInfo>(HttpMethod.Get, "health", null, 8, token, allowUnavailable: true);

    /// <inheritdoc />
    public Task<ModelCatalog> GetModelsAsync(CancellationToken token) =>
        SendAsync<ModelCatalog>(HttpMethod.Get, "api/models", null, 8, token);

    /// <inheritdoc />
    public async Task<AnswerResult> AskAsync(AskRequest request, CancellationToken token)
    {
        var answer = await SendAsync<AnswerResult>(HttpMethod.Post, "api/questions", request, 150, token);
        // 即使服务返回 200，也检查最基本的协议，避免错误项目或空答案被画成成功。
        if (answer.KnowledgeBaseId != request.KnowledgeBaseId || string.IsNullOrWhiteSpace(answer.Answer) ||
            answer.Citations is null || answer.Status is not ("answered" or "insufficient_evidence" or "needs_clarification"))
            throw new KnowledgeApiException("服务返回的答案结构不正确，请联系维护人员。", "invalid_response");
        return answer;
    }

    /// <inheritdoc />
    public Task<DocumentResult> GetDocumentAsync(string documentId, CancellationToken token) =>
        SendAsync<DocumentResult>(HttpMethod.Get, "api/documents/" + Uri.EscapeDataString(documentId), null, 8, token);

    /// <summary>统一超时、取消、错误正文和 JSON 读取，各接口共享。</summary>
    /// <typeparam name="T">与当前接口对应的响应类型。</typeparam>
    /// <param name="method">GET 或 POST。</param>
    /// <param name="path">相对服务根地址的路径。</param>
    /// <param name="body">提问时为 AskRequest，其余接口为空。</param>
    /// <param name="seconds">总等待上限，提问 150 秒，状态查询 8 秒。</param>
    /// <param name="token">用户取消信号；与超时分开解释。</param>
    /// <param name="allowUnavailable">健康接口允许读取 503 的正常状态正文。</param>
    /// <returns>反序列化后的对象，即把 JSON 文字还原为可读取属性的 C# 数据。</returns>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, int seconds,
        CancellationToken token, bool allowUnavailable = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        try
        {
            // await 等待网络时把执行机会让回界面线程，窗口仍能拖动和点击取消。
            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && !(allowUnavailable && response.StatusCode == HttpStatusCode.ServiceUnavailable))
            {
                ErrorBody? error = null;
                try { error = JsonSerializer.Deserialize<ErrorBody>(json, JsonOptions); }
                catch (JsonException) { /* 代理或错误服务可能返回 HTML，此时使用通用说明。 */ }
                throw new KnowledgeApiException(error?.Message ?? $"服务请求失败（HTTP {(int)response.StatusCode}）。",
                    error?.Code ?? "http_error", error?.RequestId);
            }
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new JsonException("返回了空对象。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new KnowledgeApiException("等待服务超时，请确认服务和模型运行正常后重试。", "timeout"); }
        catch (HttpRequestException)
        { throw new KnowledgeApiException("无法连接问答环境，请点击重新准备，或在设置中检查连接地址。", "connection_failed"); }
        catch (JsonException)
        { throw new KnowledgeApiException("服务响应无法读取，请检查连接地址是否指向本项目的问答服务。", "invalid_response"); }
    }

    /// <summary>更换连接或关闭窗口后释放网络资源。</summary>
    public void Dispose() => _http.Dispose();
}
