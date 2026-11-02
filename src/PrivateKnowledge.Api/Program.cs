// 用途：服务入口，配置依赖、HTTP 接口、错误处理与后台知识扫描。
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Questions;
using PrivateKnowledge.Api.Retrieval;
using PrivateKnowledge.Api.Storage;

namespace PrivateKnowledge.Api;

/// <summary>可独立运行的本机知识问答服务入口。</summary>
public static class Program
{
    /// <summary>启动 Web API；Ctrl+C 或 IDE 停止调试时会取消后台任务。</summary>
    /// <param name="args">命令行配置，例如 --Knowledge:RootPath D:\PrivateKnowledge。</param>
    /// <returns>服务退出时完成的任务。</returns>
    public static async Task Main(string[] args)
    {
        await using var app = CreateApp(args);
        // 只有客户端创建的子进程才监视输入管道；手动双击/IT 部署完全不受客户端生命周期影响。
        if (app.Configuration.GetValue<bool>("Desktop:Managed"))
            _ = Task.Run(async () =>
            {
                try { while (await Console.In.ReadLineAsync() is { } line) { if (line == "stop") break; } }
                finally { app.Lifetime.StopApplication(); }
            });
        await app.RunAsync();
    }

    /// <summary>建立服务，可由集成测试直接启动；配置路径以程序目录为基准。</summary>
    /// <param name="args">标准 ASP.NET Core 命令行参数。</param>
    /// <param name="configureServices">测试可在启动前替换外网模型库；正式启动留空。</param>
    /// <returns>尚未启动的 WebApplication，由调用者启动并释放。</returns>
    public static WebApplication CreateApp(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ContentRootPath = AppContext.BaseDirectory, ApplicationName = typeof(Program).Assembly.FullName });
        // 编译时生成的本机路径文件只存在于 bin 中；直接双击 exe 也能读取用户编辑的源资料。
        // 再添加环境变量与命令行，保证显式部署设置和测试传入的目录优先于本机默认值。
        builder.Configuration.AddJsonFile("appsettings.Workspace.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables().AddCommandLine(args);
        // 本机原型使用控制台日志，不要求写 Windows 事件日志的系统权限。
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        var options = builder.Configuration.GetSection("Knowledge").Get<KnowledgeOptions>() ?? new();
        var models = builder.Configuration.GetSection("Model").Get<ModelOptions>() ?? new();
        var deployment = builder.Configuration.GetSection("Deployment").Get<DeploymentOptions>() ?? new();
        deployment.Validate();
        options.Validate(AppContext.BaseDirectory);
        var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5088";
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (!deployment.Enterprise && !uri.IsLoopback) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("本机模式只允许回环地址；远程监听需要显式配置 Deployment:Mode=Enterprise 和密钥。");
        builder.WebHost.UseUrls(urls);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 32 * 1024);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(models);
        builder.Services.AddSingleton<KnowledgeStore>();
        builder.Services.AddSingleton<IModelClient, OllamaClient>();
        builder.Services.AddSingleton<IModelLibrary, OllamaLibrary>();
        builder.Services.AddSingleton<ModelDownloads>();
        builder.Services.AddSingleton<KnowledgeIndexer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<KnowledgeIndexer>());
        builder.Services.AddSingleton<Retriever>();
        builder.Services.AddSingleton<QuestionService>();
        builder.Services.AddControllers().ConfigureApiBehaviorOptions(behavior =>
            behavior.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
                new ApiError(context.HttpContext.TraceIdentifier, "invalid_request", "请求 JSON 缺少必填字段或格式不正确。")));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(swagger =>
        {
            swagger.SwaggerDoc("v1", new() { Title = "本地私有知识问答服务", Version = "v1", Description = "本机原型；示例资料完全虚构。接口说明见项目 README。" });
            swagger.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, Assembly.GetExecutingAssembly().GetName().Name + ".xml"));
        });
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        // 控制台明确打印本次真正使用的位置，出现同名副本时可以立即对照，而不是猜启动目录。
        app.Logger.LogInformation("知识资料目录：{RootPath}；索引目录：{DataPath}", options.RootPath, options.DataPath);
        // 不信任转发请求头；Local 拒绝远程连接，Enterprise 对全部请求检查共享密钥。
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Knowledge-Instance"] = builder.Configuration["Desktop:InstanceId"] ?? "standalone";
            if (!deployment.Enterprise && context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            { context.Response.StatusCode = 403; return; }
            try
            {
                if (deployment.Enterprise) await deployment.AuthorizeAsync(context, _ => next());
                else await next();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            { context.Response.StatusCode = 499; }
            catch (ServiceException ex)
            {
                context.Response.StatusCode = ex.StatusCode;
                await context.Response.WriteAsJsonAsync(new ApiError(context.TraceIdentifier, ex.Code, ex.Message), context.RequestAborted);
            }
            catch (Exception ex)
            {
                app.Logger.LogError("请求 {RequestId} 失败，异常类型 {Type}。", context.TraceIdentifier, ex.GetType().Name);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new ApiError(context.TraceIdentifier, "internal_error", "服务内部处理失败，请检查服务日志。"), context.RequestAborted);
            }
        });
        app.UseSwagger();
        app.UseSwaggerUI();
        // 启动握手不等待大模型加载；模型缺失时客户端仍能显示下载入口和诊断。
        app.MapGet("/api/runtime", () => new { application = "PrivateKnowledge", knowledgeRootPath = options.RootPath });
        app.MapControllers();
        return app;
    }
}
