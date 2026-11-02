// 用途：模拟 Ollama 的下载流，验证取消、并发、假成功与网络异常，不下载真实文件。
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>下载服务的协议与资源释放检查。</summary>
public sealed class ModelDownloadsTests
{
    /// <summary>官网新增系列只要版本存在就能下载，不再受原三个模型的固定清单限制。</summary>
    [Fact]
    public async Task NewlyPublishedTagCanDownloadWithoutChangingProductCode()
    {
        using var downloads = new ModelDownloads(new ModelOptions(), new TestModelLibrary("newly-published:custom-q4"),
            new DownloadHandler(async (request, token) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("newly-published:custom-q4", body.RootElement.GetProperty("model").GetString());
                return Reply(request.RequestUri!.AbsolutePath == "/api/show" ? """{"capabilities":["completion"]}""" : "{\"status\":\"success\"}\n");
            }));
        var progress = new List<DownloadProgress>();
        await foreach (var item in downloads.PullAsync("newly-published:custom-q4", default)) progress.Add(item);
        Assert.Equal("success", progress.Last().Status);
    }
    /// <summary>真实 Web API 能列出推荐项，并把非法下载请求作为错误行返回，不调用聊天模型。</summary>
    [Fact]
    public async Task HttpCatalogAndRejectedDownloadLeaveQuestionsUsable()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var page = await fixture.Client.GetFromJsonAsync<LibraryPage>("api/models/library?query=测试&offset=0");
        Assert.Equal(5, page!.Items.Count);
        var options = await fixture.Client.GetFromJsonAsync<LibraryVersions>("api/models/download-options?family=qwen3");
        Assert.Single(options!.Items);
        using var rejected = await fixture.Client.PostAsJsonAsync("api/models/download", new { model = "unknown:cloud" });
        Assert.Equal("application/x-ndjson", rejected.Content.Headers.ContentType!.MediaType);
        var progress = JsonSerializer.Deserialize<DownloadProgress>(await rejected.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("error", progress!.Status);
        Assert.Equal("answered", (await fixture.AskAsync("alpha", "采集之前做什么？")).Status);
    }
    /// <summary>逐层进度不能提前算成功，最后必须检查本地聊天元数据。</summary>
    [Fact]
    public async Task DownloadOnlySendsModelAndConfirmsLocalCompletion()
    {
        var calls = new List<string>();
        using var downloads = Create(async (request, token) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("qwen3:1.7b", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.TryGetProperty("question", out _));
            if (request.RequestUri.AbsolutePath == "/api/show") return Reply("""{"capabilities":["completion"]}""");
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            return Reply("{\"status\":\"pulling file\",\"total\":100,\"completed\":40}\n{\"status\":\"verifying sha256 digest\"}\n{\"status\":\"success\"}\n");
        });
        var values = await Collect(downloads);
        Assert.Equal(40, values[0].Completed);
        Assert.Equal("checking", values[1].Status);
        Assert.Equal("success", values.Last().Status);
        Assert.Equal(new[] { "/api/pull", "/api/show" }, calls);
    }

    /// <summary>错误行、连接提前结束、损坏 JSON、云端元数据都不能变成下载成功。</summary>
    /// <param name="body">测试手写的流正文。</param>
    /// <param name="remote">是否故意返回远程模型。</param>
    [Theory]
    [InlineData("{\"error\":\"disk full\"}\n", false)]
    [InlineData("{\"status\":\"pulling manifest\"}\n", false)]
    [InlineData("broken json", false)]
    [InlineData("{\"status\":\"success\"}\n", true)]
    public async Task FailureNeverBecomesSuccessAndReleasesSlot(string body, bool remote)
    {
        var good = false;
        using var downloads = Create((request, _) => Task.FromResult(Reply(request.RequestUri!.AbsolutePath == "/api/show"
            ? remote && !good ? """{"capabilities":["completion"],"remote_model":"remote"}""" : """{"capabilities":["completion"]}"""
            : good ? "{\"status\":\"success\"}\n" : body)));
        await Assert.ThrowsAnyAsync<Exception>(() => Collect(downloads));
        good = true;
        Assert.Equal("success", (await Collect(downloads)).Last().Status);
    }

    /// <summary>只接受推荐名；URL、命令字符串和云模型都不会发往 Ollama。</summary>
    [Fact]
    public async Task UnknownModelNeverReachesOllama()
    {
        var count = 0;
        using var downloads = Create((_, _) => { count++; return Task.FromResult(Reply("")); });
        foreach (var name in new[] { "https://example.com/model", "qwen3:cloud", "qwen3; command", "" })
        {
            var error = await Assert.ThrowsAsync<ServiceException>(async () =>
            { await foreach (var _ in downloads.PullAsync(name, default)) { } });
            Assert.Equal("invalid_download_model", error.Code);
        }
        Assert.Equal(0, count);
    }

    /// <summary>第二个下载明确报忙；取消第一个请求后立即允许重试。</summary>
    [Fact]
    public async Task CancellationReleasesGlobalDownloadSlot()
    {
        var entered = new TaskCompletionSource();
        var hold = true;
        using var downloads = Create(async (request, token) =>
        {
            if (hold) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Reply(request.RequestUri!.AbsolutePath == "/api/show"
                ? """{"capabilities":["completion"]}""" : "{\"status\":\"success\"}\n");
        });
        using var cancel = new CancellationTokenSource();
        var pending = Collect(downloads, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("download_busy", (await Assert.ThrowsAsync<ServiceException>(() => Collect(downloads))).Code);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        hold = false;
        Assert.Equal("success", (await Collect(downloads)).Last().Status);
    }

    /// <summary>创建只连接模拟处理器的下载器。</summary>
    /// <param name="send">生成手写响应的测试函数。</param>
    /// <returns>调用者负责释放的下载器。</returns>
    private static ModelDownloads Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ModelOptions(), new TestModelLibrary(), new DownloadHandler(send));
    /// <summary>把手写 JSON 包装成响应，不访问互联网。</summary>
    /// <param name="body">一个或多个 JSON 行。</param>
    /// <returns>测试响应。</returns>
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
    /// <summary>把下载流读完，方便断言最后状态。</summary>
    /// <param name="downloads">测试实例。</param>
    /// <param name="token">可选取消信号。</param>
    /// <returns>观察到的全部阶段。</returns>
    private static async Task<List<DownloadProgress>> Collect(ModelDownloads downloads, CancellationToken token = default)
    {
        var values = new List<DownloadProgress>();
        await foreach (var value in downloads.PullAsync("qwen3:1.7b", token)) values.Add(value);
        return values;
    }
}

/// <summary>拦截测试 HTTP 请求，不把测试内容发给真实 Ollama。</summary>
/// <param name="send">当前测试定义的响应函数。</param>
internal sealed class DownloadHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

/// <summary>明确的目录测试桩；不从真实官网读取，也不代表产品的预设清单。</summary>
/// <param name="name">当前测试模拟的官网版本；可模拟尚未出现在旧清单中的新模型。</param>
internal sealed class TestModelLibrary(string name = "qwen3:1.7b") : IModelLibrary
{
    /// <inheritdoc />
    public Task<LibraryPage> SearchAsync(string query, int offset, CancellationToken token) => Task.FromResult(
        new LibraryPage(Enumerable.Range(offset, 5).Select(i => new LibraryFamily("test" + i, "模拟目录", "tools")).ToArray(), offset + 5, false, false));
    /// <inheritdoc />
    public Task<LibraryVersions> GetVersionsAsync(string family, CancellationToken token) => Task.FromResult(
        new LibraryVersions([new(name, "模拟版本", "仅测试协议", "1.4GB", "模拟估算", "https://ollama.com/library/" + name)], false, false));
}
