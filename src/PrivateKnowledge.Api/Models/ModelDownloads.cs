// 用途：校验官网存在的本地版本，再把 Ollama 下载进度逐条交给客户端；不接触知识库和问题。
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Models;

/// <summary>给新用户看的下载选项；硬件数值是本项目估算，不能保证所有电脑都流畅。</summary>
/// <param name="Name">Ollama 完整名称，必须与官方标签一致。</param>
/// <param name="Title">界面中的中文档位名称。</param>
/// <param name="Description">适用情况及取舍。</param>
/// <param name="Size">官方页面的近似文件大小，不等于运行内存。</param>
/// <param name="Hardware">单用户、8192 上下文的起步建议。</param>
/// <param name="SourceUrl">核对模型大小与版本的官方页面。</param>
public sealed record DownloadOption(string Name, string Title, string Description, string Size, string Hardware, string SourceUrl);

/// <summary>一次下载的阶段；进度只代表当前文件层，校验结束之前不能视为已安装。</summary>
/// <param name="Status">downloading、checking、success 或 error。</param>
/// <param name="Message">面向用户的中文说明。</param>
/// <param name="Completed">当前文件已接收字节数。</param>
/// <param name="Total">当前文件总字节数；未知时为零。</param>
public sealed record DownloadProgress(string Status, string Message, long Completed = 0, long Total = 0);

/// <summary>只接受官网版本列表中的本地模型，一次下载一个；断开时释放下载连接。</summary>
public sealed class ModelDownloads : IDisposable
{
    /// <summary>本地直连 Ollama 的连接池；不通过代理转发本机请求。</summary>
    private readonly HttpClient _http;
    /// <summary>跨所有客户端限制并发，避免多个下载挤满网络和磁盘。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>动态官方模型库；读取过的版本通常会命中缓存。</summary>
    private readonly IModelLibrary _library;
    /// <summary>首次引导只能下载服务配置指定的两个模型，不能接受任意网址或远程别名。</summary>
    private readonly ModelOptions _options;

    /// <summary>创建下载连接；测试可替换网络，不会真的下载模型。</summary>
    /// <param name="options">服务配置的本机 Ollama 地址，例如 http://127.0.0.1:11434。</param>
    /// <param name="library">官网系列与版本来源；测试使用明确模拟列表。</param>
    /// <param name="handler">自动测试的模拟网络；正常部署不填写。</param>
    public ModelDownloads(ModelOptions options, IModelLibrary library, HttpMessageHandler? handler = null)
    {
        _library = library;
        _options = options;
        var uri = new Uri(options.BaseUrl);
        if (!uri.IsLoopback || uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("模型下载仅连接本机 Ollama。");
        _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>用户明确点击下载后才调用；每次 yield 把新进度交给 HTTP 接口，不等待整个文件下载完。</summary>
    /// <param name="name">来自官网版本列表的名称，例如 qwen3:1.7b。</param>
    /// <param name="token">用户取消、关窗或服务退出时触发的信号。</param>
    /// <returns>下载阶段和当前文件层进度；没有 success 的连接中断按失败处理。</returns>
    public async IAsyncEnumerable<DownloadProgress> PullAsync(string name, [EnumeratorCancellation] CancellationToken token)
    {
        if (!OllamaLibraryParser.IsLocalTag(name))
            throw new ServiceException(400, "invalid_download_model", "请选择官方版本列表中的本地模型，不接受网址、命令或云端模型。");
        // 不能只靠客户端传来的名字；服务再次确认官网确有这个本地版本，才交给 Ollama 拉取。
        var versions = await _library.GetVersionsAsync(name.Split(':')[0], token);
        if (!versions.Items.Any(option => option.Name == name))
            throw new ServiceException(400, "invalid_download_model", "该版本不在官方本地下载列表中，请重新选择。");
        await foreach (var progress in PullCoreAsync(name, "completion", token)) yield return progress;
    }

    /// <summary>首次引导下载配置中的回答或检索模型；名单来自服务配置，不使用客户端传入的模型名。</summary>
    /// <param name="role">chat 表示回答模型，embedding 表示检索模型。</param>
    /// <param name="token">取消按钮、断开连接或关服务的信号。</param>
    /// <returns>只有实际下载和本地能力检查通过才返回 success。</returns>
    public IAsyncEnumerable<DownloadProgress> PullSetupAsync(string role, CancellationToken token)
    {
        var name = role switch { "chat" => _options.ChatModel, "embedding" => _options.EmbeddingModel,
            _ => throw new ServiceException(400, "invalid_model_role", "准备模型类型只能是 chat 或 embedding。") };
        if (!OllamaLibraryParser.IsLocalTag(name))
            throw new ServiceException(400, "invalid_download_model", "服务配置的模型不是标准本地标签，请由维护者核对配置。");
        return PullCoreAsync(name, role == "embedding" ? "embedding" : "completion", token);
    }

    /// <summary>共用一个下载名额和进度解析，下载后按用途核对能力，绝不把嵌入模型列入回答模型。</summary>
    /// <param name="name">已经通过入口检查的本地模型名。</param>
    /// <param name="capability">completion 或 embedding。</param>
    /// <param name="token">本次下载的取消信号。</param>
    /// <returns>逐行下载进度。</returns>
    private async IAsyncEnumerable<DownloadProgress> PullCoreAsync(string name, string capability, [EnumeratorCancellation] CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token))
            throw new ServiceException(409, "download_busy", "已有模型正在下载，请等待完成或取消后重试。");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromHours(4));
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
            { Content = JsonContent.Create(new { model = name, stream = true }) };
            // 先拿响应头即可开始读进度，不能用默认的“等整个响应结束”模式。
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            headers.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            if (!response.IsSuccessStatusCode)
                throw new ServiceException(502, "download_failed", $"Ollama 拒绝下载（HTTP {(int)response.StatusCode}），请检查版本、网络与模型名称。");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            while (true)
            {
                // 单次超过三分钟没有任何进度就结束等待；重试时由 Ollama 复用已经下载的内容。
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                idle.CancelAfter(TimeSpan.FromMinutes(3));
                var line = await reader.ReadLineAsync(idle.Token);
                if (line is null) throw new IOException("下载连接提前结束，尚未收到完成确认，请重试。");
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new ServiceException(502, "download_failed", "Ollama 下载失败：" + error.GetString());
                var status = root.GetProperty("status").GetString() ?? "";
                if (status == "success")
                {
                    // pull 成功后仍核对本地聊天能力，云端模型或只有向量能力的模型不能进入问答选择。
                    using var check = await _http.PostAsJsonAsync("api/show", new { model = name }, idle.Token);
                    check.EnsureSuccessStatusCode();
                    using var detail = JsonDocument.Parse(await check.Content.ReadAsStringAsync(idle.Token));
                    var info = detail.RootElement;
                    if (HasRemote(info, "remote_model") || HasRemote(info, "remote_host") ||
                        !info.TryGetProperty("capabilities", out var capabilities) ||
                        !capabilities.EnumerateArray().Any(value => value.GetString() == capability))
                        throw new ServiceException(502, "invalid_download_model", "下载已结束，但模型未通过预期的本地能力检查，请核对模型用途。");
                    yield return new("success", capability == "embedding" ? "检索模型下载完成，已确认支持本地向量计算。" : "下载完成，已确认是本地聊天模型，可以选用。");
                    yield break;
                }
                var total = root.TryGetProperty("total", out var totalValue) ? totalValue.GetInt64() : 0;
                var completed = root.TryGetProperty("completed", out var doneValue) ? doneValue.GetInt64() : 0;
                if (total < 0 || completed < 0) throw new JsonException("下载进度无效。");
                var message = status switch
                {
                    "pulling manifest" => "正在获取模型信息（需要联网）……",
                    "verifying sha256 digest" => "文件已接收，正在校验完整性……",
                    "writing manifest" => "正在保存模型信息……",
                    "removing any unused layers" => "正在整理模型文件……",
                    _ => total > 0 ? "正在下载当前文件……" : "正在准备模型……"
                };
                yield return new(total > 0 ? "downloading" : "checking", message, Math.Min(completed, total), total);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>检查 Ollama 元数据中的远程标记，不能只按模型名称猜是否离线。</summary>
    /// <param name="root">api/show 正文。</param>
    /// <param name="name">remote_model 或 remote_host。</param>
    /// <returns>出现非空远程值时为 true。</returns>
    private static bool HasRemote(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(value.GetString());

    /// <summary>服务退出后释放 HTTP 连接；在途请求由服务关闭信号先取消。</summary>
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
