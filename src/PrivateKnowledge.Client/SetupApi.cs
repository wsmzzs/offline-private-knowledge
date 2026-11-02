// 用途：首次配置所需的只读状态与主动下载协议；模型名由服务配置决定。
using System.Runtime.CompilerServices;

namespace PrivateKnowledge.Client;

/// <summary>真实环境检查结果，不能用“服务进程活着”代替模型可用。</summary>
/// <param name="OllamaReady">服务所在电脑的 Ollama 是否响应。</param>
/// <param name="ChatModel">服务推荐的回答模型名称。</param>
/// <param name="ChatInstalled">默认回答模型是否已安装且通过本地聊天能力检查。</param>
/// <param name="EmbeddingModel">服务实际配置的嵌入模型；未启用时为 null。</param>
/// <param name="EmbeddingRequired">是否必须准备嵌入模型。</param>
/// <param name="EmbeddingInstalled">检索模型是否已安装并可用。</param>
/// <param name="Message">服务返回的环境说明。</param>
public sealed record SetupInfo(bool OllamaReady, string ChatModel, bool ChatInstalled, string? EmbeddingModel,
    bool EmbeddingRequired, bool EmbeddingInstalled, string Message);

/// <summary>单独列出首次配置协议，旧服务不支持时显示升级说明，不能伪装已完成。</summary>
public interface ISetupApi
{
    /// <summary>只查模型元数据，不运行生成任务。</summary>
    /// <param name="token">关闭或换连接的取消信号。</param><returns>实际环境状态。</returns>
    Task<SetupInfo> GetSetupAsync(CancellationToken token);
    /// <summary>用户主动下载配置指定模型，聊天与嵌入在服务端分别检查能力。</summary>
    /// <param name="role">chat 或 embedding。</param><param name="token">下载取消信号。</param>
    /// <returns>逐行进度，只有 success 才表示完成。</returns>
    IAsyncEnumerable<DownloadProgress> DownloadSetupAsync(string role, CancellationToken token);
}

public sealed partial class KnowledgeApiClient
{
    /// <inheritdoc />
    public Task<SetupInfo> GetSetupAsync(CancellationToken token) => SendAsync<SetupInfo>(HttpMethod.Get, "api/setup", null, 20, token);
    /// <inheritdoc />
    public async IAsyncEnumerable<DownloadProgress> DownloadSetupAsync(string role, [EnumeratorCancellation] CancellationToken token)
    {
        if (role is not ("chat" or "embedding")) throw new KnowledgeApiException("准备模型用途无效。");
        await foreach (var progress in DownloadCoreAsync("api/setup/download", new { role }, token)) yield return progress;
    }
}
