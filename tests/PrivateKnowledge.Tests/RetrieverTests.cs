// 用途：用虚构个人档案检查中文问句的检索漏检；不把用户真实学校、经历或问题写成产品答案。
using System.Text;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Retrieval;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>先验证真实文本解析和检索，明确区别于大模型回答质量测试。</summary>
public sealed class RetrieverTests
{
    /// <summary>同一表格用不同日常问法查询，仍能找出毕业院校；原始表格文字不能被改写。</summary>
    /// <param name="question">用户可能输入的完整问句。</param>
    [Theory]
    [InlineData("我毕业于哪个学校")]
    [InlineData("请问我毕业于哪所学校")]
    [InlineData("我是哪一个学校毕业的")]
    [InlineData("我的毕业院校是什么")]
    [InlineData("我在哪所学校毕业的")]
    [InlineData("本人毕业于哪一所学校")]
    public async Task GraduationQuestionFindsExistingMarkdownTable(string question)
    {
        var snapshot = Snapshot();
        var retriever = new Retriever(new KnowledgeOptions { UseEmbeddings = false, MinimumKeywordCoverage = .2 }, new NoModel());
        var chunks = await retriever.SearchAsync(snapshot, question, default);
        Assert.Contains(chunks, chunk => chunk.Text.Contains("| 毕业院校 | 演示理工学院 |", StringComparison.Ordinal));
        Assert.All(chunks, chunk => Assert.Contains(chunk, snapshot.Documents.SelectMany(document => document.Chunks)));
    }

    /// <summary>不能把问句中的代词、疑问词当依据；完全无关问题仍返回空集合。</summary>
    /// <param name="question">没有资料依据的问法。</param>
    [Theory]
    [InlineData("请问我怎么养仓鼠")]
    [InlineData("我最喜欢的水果是什么")]
    [InlineData("我是不是可以这样呢")]
    [InlineData("你好")]
    public async Task UnrelatedAndEmptyTopicQuestionsStillHaveNoEvidence(string question)
    {
        var retriever = new Retriever(new KnowledgeOptions(), new NoModel());
        Assert.Empty(await retriever.SearchAsync(Snapshot(), question, default));
    }

    /// <summary>解析手写虚构 Markdown，走与真实资料相同的 DocumentReader 和段落拆分。</summary>
    /// <returns>仅包含示例人物资料的独立快照。</returns>
    private static KnowledgeSnapshot Snapshot()
    {
        const string markdown = "# 虚构个人档案\n\n## 基本背景\n\n| 项目 | 已确认信息 |\n| --- | --- |\n| 毕业院校 | 演示理工学院 |\n| 专业 | 软件工程 |\n| 毕业时间 | 2024年6月 |\n";
        var document = DocumentReader.Read("fictional", "个人档案.md", Encoding.UTF8.GetBytes(markdown), 1400);
        return new("fictional", 1, "test-only", "embedding=off", [document]);
    }
}

/// <summary>关键词检索无需聊天或向量；误调用任意模型方法就让测试明确失败。</summary>
internal sealed class NoModel : IModelClient
{
    /// <inheritdoc />
    public Task<ModelAnswer> AnswerAsync(string question, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken, string? modelName = null) => throw new InvalidOperationException("本测试不应调用聊天模型。");
    /// <inheritdoc />
    public Task<ModelCatalog> GetModelsAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不应查询模型。");
    /// <inheritdoc />
    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不应生成向量。");
    /// <inheritdoc />
    public Task<string> EmbeddingIdentityAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不应查询向量模型。");
    /// <inheritdoc />
    public Task<string> CheckAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("本测试不应检查模型。");
}
