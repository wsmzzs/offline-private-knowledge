// 用途：下载入口与知识问答分开；流式输出仅包含模型名称对应的下载阶段，不发送任何资料。
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;

namespace PrivateKnowledge.Api.Controllers;

/// <summary>用户选中的下载模型。</summary>
/// <param name="Model">官网版本列表中的完整标签，例如 qwen3:1.7b。</param>
public sealed record ModelDownloadRequest([Required] string Model);

/// <summary>首次配置只提交用途，不允许客户端指定任意下载地址。</summary>
/// <param name="Role">chat 或 embedding。</param>
public sealed record SetupDownloadRequest([Required] string Role);

/// <summary>提供官网分页目录、具体版本和用户主动发起的下载流。</summary>
/// <param name="downloads">所有请求共用的下载器，保证最多一个下载。</param>
/// <param name="lifetime">服务退出时用于终止下载的信号。</param>
/// <param name="library">按需读取官网模型库的来源。</param>
[ApiController]
public sealed class ModelDownloadsController(ModelDownloads downloads, IHostApplicationLifetime lifetime, IModelLibrary library) : ControllerBase
{
    /// <summary>只把搜索词交给官网，每次返回最多五个模型系列，不包含私有资料。</summary>
    /// <param name="query">搜索框内容；空串表示浏览。</param>
    /// <param name="offset">上次 NextOffset，首次为零。</param>
    /// <param name="token">断开请求时取消等待。</param>
    /// <returns>当前批次及下一批偏移，缓存退路会明确标记。</returns>
    [HttpGet("/api/models/library")]
    public Task<LibraryPage> Library(string query = "", int offset = 0, CancellationToken token = default) =>
        library.SearchAsync(query, offset, token);

    /// <summary>选中模型系列时才读取它的版本和大小，不下载权重文件。</summary>
    /// <param name="family">例如 qwen3。</param>
    /// <param name="token">关窗或请求断开时取消。</param>
    /// <returns>带文件大小和估算配置建议的本地版本。</returns>
    [HttpGet("/api/models/download-options")]
    public Task<LibraryVersions> Options(string family, CancellationToken token) => library.GetVersionsAsync(family, token);

    /// <summary>显式点击下载后开始；每行独立 JSON，客户端可边接收边显示。</summary>
    /// <param name="request">示例：{"model":"qwen3:1.7b"}，不接受任意网址。</param>
    /// <param name="token">客户端取消时连接断开，停止本次拉取。</param>
    /// <returns>以 success 或 error 结束的进度流；用户主动取消不返回完成。</returns>
    [HttpPost("/api/models/download")]
    public Task Download([FromBody] ModelDownloadRequest request, CancellationToken token) =>
        StreamAsync(() => downloads.PullAsync(request.Model, token), token);

    /// <summary>首次配置主动下载服务指定的模型，不读取或发送知识资料。</summary>
    /// <param name="request">完整示例：{"role":"embedding"}。</param>
    /// <param name="token">取消或关窗信号。</param>
    /// <returns>逐行进度，success 表示安装和能力核验均完成。</returns>
    [HttpPost("/api/setup/download")]
    public Task DownloadSetup([FromBody] SetupDownloadRequest request, CancellationToken token) =>
        StreamAsync(() => downloads.PullSetupAsync(request.Role, token), token);

    /// <summary>统一输出下载流；工厂放在 try 内，入口验证失败也会生成明确 error 行。</summary>
    /// <param name="create">已限定用途的下载流工厂。</param><param name="token">请求取消信号。</param>
    /// <returns>本次下载完成或明确失败的任务。</returns>
    private async Task StreamAsync(Func<IAsyncEnumerable<DownloadProgress>> create, CancellationToken token)
    {
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.ApplicationStopping);
        // 流已经发出时不能再让通用错误中间件改 HTTP 状态；因此统一发送明确的 error 行。
        DownloadProgress? failure = null;
        try
        {
            Response.ContentType = "application/x-ndjson; charset=utf-8";
            Response.Headers.CacheControl = "no-store";
            await foreach (var progress in create().WithCancellation(stopped.Token))
                await WriteProgressAsync(progress, stopped.Token);
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { failure = new("error", "下载等待超时，请检查网络后重试，已有内容由 Ollama 管理。"); }
        catch (ServiceException ex) { failure = new("error", ex.Message); }
        catch (HttpRequestException) { failure = new("error", "无法完成下载连接，请确认本机已安装并启动 Ollama，且能够访问模型下载网站。"); }
        catch (IOException) { failure = new("error", "下载连接中断或文件无法读写，请检查网络、磁盘空间后重试。"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { failure = new("error", "Ollama 返回的下载信息无法读取，请检查版本后重试。"); }
        if (failure is not null && !stopped.IsCancellationRequested) await WriteProgressAsync(failure, stopped.Token);
    }

    /// <summary>每条进度立即刷到网络，避免用户只在下载结束时才看到界面变化。</summary>
    /// <param name="progress">阶段、中文提示和当前文件字节数。</param>
    /// <param name="token">关闭连接时取消写入。</param>
    /// <returns>本条进度写出后完成。</returns>
    private async Task WriteProgressAsync(DownloadProgress progress, CancellationToken token)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", token);
        await Response.Body.FlushAsync(token);
    }
}
