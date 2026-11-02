// 用途：用明确的协议模拟检查批量向量、检索指令、CPU/释放参数和前台优先，不下载或运行模型。
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>模型调用与资源调度的回归检查；真实效果由 SemanticChecks 单独验证。</summary>
public sealed class EmbeddingProtocolTests
{
    /// <summary>一批资料只发一次请求；只有问题带指令，并传入 CPU 与释放参数。</summary>
    [Fact]
    public async Task BatchAndQueryUseDifferentInputsAndExplicitResourceOptions()
    {
        using var handler = new EmbeddingHandler();
        using var client = Client(handler);
        var vectors = await client.EmbedDocumentsAsync(["资料一", "资料二"], default);
        await client.EmbedQueryAsync("在哪里完成学业", default);
        Assert.Equal(2, vectors.Length);
        var requests = handler.Inputs.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(new[] { "资料一", "资料二" }, requests[0]);
        Assert.StartsWith("Instruct:", Assert.Single(requests[1]));
        Assert.EndsWith("Query: 在哪里完成学业", requests[1][0]);
        Assert.All(handler.Payloads, json =>
        {
            Assert.Equal(0, json.GetProperty("options").GetProperty("num_gpu").GetInt32());
            Assert.Equal(0, json.GetProperty("keep_alive").GetInt32());
            Assert.False(json.GetProperty("truncate").GetBoolean());
        });
    }

    /// <summary>少返回一条向量时拒绝整个批次，不让索引与原文错位。</summary>
    [Fact]
    public async Task WrongVectorCountRejectsWholeBatch()
    {
        using var handler = new EmbeddingHandler { WrongCount = true };
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<ServiceException>(() => client.EmbedDocumentsAsync(["第一行", "第二行"], default));
        Assert.Equal("invalid_embedding", error.Code);
    }

    /// <summary>本地名称指向远程模型时，在发送私有资料之前拒绝。</summary>
    [Fact]
    public async Task RemoteAliasNeverReceivesPrivateText()
    {
        using var handler = new EmbeddingHandler { Remote = true };
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<ServiceException>(() => client.EmbedDocumentsAsync(["私有资料"], default));
        Assert.Equal("invalid_embedding_model", error.Code);
        Assert.Empty(handler.Inputs);
    }

    /// <summary>第一批已开始时不打断；前台问题必须先于已排队的下一批后台资料执行。</summary>
    [Fact]
    public async Task InteractiveQueryRunsBeforeWaitingBackgroundBatch()
    {
        using var handler = new EmbeddingHandler { HoldFirst = true };
        using var client = Client(handler);
        var first = client.EmbedDocumentsAsync(["第一批"], default);
        await handler.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.EmbedDocumentsAsync(["第二批"], default);
        var query = client.EmbedQueryAsync("前台问题", default);
        handler.ReleaseFirst.TrySetResult();
        await Task.WhenAll(first, second, query).WaitAsync(TimeSpan.FromSeconds(5));
        var order = handler.Inputs.ToArray();
        Assert.Equal("第一批", order[0][0]);
        Assert.Contains("前台问题", order[1][0]);
        Assert.Equal("第二批", order[2][0]);
    }

    /// <summary>创建采用真实 Qwen 输入规则的适配器；网络替换成测试传输。</summary>
    /// <param name="handler">测试期间的确定性 HTTP 模拟。</param><returns>由测试负责释放的客户端。</returns>
    private static OllamaClient Client(EmbeddingHandler handler) => new(new ModelOptions { EmbeddingModel = "qwen3-embedding:0.6b" }, new KnowledgeOptions { UseEmbeddings = true }, handler);
}

/// <summary>只模拟元数据和向量 API；向量固定为 [1,0]，不声称具有语义理解能力。</summary>
internal sealed class EmbeddingHandler : HttpMessageHandler
{
    /// <summary>按实际执行顺序记录输入，线程安全。</summary>
    public ConcurrentQueue<string[]> Inputs { get; } = new();
    /// <summary>保留独立 JSON 副本，用于检查资源参数。</summary>
    public ConcurrentQueue<JsonElement> Payloads { get; } = new();
    /// <summary>模拟缺少一条向量。</summary>
    public bool WrongCount { get; init; }
    /// <summary>模拟远程别名。</summary>
    public bool Remote { get; init; }
    /// <summary>是否暂停第一批以验证排队顺序。</summary>
    public bool HoldFirst { get; init; }
    /// <summary>第一批到达的通知。</summary>
    public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>测试允许第一批继续执行的信号。</summary>
    public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>实际向量请求数，决定是否暂停第一批。</summary>
    private int _count;

    /// <summary>返回模拟元数据或向量；取消信号会结束受控等待。</summary>
    /// <param name="request">适配器发出的真实请求结构。</param><param name="cancellationToken">请求取消信号。</param>
    /// <returns>符合 Ollama 结构的人工 JSON。</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/api/show")
            return Reply(new { capabilities = new[] { "embedding" }, remote_model = Remote ? "cloud-alias" : null });
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var texts = json.RootElement.GetProperty("input").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Inputs.Enqueue(texts);
        Payloads.Enqueue(json.RootElement.Clone());
        if (Interlocked.Increment(ref _count) == 1 && HoldFirst)
        {
            FirstEntered.TrySetResult();
            await ReleaseFirst.Task.WaitAsync(cancellationToken);
        }
        return Reply(new { embeddings = Enumerable.Range(0, WrongCount ? 1 : texts.Length).Select(_ => new[] { 1.0f, 0.0f }).ToArray() });
    }

    /// <summary>创建独立响应，释放响应不会影响其他请求。</summary>
    /// <param name="value">手写模拟对象。</param><returns>JSON 响应。</returns>
    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
