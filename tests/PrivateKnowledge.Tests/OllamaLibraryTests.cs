// 用途：用明确的模拟官方 HTML 验证分页、缓存、格式变化与离线模型筛选，不访问外网。
using System.Net;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>官网适配器的边界测试；页面结构依据本轮读取的官网搜索卡片与版本链接。</summary>
public sealed class OllamaLibraryTests
{
    /// <summary>前四批复用同一官网页，第五批才请求第二页；搜索词和 HX 分页请求头正确。</summary>
    [Fact]
    public async Task FiveItemsPerBatchReuseUpstreamPageAndAdvance()
    {
        var calls = new List<Uri>();
        using var library = new OllamaLibrary(new DownloadHandler((request, _) =>
        {
            Assert.Equal("ollama.com", request.RequestUri!.Host);
            Assert.Equal("true", request.Headers.GetValues("HX-Request").Single());
            calls.Add(request.RequestUri);
            return Task.FromResult(Html(SearchHtml(request.RequestUri.Query.Contains("page=2") ? 20 : 0, true)));
        }));
        var names = new List<string>(); int? next = 0;
        for (var i = 0; i < 5; i++)
        {
            var page = await library.SearchAsync("qwen & 测试", next!.Value, default);
            Assert.Equal(5, page.Items.Count); next = page.NextOffset;
            names.AddRange(page.Items.Select(item => item.Name));
        }
        Assert.Equal(25, names.Distinct().Count()); Assert.Equal(2, calls.Count);
        Assert.Contains("qwen & 测试", Uri.UnescapeDataString(calls[0].Query));
    }

    /// <summary>官网错误不清除缓存；超过十分钟时明确标记旧数据，恢复后又能刷新。</summary>
    [Fact]
    public async Task ExpiredCacheIsExplicitDuringOutageAndCanRecover()
    {
        var clock = new LibraryClock(); var fail = false;
        using var library = new OllamaLibrary(new DownloadHandler((_, _) =>
            fail ? throw new HttpRequestException("模拟断网") : Task.FromResult(Html(SearchHtml(0, false)))), clock);
        Assert.False((await library.SearchAsync("", 0, default)).FromCache);
        Assert.True((await library.SearchAsync("", 5, default)).FromCache);
        clock.Advance(); fail = true;
        var cached = await library.SearchAsync("", 0, default);
        Assert.True(cached.FromCache); Assert.True(cached.IsStale);
        fail = false; Assert.False((await library.SearchAsync("", 0, default)).IsStale);
    }

    /// <summary>无缓存断网报明确错误；格式错误也不能变成空列表或被缓存十分钟。</summary>
    [Fact]
    public async Task FailuresAreVisibleAndMalformedPageDoesNotPoisonCache()
    {
        var body = "<html>模拟页面改版</html>";
        using var library = new OllamaLibrary(new DownloadHandler((_, _) => Task.FromResult(Html(body))));
        Assert.Equal("library_format_changed", (await Assert.ThrowsAsync<ServiceException>(() => library.SearchAsync("", 0, default))).Code);
        body = SearchHtml(0, false);
        Assert.NotEmpty((await library.SearchAsync("", 0, default)).Items);
        using var offline = new OllamaLibrary(new DownloadHandler((_, _) => throw new HttpRequestException()));
        Assert.Equal("library_unavailable", (await Assert.ThrowsAsync<ServiceException>(() => offline.SearchAsync("", 0, default))).Code);
    }

    /// <summary>保留任意真实名称，不限制三项；过滤嵌入与分类专用系列。</summary>
    [Fact]
    public void SearchParsesCardsAndFiltersNonChatFamilies()
    {
        var html = SearchHtml(0, false) + """
            <a href="/library/embed"><h2>embed</h2><span>embedding</span><p>向量模型</p></a>
            <a href="/library/classifier"><h2>classifier</h2><span>decision</span></a>
            <a href="https://evil.example/model"><h2>恶意链接</h2></a>
            """;
        var parsed = OllamaLibraryParser.ParseSearch(html);
        Assert.Equal(20, parsed.Items.Count); Assert.False(parsed.HasMore);
        Assert.Equal("说明 & 示例", parsed.Items[0].Description);
    }

    /// <summary>同一版本重复链接只留一项；云端版本排除，文件大小来自链接中的实际文字。</summary>
    [Fact]
    public void VersionsReadSizesWithoutTurningCloudIntoOfflineModel()
    {
        var items = OllamaLibraryParser.ParseVersions("sample", """
            <a href="/library/sample:small">sample:small • 900MB • 32K context window</a>
            <a href="/library/sample:small">sample:small</a>
            <a href="/library/sample:large-q8_0">large • 12.5GB • Text input</a>
            <a href="/library/sample:cloud">cloud • 1GB</a>
            <a href="/library/other:1b">1GB</a>
            """);
        Assert.Equal(2, items.Count); Assert.Equal("900MB", items[0].Size);
        Assert.Equal("sample:large-q8_0", items[1].Name); Assert.Contains("估算", items[1].Hardware);
        Assert.Throws<ServiceException>(() => OllamaLibraryParser.ParseVersions("embed", "<span>embedding</span>"));
    }

    /// <summary>路径、URL、命令、换行和云端标签不能成为下载目标。</summary>
    /// <param name="tag">模拟的非法输入。</param>
    [Theory]
    [InlineData("https://evil.example/a")]
    [InlineData("qwen3:cloud")]
    [InlineData("qwen3:1.7b\n")]
    [InlineData("qwen3:1b;cmd")]
    [InlineData("../qwen3:1b")]
    public void InvalidTagsAreRejected(string tag) => Assert.False(OllamaLibraryParser.IsLocalTag(tag));

    /// <summary>生成二十张明确的模拟卡片，模拟官网一个分页片段。</summary>
    /// <param name="start">系列编号起点。</param>
    /// <param name="more">是否模拟下一页。</param>
    /// <returns>不含脚本的测试 HTML。</returns>
    private static string SearchHtml(int start, bool more) => "<div data-result-count='20'></div>" +
        string.Concat(Enumerable.Range(start, 20).Select(i => $"<a href='/library/model-{i}'><h2>model-{i}</h2><span>tools</span><p>说明 &amp; 示例</p></a>")) +
        (more ? "<li hx-get='/search?page=2'></li>" : "");
    /// <summary>生成模拟成功响应。</summary>
    /// <param name="html">手写测试页面。</param>
    /// <returns>由 HttpClient 负责释放的响应。</returns>
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };
}

/// <summary>测试专用快进时钟，不修改电脑时间。</summary>
internal sealed class LibraryClock : TimeProvider
{
    /// <summary>独立测试时间。</summary>
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;
    /// <summary>模拟缓存已经过了十分钟。</summary>
    public void Advance() => _now = _now.AddMinutes(11);
}
