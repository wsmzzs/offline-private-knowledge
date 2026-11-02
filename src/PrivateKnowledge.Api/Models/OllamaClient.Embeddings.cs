// 用途：区分“问题”和“资料”的嵌入输入，小批请求、CPU 运行与驻留设置都集中在这里。
using System.Net.Http.Json;
using System.Text.Json;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Models;

public sealed partial class OllamaClient
{
    /// <summary>输入规则版本；变化后旧向量必须重新生成，不能与新问题向量混用。</summary>
    private const string EmbeddingInputVersion = "input-v2-title-query-instruct";

    /// <inheritdoc />
    public async Task<float[]> EmbedQueryAsync(string question, CancellationToken cancellationToken)
    {
        // Qwen 官方要求在查询端说明检索任务；资料端保持真实正文。其他模型不擅自套用此格式。
        var input = _options.EmbeddingModel.Contains("qwen3-embedding", StringComparison.OrdinalIgnoreCase)
            ? "Instruct: Given a question, retrieve relevant passages from the private knowledge base that answer the question.\nQuery: " + question
            : question;
        return (await EmbedBatchAsync([input], false, cancellationToken))[0];
    }

    /// <inheritdoc />
    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        EmbedBatchAsync(texts, true, cancellationToken);

    /// <summary>核对模型确实在本机且支持向量，阻止远程别名接收私有资料；只读元数据，不加载模型。</summary>
    /// <param name="token">元数据检查的超时信号。</param>
    /// <returns>检查通过后结束；缺失或能力不符时返回明确服务错误。</returns>
    private async Task ValidateEmbeddingModelAsync(CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("api/show", new { model = _options.EmbeddingModel }, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (HasRemoteValue(root, "remote_model") || HasRemoteValue(root, "remote_host") ||
            !root.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.EnumerateArray().Any(value => value.GetString() == "embedding"))
            throw new ServiceException(503, "invalid_embedding_model", "检索模型必须是已安装的本地嵌入模型，不能使用聊天模型或远程别名。");
    }

    /// <summary>一次发送一小批资料；核验数量、维数和数值，任一错误都不发布部分索引。</summary>
    /// <param name="texts">资料批次或单个问题。</param>
    /// <param name="background">资料为 true，使排队中的提问优先。</param>
    /// <param name="token">取消本次等待和计算的信号。</param>
    /// <returns>与每个输入一一对应的非零向量。</returns>
    private async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, bool background, CancellationToken token)
    {
        if (texts.Count == 0) return [];
        if (texts.Count > 16) throw new ArgumentOutOfRangeException(nameof(texts), "每批最多 16 个片段。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try { await ValidateEmbeddingModelAsync(deadline.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        { throw new ServiceException(503, "embedding_unavailable", "无法读取本地嵌入模型信息，请检查 Ollama 和服务配置。"); }
        var options = new Dictionary<string, object>();
        // num_gpu=0 仅关闭这一类请求的 GPU 分层；回答模型仍由 Ollama 按实际硬件安排。
        if (_options.EmbeddingOnCpu) options["num_gpu"] = 0;
        using var json = await PostAsync("api/embed", new { model = _options.EmbeddingModel, input = texts,
            truncate = false, keep_alive = _options.EmbeddingKeepAliveSeconds, options }, token, background);
        try
        {
            var vectors = json.RootElement.GetProperty("embeddings").Deserialize<float[][]>();
            if (vectors is null || vectors.Length != texts.Count || vectors.Any(vector => vector is null ||
                vector.Length == 0 || vector.Any(value => !float.IsFinite(value)) || vector.All(value => value == 0)) ||
                vectors.Any(vector => vector.Length != vectors[0].Length)) throw new JsonException();
            return vectors;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ServiceException(502, "invalid_embedding", "嵌入模型返回的向量数量、维数或数值无效；没有发布本批索引。"); }
    }
}
