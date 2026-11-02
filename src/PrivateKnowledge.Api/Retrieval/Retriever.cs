// 用途：在已选定的知识库快照内检索，不允许模型自行选择其他知识库。
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;

namespace PrivateKnowledge.Api.Retrieval;

/// <summary>适合小规模本机资料的关键词与可选语义混合检索。</summary>
public sealed class Retriever(KnowledgeOptions options, IModelClient model)
{
    /// <summary>先移除常见问句成分，再拆双字词，避免“我毕、业于、哪个”等碎片稀释真正的主题。</summary>
    private static readonly string[] StopWords =
    [
        // 长词放在单字之前。例如“哪一个”先整体去掉，不留下“一”参与匹配。
        "请问", "一下", "怎么", "如何", "什么", "哪些", "哪一个", "哪一所", "哪一家", "哪个", "哪所", "哪家", "哪里",
        "是否", "可以", "需要", "应该", "这个", "那个", "我们", "你们", "他们", "她们", "它们", "本人",
        // 用空格替代，而不是直接拼接两侧文字，否则又会产生原句没有的相邻双字词。
        "我的", "你的", "他的", "她的", "它的", "我", "你", "他", "她", "它", "于", "在", "是", "的", "了", "吗", "呢", "呀"
    ];

    /// <summary>检索相关段落；没有超过证据门槛的候选时返回空集合。</summary>
    /// <param name="snapshot">调用者已经确认可用的单知识库快照。</param>
    /// <param name="question">用户本次完整问题。</param>
    /// <param name="token">客户端取消信号。</param>
    /// <returns>最多 TopChunks 个证据段落，按得分排序。</returns>
    public async Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(KnowledgeSnapshot snapshot, string question, CancellationToken token)
    {
        var terms = Terms(question);
        // 相似的错误码可能有完全不同的处置方式；用户明确指定错误码时，先核对原文确有这个编号。
        // 这是通用的格式检查，不写入任何具体项目的错误码或答案。
        var errorCodes = Regex.Matches(question, @"(?:错误码|故障码|错误代码|故障代码)\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9_.-]*)")
            .Select(match => match.Groups[1].Value.TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allChunks = snapshot.Documents.SelectMany(document => document.Chunks).ToArray();
        bool HasCode(KnowledgeChunk chunk, string code) => Regex.IsMatch(chunk.Title + "\n" + chunk.Section + "\n" + chunk.Text,
            @"(?<![A-Za-z0-9_])" + Regex.Escape(code) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (errorCodes.Any(code => !allChunks.Any(chunk => HasCode(chunk, code)))) return [];
        if (options.UseEmbeddings && !snapshot.Pipeline.EndsWith("embedding=" + await model.EmbeddingIdentityAsync(token), StringComparison.Ordinal))
            throw new ServiceException(503, "embedding_mismatch", "嵌入模型已经变化，请等待知识索引更新。");
        float[]? query = options.UseEmbeddings ? await model.EmbedQueryAsync(question, token) : null;
        var scores = new List<(KnowledgeChunk Chunk, double Keyword, double Semantic)>();
        foreach (var chunk in allChunks)
        {
            token.ThrowIfCancellationRequested();
            var text = (chunk.Title + "\n" + chunk.Section + "\n" + chunk.Text).ToLowerInvariant();
            var keyword = terms.Count == 0 ? 0 : terms.Count(term => text.Contains(term, StringComparison.Ordinal)) / (double)terms.Count;
            var semantic = query is not null && chunk.Vector is not null ? Cosine(query, chunk.Vector) : 0;
            scores.Add((chunk, keyword, semantic));
        }
        // 两路分数的量纲不同，分别排序后按名次合并（RRF），不再把原始分数各乘 0.5。
        var keywordRank = scores.Where(item => item.Keyword >= options.MinimumKeywordCoverage)
            .OrderByDescending(item => item.Keyword).ThenBy(item => item.Chunk.Id, StringComparer.Ordinal)
            .Take(options.RetrievalCandidates).Select(item => item.Chunk).ToArray();
        var semanticRank = query is null ? [] : scores.Where(item => item.Semantic >= options.MinimumSemanticScore)
            .OrderByDescending(item => item.Semantic).ThenBy(item => item.Chunk.Id, StringComparer.Ordinal)
            .Take(options.RetrievalCandidates).Select(item => item.Chunk).ToArray();
        var merged = new Dictionary<string, (KnowledgeChunk Chunk, double Score)>(StringComparer.Ordinal);
        AddRanks(keywordRank);
        AddRanks(semanticRank);
        var ranked = merged.Values.OrderByDescending(item => item.Score).ThenBy(item => item.Chunk.Id, StringComparer.Ordinal)
            .Select(item => item.Chunk).ToArray();
        var evidence = FitEvidence(ranked, question);
        return errorCodes.Any(code => !evidence.Any(chunk => HasCode(chunk, code))) ? [] : evidence;

        // 60 是排名平滑常数，不是相关性门槛；同一片段被两路找到会得到两次排名贡献。
        void AddRanks(KnowledgeChunk[] list)
        {
            for (var i = 0; i < list.Length; i++)
            {
                var chunk = list[i];
                merged.TryGetValue(chunk.Id, out var old);
                merged[chunk.Id] = (chunk, old.Score + 1.0 / (60 + i + 1));
            }
        }
    }

    /// <summary>按真实 JSON 字节预算选完整片段；不截去段落中的条件，也不把超限当成资料不存在。</summary>
    /// <param name="ranked">合并两路排名后的候选。</param>
    /// <param name="question">用于为问题和系统说明预留空间的原始问句。</param>
    /// <returns>最多 TopChunks 段；有候选但全部超限时返回明确的 413。</returns>
    private IReadOnlyList<KnowledgeChunk> FitEvidence(KnowledgeChunk[] ranked, string question)
    {
        var selected = new List<KnowledgeChunk>();
        var budget = options.EvidenceMaxBytes;
        var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        foreach (var chunk in ranked)
        {
            var candidate = selected.Append(chunk).Select((item, index) => new { id = $"S{index + 1}", title = item.Title, section = item.Section, text = item.Text });
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(candidate, jsonOptions)) > budget) continue;
            // 与真正调用回答模型使用同一个提示词/JSON 构造器，修改系统要求后也不会低估输入。
            if (AnswerPrompt.TotalBytes(question, selected.Append(chunk)) > 6000) continue;
            selected.Add(chunk);
            if (selected.Count == options.TopChunks) break;
        }
        if (ranked.Length > 0 && selected.Count == 0)
            throw new ServiceException(413, "context_too_large", "相关资料的完整片段超过本次输入预算，请缩短问题或调小 ChunkCharacters 后重新整理索引。");
        return selected;
    }

    /// <summary>去掉问句成分后生成中文相邻双字词和英文单词；仍是轻量规则，不等于理解同义句。</summary>
    /// <param name="question">例如“连接后为什么没有温度曲线”。</param>
    /// <returns>用于匹配的去重关键词集合。</returns>
    private static HashSet<string> Terms(string question)
    {
        var normalized = question.ToLowerInvariant();
        // “我毕业于哪个学校”现在保留“毕业、学校”两个主题词；门槛仍是配置值，不降低来迁就这个例子。
        // 只修改检索用的副本，交给聊天模型的仍是用户原问题，引用也仍是数据库中的原文。
        foreach (var word in StopWords) normalized = normalized.Replace(word, " ", StringComparison.Ordinal);
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(normalized, @"[\p{IsCJKUnifiedIdeographs}]+|[a-z0-9_]+"))
        {
            var value = match.Value;
            if (value[0] <= 127) terms.Add(value);
            else for (var i = 0; i < value.Length - 1; i++) terms.Add(value.Substring(i, 2));
        }
        return terms;
    }

    /// <summary>比较两个向量的方向；维数不同说明索引和查询模型不一致，必须重建。</summary>
    /// <param name="a">问题的向量。</param>
    /// <param name="b">已保存段落向量。</param>
    /// <returns>余弦相似度，不能解释为答案正确率。</returns>
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ServiceException(503, "embedding_mismatch", "查询与资料向量不兼容，请重建索引。");
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; normA += (double)a[i] * a[i]; normB += (double)b[i] * b[i]; }
        return normA == 0 || normB == 0 ? 0 : dot / Math.Sqrt(normA * normB);
    }
}
