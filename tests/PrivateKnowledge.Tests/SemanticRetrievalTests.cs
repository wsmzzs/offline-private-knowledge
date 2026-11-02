// 用途：检查语义检索、表格上下文和输入预算；这里的向量是明确的测试数据，不代表真实模型质量。
using System.Text;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Retrieval;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>把检索可靠性与真实模型效果分开验证。</summary>
public sealed class SemanticRetrievalTests
{
    /// <summary>错误码不存在时，即使语义向量完全一样，也不能套用另一错误的处理方式。</summary>
    [Fact]
    public async Task UnknownErrorCodeDoesNotUseSimilarError()
    {
        var model = new VectorModel([1, 0]);
        var found = await new Retriever(new KnowledgeOptions { UseEmbeddings = true }, model)
            .SearchAsync(Snapshot(Chunk("error", "错误码 E102：检查表头", [1, 0])), "错误码 E999 怎么处理", default);
        Assert.Empty(found);
        Assert.False(model.QueryCalled);
    }

    /// <summary>没有相同词语也能通过向量找回；无关向量不能绕过语义门槛。</summary>
    [Fact]
    public async Task SemanticBranchFindsParaphraseWithoutKeywordOverlap()
    {
        var good = Chunk("good", "院校：示例理工学院", [1, 0]);
        var bad = Chunk("bad", "打印机维护", [0, 1]);
        var model = new VectorModel([1, 0]);
        var snapshot = Snapshot(good, bad);
        var keyword = await new Retriever(new KnowledgeOptions(), new NoModel()).SearchAsync(snapshot, "在哪里完成高等教育", default);
        Assert.Empty(keyword);
        var found = await new Retriever(new KnowledgeOptions { UseEmbeddings = true }, model).SearchAsync(snapshot, "在哪里完成高等教育", default);
        Assert.Equal("good", Assert.Single(found).Id);
        Assert.True(model.QueryCalled);
    }

    /// <summary>模型标识改变后拒绝查询旧快照，不能悄悄比较不兼容向量。</summary>
    [Fact]
    public async Task ChangedModelIdentityRejectsOldSnapshot()
    {
        var model = new VectorModel([1, 0]);
        var snapshot = Snapshot(Chunk("one", "示例正文", [1, 0])) with { Pipeline = "embedding=other-model" };
        var error = await Assert.ThrowsAsync<ServiceException>(() => new Retriever(new KnowledgeOptions { UseEmbeddings = true }, model).SearchAsync(snapshot, "问题", default));
        Assert.Equal("embedding_mismatch", error.Code);
        Assert.False(model.QueryCalled);
    }

    /// <summary>同一个片段被两路找到时提升排名，不重复发送两次。</summary>
    [Fact]
    public async Task RankFusionDeduplicatesAndRewardsBothBranches()
    {
        var both = Chunk("both", "校验失败时检查源文件编码", [.8f, .6f]);
        var semanticOnly = Chunk("semantic", "字符集必须使用 UTF-8", [1, 0]);
        var found = await new Retriever(new KnowledgeOptions { UseEmbeddings = true }, new VectorModel([1, 0]))
            .SearchAsync(Snapshot(both, semanticOnly), "校验失败", default);
        Assert.Equal("both", found[0].Id);
        Assert.Equal(2, found.Select(item => item.Id).Distinct().Count());
    }

    /// <summary>超长 Markdown 表格切成多行后，每个字段值仍带原表头，完整原文不变。</summary>
    [Fact]
    public void MarkdownTableRepeatsHeadersAndPreservesOriginal()
    {
        var text = "# 人员资料\n\n| 项目 | 信息 |\n| --- | --- |\n" +
            string.Join('\n', Enumerable.Range(0, 40).Select(i => $"| 字段{i} | 示例值{i} |"));
        var document = DocumentReader.Read("sample", "资料.md", Encoding.UTF8.GetBytes(text), 200);
        Assert.Equal(text, document.Text);
        Assert.Equal(40, document.Chunks.Count);
        Assert.All(document.Chunks, chunk =>
        {
            Assert.Contains("| 项目 | 信息 |", chunk.Text);
            Assert.Contains("表格", chunk.Section);
            Assert.InRange(chunk.Text.Length, 1, 200);
        });
    }

    /// <summary>长正文在句末切分，按顺序连接后不丢失原句；不会把普通长段直接标成文件错误。</summary>
    [Fact]
    public void LongParagraphSplitsWithoutLosingSentences()
    {
        var text = string.Concat(Enumerable.Range(0, 80).Select(i => $"第{i}步检查文件内容。"));
        var document = DocumentReader.Read("sample", "流程.txt", Encoding.UTF8.GetBytes(text), 200);
        Assert.True(document.Chunks.Count > 1);
        Assert.All(document.Chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 200));
        Assert.Equal(text, string.Concat(document.Chunks.Select(chunk => chunk.Text.Replace("\r\n", "").Replace("\n", ""))));
    }

    /// <summary>多段都有依据时按预算选完整片段，避免原来四段直接超过回答模型输入限制。</summary>
    [Fact]
    public async Task EvidenceBudgetKeepsWholeChunks()
    {
        var chunks = Enumerable.Range(0, 4).Select(i => Chunk(i.ToString(), "规格" + new string('甲', 450), null)).ToArray();
        var found = await new Retriever(new KnowledgeOptions { EvidenceMaxBytes = 2000 }, new NoModel()).SearchAsync(Snapshot(chunks), "规格", default);
        Assert.Single(found);
        Assert.Equal(chunks[0].Text, found[0].Text);
    }

    /// <summary>创建手写测试片段，不使用模型伪造业务知识。</summary>
    /// <param name="id">测试编号。</param><param name="text">虚构正文。</param><param name="vector">人工向量或 null。</param>
    /// <returns>属于同一虚构文档的片段。</returns>
    private static KnowledgeChunk Chunk(string id, string text, float[]? vector) => new(id, "document", "示例.md", "正文", text, vector);

    /// <summary>创建只用于本测试的索引。</summary>
    /// <param name="chunks">人工片段。</param><returns>带固定模型标识的快照。</returns>
    private static KnowledgeSnapshot Snapshot(params KnowledgeChunk[] chunks) => new("sample", 1, "test", "embedding=test-model",
        [new KnowledgeDocument("document", "示例.md", "hash", "测试正文", chunks.ToList())]);
}

/// <summary>返回指定问题向量的测试适配器；不会调用 Ollama。</summary>
/// <param name="vector">人工设置的问题向量。</param>
internal sealed class VectorModel(float[] vector) : IModelClient
{
    /// <summary>记录检索是否通过专用问题入口，防止遗漏查询格式处理。</summary>
    public bool QueryCalled { get; private set; }
    /// <inheritdoc />
    public Task<float[]> EmbedQueryAsync(string question, CancellationToken cancellationToken) { QueryCalled = true; return Task.FromResult(vector); }
    /// <inheritdoc />
    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("应使用专用问题入口。");
    /// <inheritdoc />
    public Task<string> EmbeddingIdentityAsync(CancellationToken cancellationToken) => Task.FromResult("test-model");
    /// <inheritdoc />
    public Task<ModelAnswer> AnswerAsync(string question, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken, string? modelName = null) => throw new NotSupportedException();
    /// <inheritdoc />
    public Task<ModelCatalog> GetModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    /// <inheritdoc />
    public Task<string> CheckAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}
