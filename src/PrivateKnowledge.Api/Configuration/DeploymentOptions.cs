// 用途：把本机默认运行与 IT 显式启用的企业共享运行分开；企业密钥由环境变量提供。
using System.Security.Cryptography;
using System.Text;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Configuration;

/// <summary>企业连接的最小部署边界；共享密钥代表整个实例，不提供个人账号或项目权限。</summary>
public sealed class DeploymentOptions
{
    /// <summary>Local 为默认；仅 IT 配置 Enterprise 时才接受远程连接。</summary>
    public string Mode { get; set; } = "Local";
    /// <summary>通过 Deployment__ApiKey 设置的共享密钥，不应提交到仓库。</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>是否已明确选择企业共享部署。</summary>
    public bool Enterprise => Mode == "Enterprise";

    /// <summary>启动前拒绝拼错模式和过短密钥；实际监听地址与 Host 由 IT 另行配置。</summary>
    public void Validate()
    {
        if (Mode is not ("Local" or "Enterprise")) throw new InvalidOperationException("Deployment:Mode 必须是 Local 或 Enterprise。");
        if (Enterprise && ApiKey.Length < 32) throw new InvalidOperationException("企业模式需要至少 32 字符的 Deployment__ApiKey。");
    }

    /// <summary>为全部企业接口检查密钥；阻止员工通过下载接口修改服务端模型。</summary>
    /// <param name="context">当前请求，密钥只能出现在 X-Knowledge-Key 请求头。</param>
    /// <param name="next">后续健康、资料或问答接口。</param>
    /// <returns>完成验证和后续请求的任务。</returns>
    public async Task AuthorizeAsync(HttpContext context, RequestDelegate next)
    {
        var supplied = context.Request.Headers["X-Knowledge-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey))))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new ApiError(context.TraceIdentifier, "unauthorized", "企业访问密钥无效或未填写，请联系 IT。"));
            return;
        }
        // 企业模型由 IT 在服务器维护；即使知道共享密钥，普通客户端也不能发起下载。
        if (context.Request.Path.StartsWithSegments("/api/setup/download") || context.Request.Path.StartsWithSegments("/api/models/download") || context.Request.Path.StartsWithSegments("/api/models/download-options") ||
            context.Request.Path.StartsWithSegments("/api/models/library"))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new ApiError(context.TraceIdentifier, "admin_managed", "企业模型由 IT 统一维护。"));
            return;
        }
        await next(context);
    }
}
