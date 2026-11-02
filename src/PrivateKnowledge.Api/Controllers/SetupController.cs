// 用途：给首次配置提供实际环境状态；只查元数据，不生成回答，不自动联网下载。
using Microsoft.AspNetCore.Mvc;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;

namespace PrivateKnowledge.Api.Controllers;

/// <summary>本机准备和企业连接共用的只读状态入口。</summary>
[ApiController]
public sealed class SetupController(IModelClient models, ModelOptions options, KnowledgeOptions knowledge) : ControllerBase
{
    /// <summary>检查配置中的两个模型；只有名称和本地能力都通过才显示已安装。</summary>
    /// <param name="token">客户端退出或重查时的取消信号。</param>
    /// <returns>完整示例见公开文档 docs/SETUP.md；不暴露私有问题或访问密钥。</returns>
    [HttpGet("/api/setup")]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        try
        {
            var catalog = await models.GetModelsAsync(token);
            var state = await models.CheckAsync(token);
            return Ok(new { ollamaReady = state != "unavailable", chatModel = options.ChatModel,
                chatInstalled = catalog.Models.Any(model => model.Name == options.ChatModel),
                embeddingModel = knowledge.UseEmbeddings ? options.EmbeddingModel : null,
                embeddingRequired = knowledge.UseEmbeddings,
                embeddingInstalled = !knowledge.UseEmbeddings || state is "ready" or "chat_model_missing",
                message = state == "ready" ? "模型已准备好。" : "请准备缺少的模型，或由维护者修正模型配置。" });
        }
        catch (ServiceException)
        {
            return Ok(new { ollamaReady = false, chatModel = options.ChatModel, chatInstalled = false,
                embeddingModel = knowledge.UseEmbeddings ? options.EmbeddingModel : null,
                embeddingRequired = knowledge.UseEmbeddings, embeddingInstalled = !knowledge.UseEmbeddings,
                message = "本机 Ollama 尚不可用，请安装或启动后重新检查。" });
        }
    }
}
