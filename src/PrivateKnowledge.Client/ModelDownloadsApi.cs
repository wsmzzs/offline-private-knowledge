// 用途：模型下载专用协议；与问答接口分开，便于独立测试下载、取消和断线。
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PrivateKnowledge.Client;

/// <summary>官网具体模型版本；配置建议是估算，不是硬件检测结果。</summary>
/// <param name="Name">用于下载和问答的完整模型名。</param>
/// <param name="Title">中文档位名称。</param>
/// <param name="Description">适用场景。</param>
/// <param name="Size">近似下载大小。</param>
/// <param name="Hardware">内存、可选显存和磁盘的起步建议。</param>
/// <param name="SourceUrl">公开官方模型页面，不包含私有资料。</param>
public sealed record DownloadOption(string Name, string Title, string Description, string Size, string Hardware, string SourceUrl);

/// <summary>官网中的模型系列，选中后才查询具体版本。</summary>
/// <param name="Name">系列名称，例如 qwen3。</param>
/// <param name="Description">官网原始简介。</param>
/// <param name="Capabilities">官网能力标记。</param>
public sealed record LibraryFamily(string Name, string Description, string Capabilities);

/// <summary>最多五个系列的分页响应。</summary>
/// <param name="Items">当前批次。</param>
/// <param name="NextOffset">下一批位置，null 表示末尾。</param>
/// <param name="FromCache">是否命中缓存。</param>
/// <param name="IsStale">是否为官网不可用时的旧缓存。</param>
public sealed record LibraryPage(IReadOnlyList<LibraryFamily> Items, int? NextOffset, bool FromCache, bool IsStale);

/// <summary>某系列的全部本地版本；仅小型元数据，不包含模型文件。</summary>
/// <param name="Items">本地版本与配置估算。</param>
/// <param name="FromCache">是否命中缓存。</param>
/// <param name="IsStale">是否为旧缓存。</param>
public sealed record LibraryVersions(IReadOnlyList<DownloadOption> Items, bool FromCache, bool IsStale);

/// <summary>一行下载响应；Total 表示当前文件层而不是整个模型。</summary>
/// <param name="Status">downloading、checking、success 或 error。</param>
/// <param name="Message">中文状态说明。</param>
/// <param name="Completed">当前文件已接收的字节数。</param>
/// <param name="Total">当前文件总字节数，未知时为零。</param>
public sealed record DownloadProgress(string Status, string Message, long Completed = 0, long Total = 0);

/// <summary>下载窗口使用的接口，自动测试可提供假网络而不占用用户流量。</summary>
public interface IModelDownloadsApi
{
    /// <summary>读取官网模型系列，每次最多五个。</summary>
    /// <param name="query">用户搜索词。</param>
    /// <param name="offset">服务上次返回的 NextOffset，首次零。</param>
    /// <param name="token">关闭窗口时取消。</param>
    /// <returns>本批模型系列与下一批位置。</returns>
    Task<LibraryPage> GetLibraryAsync(string query, int offset, CancellationToken token);
    /// <summary>按需读取系列版本和文件大小。</summary>
    /// <param name="family">选中的系列。</param>
    /// <param name="token">关闭窗口时取消。</param>
    /// <returns>下载选项及缓存标记。</returns>
    Task<LibraryVersions> GetVersionsAsync(string family, CancellationToken token);
    /// <summary>接收逐行进度；下载成功必须看到 success，不能把连接结束当作成功。</summary>
    /// <param name="name">用户点选的完整模型名。</param>
    /// <param name="token">取消按钮或关闭窗口时传入的信号。</param>
    /// <returns>可逐条显示的进度。</returns>
    IAsyncEnumerable<DownloadProgress> DownloadAsync(string name, CancellationToken token);
}

public sealed partial class KnowledgeApiClient
{
    /// <inheritdoc />
    public async Task<LibraryPage> GetLibraryAsync(string query, int offset, CancellationToken token) =>
        await SendAsync<LibraryPage>(HttpMethod.Get, $"api/models/library?query={Uri.EscapeDataString(query)}&offset={offset}", null, 30, token);

    /// <inheritdoc />
    public async Task<LibraryVersions> GetVersionsAsync(string family, CancellationToken token) =>
        await SendAsync<LibraryVersions>(HttpMethod.Get, "api/models/download-options?family=" + Uri.EscapeDataString(family), null, 30, token);

    /// <inheritdoc />
    public async IAsyncEnumerable<DownloadProgress> DownloadAsync(string name, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var progress in DownloadCoreAsync("api/models/download", new { model = name }, token)) yield return progress;
    }

    /// <summary>普通模型管理和首次配置共用进度解析；所有请求只发送模型名或用途，不发送资料。</summary>
    /// <param name="path">代码固定的下载接口路径。</param><param name="body">模型名或 chat/embedding 用途。</param>
    /// <param name="token">用户取消或关闭窗口信号。</param><returns>经过结构核验的进度。</returns>
    private async IAsyncEnumerable<DownloadProgress> DownloadCoreAsync(string path, object body, [EnumeratorCancellation] CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(4));
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        // 服务可能先花二十秒核对官网版本，再等待 Ollama 响应头；客户端必须给两段等待留够时间。
        headers.CancelAfter(TimeSpan.FromSeconds(70));
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        if (!response.IsSuccessStatusCode)
            throw new KnowledgeApiException($"下载请求失败（HTTP {(int)response.StatusCode}），请确认问答服务已更新。", "download_failed");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            idle.CancelAfter(TimeSpan.FromMinutes(4));
            var line = await reader.ReadLineAsync(idle.Token);
            if (line is null) throw new KnowledgeApiException("下载连接提前结束，尚未收到完成确认，请重试。", "download_incomplete");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var progress = JsonSerializer.Deserialize<DownloadProgress>(line, JsonOptions)
                ?? throw new JsonException("空下载进度。");
            if (progress.Status is not ("downloading" or "checking" or "success" or "error") ||
                progress.Total < 0 || progress.Completed < 0 || progress.Completed > progress.Total)
                throw new JsonException("下载进度字段无效。");
            yield return progress;
            if (progress.Status is "success" or "error") yield break;
        }
    }
}
