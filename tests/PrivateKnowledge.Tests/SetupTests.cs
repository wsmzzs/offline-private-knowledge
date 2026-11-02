// 用途：检查准备接口的实际状态和固定用途下载，模型协议为明确模拟，不会下载真实文件。
using System.Net;
using System.Net.Http.Json;
using System.Text;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Models;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>首次配置不能把仅向量模型当聊天，也不能接受用户任意下载目标。</summary>
public sealed class SetupTests
{
    /// <summary>准备接口通过真实 HTTP 检查模型用途，关闭向量时无需额外模型。</summary>
    [Fact]
    public async Task SetupStatusReflectsConfiguredModels()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        using var status = await fixture.Client.GetAsync("api/setup");
        status.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("chatInstalled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("embeddingRequired").GetBoolean());
    }

    /// <summary>检索准备只拉取服务配置名称，并要求 embedding 能力，不能用 completion 冒充。</summary>
    /// <param name="capability">模拟实际模型能力。</param><param name="shouldPass">是否应允许成功。</param>
    [Theory]
    [InlineData("embedding", true)]
    [InlineData("completion", false)]
    public async Task SetupEmbeddingDownloadChecksCorrectCapability(string capability, bool shouldPass)
    {
        using var downloads = new ModelDownloads(new ModelOptions { EmbeddingModel = "qwen3-embedding:0.6b" }, new TestModelLibrary(),
            new SetupHandler(capability));
        var values = new List<DownloadProgress>();
        async Task Collect() { await foreach (var item in downloads.PullSetupAsync("embedding", default)) values.Add(item); }
        if (shouldPass) { await Collect(); Assert.Equal("success", values.Last().Status); }
        else await Assert.ThrowsAsync<PrivateKnowledge.Api.Contracts.ServiceException>(Collect);
    }
}

/// <summary>仅响应指定配置模型，任何任意 URL/名称都会使本测试失败。</summary>
/// <param name="capability">模拟的本地能力。</param>
internal sealed class SetupHandler(string capability) : HttpMessageHandler
{
    /// <summary>记录并核对请求模型；返回手写下载进度或模型元数据。</summary>
    /// <param name="request">真实适配器请求。</param><param name="cancellationToken">取消信号。</param><returns>人工协议响应。</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Assert.Equal("qwen3-embedding:0.6b", json.RootElement.GetProperty("model").GetString());
        var text = request.RequestUri!.AbsolutePath == "/api/pull" ? "{\"status\":\"success\"}\n" : "{\"capabilities\":[\"" + capability + "\"]}";
        return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
