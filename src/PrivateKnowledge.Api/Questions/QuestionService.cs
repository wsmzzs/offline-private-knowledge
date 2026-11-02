// 用途：连接目录状态、检索和模型，检查引用并拒绝更新过程中的旧答案。
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Retrieval;
using PrivateKnowledge.Api.Storage;

namespace PrivateKnowledge.Api.Questions;

/// <summary>问答用例，不访问生产数据库，也不执行模型建议。</summary>
public sealed class QuestionService(KnowledgeStore store, KnowledgeIndexer indexer, Retriever retriever, IModelClient model)
{
    /// <summary>执行一次单轮问答，返回原文出处；引用合法不等于结论一定正确。</summary>
    /// <param name="request">客户端发送的知识库标识和完整问题。</param>
    /// <param name="requestId">HTTP 请求追踪编号。</param>
    /// <param name="token">客户端断开连接时发出的取消信号。</param>
    /// <returns>回答、证据不足或澄清结果；服务故障使用 ServiceException。</returns>
    public async Task<QuestionResponse> AskAsync(QuestionRequest request, string requestId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.KnowledgeBaseId) || request.KnowledgeBaseId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 1000)
            throw new ServiceException(400, "invalid_question", "请填写知识库标识和 1～1000 字的完整问题。");
        indexer.EnsureCatalogChecked();
        var state = store.List().FirstOrDefault(b => b.Id == request.KnowledgeBaseId);
        if (state is null || state.Status == "missing") throw new ServiceException(404, "knowledge_not_found", "知识库不存在。");
        if (state.Status != "ready") throw new ServiceException(503, "knowledge_not_ready", $"知识库暂不可用：{state.Message}");
        var generation = indexer.Generation;
        var snapshot = store.ReadSnapshot(state.Id) ?? throw new ServiceException(503, "knowledge_not_ready", "知识库尚无完整索引。");
        var chunks = await retriever.SearchAsync(snapshot, request.Question, token);
        if (chunks.Count == 0)
        {
            EnsureUnchanged();
            return new QuestionResponse(requestId, "insufficient_evidence", state.Id, snapshot.Revision,
                "当前知识库中没有找到足够的相关依据，请补充相关资料或使用更明确的问题。", []);
        }
        var result = await model.AnswerAsync(request.Question, chunks, token, request.Model);
        EnsureUnchanged();
        if (string.IsNullOrWhiteSpace(result.Answer) || result.Answer.Length > 4000 || result.SourceIds is null ||
            result.Status is not ("answered" or "insufficient_evidence" or "needs_clarification"))
            throw new ServiceException(502, "invalid_model_response", "模型返回的回答格式无效。");
        var map = chunks.Select((chunk, i) => (Key: $"S{i + 1}", Chunk: chunk)).ToDictionary(x => x.Key, x => x.Chunk);
        if (result.SourceIds.Any(id => id is null || !map.ContainsKey(id)) || (result.Status == "answered" && result.SourceIds.Length == 0))
            throw new ServiceException(502, "invalid_citations", "模型引用了未提供的资料，已阻止显示该回答。");
        // 无依据时使用固定说明，避免把模型顺带生成的业务建议继续展示出来。
        // 澄清结果必须真的向用户提问；把小模型误放在此状态中的陈述/猜测也按证据不足处理。
        if (result.Status == "insufficient_evidence" || (result.Status == "needs_clarification" &&
            !result.Answer.TrimEnd().EndsWith('？') && !result.Answer.TrimEnd().EndsWith('?')))
            return new QuestionResponse(requestId, "insufficient_evidence", state.Id, snapshot.Revision,
                "当前资料不足以确认答案，请补充相关说明或联系资料维护人员。", [], result.Model);
        var citations = result.SourceIds.Distinct().Select(id => map[id])
            .Select(c => new Citation(c.DocumentId, c.Title, c.Section, c.Text)).ToArray();
        // 通道编号 CH1 不能被模型当成“1 度”的报警规则；新增带单位数值缺少原文依据时保守拒答。
        if (!EvidenceVerifier.QuantitiesSupported(result.Answer, citations))
            return new QuestionResponse(requestId, "insufficient_evidence", state.Id, snapshot.Revision,
                "回答中的具体数值未能在引用资料中核实，请补充明确规则或查看原文。", [], result.Model);
        return new QuestionResponse(requestId, result.Status, state.Id, snapshot.Revision, result.Answer.Trim(), citations, result.Model);

        // 请求等待模型期间资料也可能改变；不能把旧快照的答案当成最新答案返回。
        void EnsureUnchanged()
        {
            indexer.EnsureCatalogChecked();
            var latest = store.List().FirstOrDefault(b => b.Id == state.Id);
            if (indexer.Generation != generation || latest?.Status != "ready" || latest.Revision != snapshot.Revision)
                throw new ServiceException(409, "knowledge_changed", "回答期间资料发生变化，请重新提问。");
        }
    }
}
