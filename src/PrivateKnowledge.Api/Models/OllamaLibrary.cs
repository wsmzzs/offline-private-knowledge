// 用途：按需读取官方模型库；缓存小网页，五项分页，不预先下载任何模型或上传私有资料。
using Microsoft.Extensions.Caching.Memory;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Models;

/// <summary>官方网页适配器；固定 HTTPS 来源，有限超时与缓存，外网失败不影响本地问答接口。</summary>
public sealed class OllamaLibrary : IModelLibrary, IDisposable
{
    /// <summary>只访问官网的连接；不携带知识问答服务的密钥。</summary>
    private readonly HttpClient _http;
    /// <summary>最多缓存 128 个小网页，一天不用会清除；十分钟内直接复用。</summary>
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 128 });
    /// <summary>合并同时到达的目录请求，避免每个客户端重复访问同一页。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>缓存时间来源，测试可快进以验证过期和网络故障。</summary>
    private readonly TimeProvider _clock;
    /// <summary>缓存内容及抓取时间；旧缓存只在请求失败时作为退路。</summary>
    /// <param name="Html">官网正文。</param>
    /// <param name="FetchedAt">抓取时间，用于十分钟新鲜度判断。</param>
    private sealed record CachedPage(string Html, DateTimeOffset FetchedAt);

    /// <summary>生产环境使用官方域名；测试可传入截获请求的处理器。</summary>
    /// <param name="handler">测试模拟网络；正常部署留空。</param>
    /// <param name="clock">测试用时钟；正式使用系统时间。</param>
    public OllamaLibrary(HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri("https://ollama.com/"), Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        // 官网滚动分页要求此请求头；没有它，page=2 也可能返回完整的第一页。
        _http.DefaultRequestHeaders.Add("HX-Request", "true");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrivateKnowledge/1.0");
    }

    /// <inheritdoc />
    public async Task<LibraryPage> SearchAsync(string query, int offset, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length > 100 || offset < 0 || offset > 100000)
            throw new ServiceException(400, "invalid_library_query", "搜索词最多 100 字，分页位置必须有效。");
        // 官网通常一页二十项；同一页由缓存复用，应用只把其中五项交给界面。
        var upstreamPage = offset / 20 + 1;
        var start = offset % 20;
        var page = await ReadPageAsync($"search?c=local&q={Uri.EscapeDataString(query)}&page={upstreamPage}", token);
        var parsed = OllamaLibraryParser.ParseSearch(page.Html);
        // 过滤嵌入系列后，官网页可能不足二十项，所以续页位置明确分成“页号+页内位置”。
        var items = parsed.Items.Skip(start).Take(5).ToArray();
        int? next = start + items.Length < parsed.Items.Count ? offset + items.Length
            : parsed.HasMore ? upstreamPage * 20 : null;
        return new(items, next, page.FromCache, page.IsStale);
    }

    /// <inheritdoc />
    public async Task<LibraryVersions> GetVersionsAsync(string family, CancellationToken token)
    {
        if (!OllamaLibraryParser.IsFamily(family))
            throw new ServiceException(400, "invalid_download_model", "请选择有效的官方模型系列。");
        var page = await ReadPageAsync("library/" + family + "/tags", token);
        return new(OllamaLibraryParser.ParseVersions(family, page.Html), page.FromCache, page.IsStale);
    }

    /// <summary>缓存命中不联网；过期时尝试刷新，网站失败则显式返回旧缓存状态。</summary>
    /// <param name="path">程序构造的官网相对路径，不接受用户 URL。</param>
    /// <param name="token">客户端断开或关窗的取消信号。</param>
    /// <returns>纯 HTML 与缓存标记。</returns>
    private async Task<(string Html, bool FromCache, bool IsStale)> ReadPageAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _cache.TryGetValue<CachedPage>(path, out var cached);
            if (cached is not null && _clock.GetUtcNow() - cached.FetchedAt < TimeSpan.FromMinutes(10))
                return (cached.Html, true, false);
            try
            {
                using var response = await _http.GetAsync(path, token);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(token);
                // 页面格式不再匹配时拒绝缓存，用户重试才有机会拿到修复后的官网页面。
                if (path.StartsWith("search?", StringComparison.Ordinal)) OllamaLibraryParser.ParseSearch(html);
                else OllamaLibraryParser.ParseVersions(path.Split('/')[1], html);
                _cache.Set(path, new CachedPage(html, _clock.GetUtcNow()),
                    new MemoryCacheEntryOptions().SetSize(1).SetSlidingExpiration(TimeSpan.FromDays(1)));
                return (html, false, false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (cached is not null) return (cached.Html, true, true);
                throw new ServiceException(503, "library_unavailable", "无法读取 Ollama 官方模型库，请检查网络后重试；已安装模型的本地问答仍可使用。");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>服务退出时释放外网连接、网页缓存和请求锁。</summary>
    public void Dispose() { _http.Dispose(); _cache.Dispose(); _gate.Dispose(); }
}
