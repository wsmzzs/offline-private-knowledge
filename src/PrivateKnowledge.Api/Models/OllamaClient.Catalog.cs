// 用途：读取 Ollama 已安装模型的元数据，为界面提供经过本地性和聊天能力检查的选项。
using System.Net.Http.Json;
using System.Text.Json;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Models;

public sealed partial class OllamaClient
{
    /// <inheritdoc />
    public async Task<ModelCatalog> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var response = await _http.GetAsync("api/tags", deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var names = json.RootElement.GetProperty("models").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct().ToArray();
            // 只读取元数据，最多同时查四项，避免模型较多时挤占本机连接。
            using var slots = new SemaphoreSlim(4);
            var results = await Task.WhenAll(names.Select(async name =>
            {
                await slots.WaitAsync(deadline.Token);
                try { return await ReadChatModelAsync(name, deadline.Token); }
                finally { slots.Release(); }
            }));
            return new ModelCatalog(_options.ChatModel, results.OfType<ChatModelInfo>()
                .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ServiceException(503, "model_catalog_unavailable", "无法读取本地模型列表，请确认 Ollama 已启动后刷新状态。"); }
    }

    /// <summary>核对单个模型的能力和来源；只凭名称判断可能把云端别名误认为本地模型。</summary>
    /// <param name="name">来自 api/tags 的已安装名称，例如 qwen3:1.7b。</param>
    /// <param name="token">整个列表查询共享的六秒期限或用户取消信号。</param>
    /// <returns>本地聊天模型信息；云端、纯嵌入或已删除模型返回 null。</returns>
    private async Task<ChatModelInfo?> ReadChatModelAsync(string name, CancellationToken token)
    {
        if (name.Contains("cloud", StringComparison.OrdinalIgnoreCase)) return null;
        using var response = await _http.PostAsJsonAsync("api/show", new { model = name }, token);
        // 列表读取后模型可能被删除；忽略这一项，其余模型仍然可以选择。
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (HasRemoteValue(root, "remote_model") || HasRemoteValue(root, "remote_host")) return null;
        if (!root.TryGetProperty("capabilities", out var capabilities)) return null;
        var values = capabilities.EnumerateArray().Select(item => item.GetString()).ToHashSet();
        return values.Contains("completion") ? new ChatModelInfo(name, values.Contains("thinking")) : null;
    }

    /// <summary>判断元数据是否声明了远程模型；缺失、null 和空字符串都表示未声明。</summary>
    /// <param name="root">api/show 返回的模型元数据。</param>
    /// <param name="property">固定字段 remote_model 或 remote_host。</param>
    /// <returns>存在非空远程值时为 true。</returns>
    private static bool HasRemoteValue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null &&
        !string.IsNullOrWhiteSpace(value.GetString());
}
