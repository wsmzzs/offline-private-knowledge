// 用途：对外提供状态、知识库、问答及引用原文接口，不接受任意磁盘路径。
using Microsoft.AspNetCore.Mvc;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Questions;
using PrivateKnowledge.Api.Storage;
using PrivateKnowledge.Api.Configuration;

namespace PrivateKnowledge.Api.Controllers;

/// <summary>客户端调用入口；模型和资料处理细节留在服务内部。</summary>
[ApiController]
public sealed class KnowledgeController(KnowledgeStore store, KnowledgeIndexer indexer, IModelClient model,
    QuestionService questions, KnowledgeOptions options, ModelOptions modelOptions) : ControllerBase
{
    /// <summary>查看服务、资料及模型就绪状态；服务进程活着不代表可以正常回答。</summary>
    /// <param name="token">请求断开时取消本次模型检查。</param>
    /// <returns>200 表示至少一个知识库及模型可用；否则 503，正文仍包含详细状态。</returns>
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken token)
    {
        var catalog = "ready";
        try { indexer.EnsureCatalogChecked(); }
        catch (ServiceException ex) { catalog = ex.Code; }
        var modelState = await model.CheckAsync(token);
        var bases = store.List();
        var ready = catalog == "ready" && modelState == "ready" && bases.Any(b => b.Status == "ready");
        return StatusCode(ready ? 200 : 503, new { status = ready ? "ready" : "degraded", catalog, model = modelState,
            // 本服务只接受本机连接；把实际资料位置交给本机界面，便于确认正在编辑的是否为同一目录。
            knowledgeRootPath = options.RootPath, scanSeconds = options.ScanSeconds,
            retrieval = new { mode = options.UseEmbeddings ? "hybrid" : "keyword", embeddingModel = options.UseEmbeddings ? modelOptions.EmbeddingModel : null,
                embeddingOnCpu = modelOptions.EmbeddingOnCpu, embeddingKeepAliveSeconds = modelOptions.EmbeddingKeepAliveSeconds },
            knowledgeBases = bases.Select(b => new { id = b.Id, status = b.Status, revision = b.Revision }) });
    }

    /// <summary>获取已发现的知识库和处理状态，包括错误和空目录。</summary>
    /// <returns>知识库数组，不包含原文或服务器绝对路径。</returns>
    [HttpGet("/api/knowledge-bases")]
    public IActionResult List() => Ok(store.List().Select(b => new { id = b.Id, status = b.Status,
        revision = b.Revision, documentCount = b.DocumentCount, message = b.Message, updatedAt = b.UpdatedAt }));

    /// <summary>提供已经安装的本地聊天模型，不自动下载模型。</summary>
    /// <param name="token">请求断开时停止元数据查询。</param>
    /// <returns>默认模型名称和可选模型列表。</returns>
    [HttpGet("/api/models")]
    public Task<ModelCatalog> Models(CancellationToken token) => model.GetModelsAsync(token);

    /// <summary>提交一个完整问题，仅在指定知识库中查找资料。</summary>
    /// <param name="request">JSON 示例：{"knowledgeBaseId":"温度采集与监控软件","question":"连接后为什么没有温度曲线？"}。</param>
    /// <param name="token">客户端取消或连接断开时传入的信号。</param>
    /// <returns>回答、资料修订号和原文出处；证据不足也是 200 的正常业务结果。</returns>
    [HttpPost("/api/questions")]
    [ProducesResponseType(typeof(QuestionResponse), 200)]
    [ProducesResponseType(typeof(ApiError), 400)]
    [ProducesResponseType(typeof(ApiError), 503)]
    public Task<QuestionResponse> Ask([FromBody] QuestionRequest request, CancellationToken token) =>
        questions.AskAsync(request, HttpContext.TraceIdentifier, token);

    /// <summary>按回答中的 documentId 查看该版本原文；失效或删除的文档返回 404。</summary>
    /// <param name="documentId">服务返回的内容编号，不能填写磁盘路径。</param>
    /// <returns>知识库、修订号、相对路径和原文；不读取请求者指定的任意文件。</returns>
    [HttpGet("/api/documents/{documentId}")]
    public IActionResult Document(string documentId)
    {
        indexer.EnsureCatalogChecked();
        var generation = indexer.Generation;
        foreach (var state in store.List().Where(b => b.Status == "ready"))
        {
            var snapshot = store.ReadSnapshot(state.Id);
            var document = snapshot?.Documents.FirstOrDefault(d => d.Id == documentId);
            if (document is null) continue;
            indexer.EnsureCatalogChecked();
            var latest = store.List().First(b => b.Id == state.Id);
            if (generation != indexer.Generation || latest.Status != "ready" || latest.Revision != snapshot!.Revision)
                throw new ServiceException(409, "knowledge_changed", "资料发生变化，请重新提问获取最新引用。");
            return Ok(new { documentId = document.Id, knowledgeBaseId = state.Id, knowledgeRevision = snapshot!.Revision,
                relativePath = document.RelativePath, text = document.Text });
        }
        throw new ServiceException(404, "document_not_found", "文档不存在、已更新或暂不可用，请重新获取引用。");
    }
}
