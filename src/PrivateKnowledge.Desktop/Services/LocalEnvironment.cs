// 用途：只管理当前客户端自己创建的问答子进程；已有服务、企业服务器、Ollama 都不会被关闭。
using System.Diagnostics;
using System.Text.Json;
using PrivateKnowledge.Client;

namespace PrivateKnowledge.Desktop.Services;

/// <summary>让界面通过同一入口准备环境；测试可替换为不启动进程的实现。</summary>
public interface ILocalEnvironment : IDisposable
{
    /// <summary>供运行状态面板显示的真实进程来源。</summary>
    string Description { get; }
    /// <summary>检查已有本机服务，必要时启动配套程序。</summary>
    /// <param name="address">已校验的本机根地址，例如 http://127.0.0.1:5088/。</param>
    /// <param name="knowledgePath">用户选择的资料目录；空值沿用服务配置。</param>
    /// <param name="token">关窗或切换模式时取消准备。</param>
    /// <returns>HTTP 环境可访问时结束；模型和索引是否就绪由后续健康检查判断。</returns>
    Task PrepareAsync(string address, string? knowledgePath, CancellationToken token);
    /// <summary>通知自己创建的子进程退出；不按端口或进程名批量关闭程序。</summary>
    /// <returns>该子进程已经退出的任务。</returns>
    Task StopAsync();
}

/// <summary>本机服务的拥有者；持有 Process 对象才有资格发送退出信号。</summary>
public sealed class LocalEnvironment : ILocalEnvironment
{
    /// <summary>唯一受此对象管理的子进程；复用别人的服务时始终为空。</summary>
    private Process? _child;
    /// <summary>串行处理准备、停止和切换，避免重复启动两个进程。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>进程当前使用的资料目录；用于发现用户换目录后的重启需求。</summary>
    private string? _path;
    /// <summary>本次启动的服务地址；更换端口后不能把旧进程当成新连接。</summary>
    private string? _address;
    /// <summary>关闭窗口后不再接受启动任务。</summary>
    private bool _disposed;
    /// <summary>配套程序目录；正式使用客户端旁的 service，测试可以指定隔离构建目录。</summary>
    private readonly string _serviceDirectory;
    /// <inheritdoc />
    public string Description { get; private set; } = "本机环境尚未准备。";

    /// <summary>确定配套程序位置；构造时不启动进程或访问网络。</summary>
    /// <param name="serviceDirectory">不填写时使用客户端旁的 service 文件夹。</param>
    public LocalEnvironment(string? serviceDirectory = null) => _serviceDirectory = serviceDirectory ?? Path.Combine(AppContext.BaseDirectory, "service");

    /// <inheritdoc />
    public async Task PrepareAsync(string address, string? knowledgePath, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            KnowledgeApiClient.ValidateAddress(address);
            var path = string.IsNullOrWhiteSpace(knowledgePath) ? null : Path.GetFullPath(knowledgePath);
            if (path is not null && !Directory.Exists(path))
                throw new KnowledgeApiException("资料文件夹不存在，请在设置中重新选择。", "knowledge_path_missing");
            // 只重启自己拥有的进程；用户选目录不会改写别人手动启动的服务配置。
            if (_child is not null && (_path != path || _address != address)) await StopChildAsync();
            if (await ProbeAsync(address, token))
            {
                if (_child is null)
                {
                    if (path is not null)
                    {
                        using var http = NewHttp();
                        using var health = await http.GetAsync(new Uri(new Uri(address), "api/runtime"), token);
                        using var legacy = health.StatusCode == System.Net.HttpStatusCode.NotFound
                            ? await http.GetAsync(new Uri(new Uri(address), "health"), token) : null;
                        using var info = JsonDocument.Parse(await (legacy ?? health).Content.ReadAsStringAsync(token));
                        var actual = info.RootElement.TryGetProperty("knowledgeRootPath", out var root) ? root.GetString() ?? "" : "";
                        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual),
                            Path.TrimEndingDirectorySeparator(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            throw new KnowledgeApiException("已有服务正在使用另一个资料目录。请由启动它的人关闭后重试，或清空本机资料目录设置以沿用现有配置。");
                    }
                    Description = "复用已运行的本机服务；关闭客户端后它继续运行。";
                }
                return;
            }
            if (_child is not null) await StopChildAsync();
            await EnsureOllamaAsync(token);
            var folder = _serviceDirectory;
            var executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "PrivateKnowledge.Api.exe" : "PrivateKnowledge.Api");
            if (!File.Exists(executable))
                throw new KnowledgeApiException("缺少配套问答程序。请重新生成客户端，或使用完整发布包（包含 service 文件夹）。", "service_missing");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            // ArgumentList 不经过命令行拼接；含空格、中文的目录也按一个参数交给服务。
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
            start.Environment["Desktop__Managed"] = "true";
            start.Environment["Desktop__InstanceId"] = Guid.NewGuid().ToString("N");
            start.Environment["Deployment__Mode"] = "Local";
            if (path is not null)
            {
                start.Environment["Knowledge__RootPath"] = path;
                // 不往知识资料目录塞数据库；不同资料根使用独立索引，避免旧快照交叉。
                var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16];
                start.Environment["Knowledge__DataPath"] = Path.Combine(AppContext.BaseDirectory, "local-data", id);
            }
            _child = Process.Start(start) ?? throw new IOException("配套问答程序无法启动。");
            _path = path; _address = address;
            // 必须持续读取管道，防止服务日志写满后把服务卡住；不在客户端保存私有内容。
            _child.OutputDataReceived += IgnoreOutput; _child.ErrorDataReceived += IgnoreOutput;
            _child.BeginOutputReadLine(); _child.BeginErrorReadLine();
            Description = "由当前客户端启动；关闭窗口后自动停止此问答服务。Ollama 保持独立运行。";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                while (true)
                {
                    if (_child.HasExited) throw new KnowledgeApiException("问答程序启动后已退出。请检查端口占用、资料和索引目录权限。", "service_start_failed");
                    if (await ProbeAsync(address, deadline.Token, start.Environment["Desktop__InstanceId"])) return;
                    await Task.Delay(300, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { await StopChildAsync(); throw new KnowledgeApiException("本机问答环境准备超时，请检查安装与目录权限后重试。", "service_start_timeout"); }
            catch { await StopChildAsync(); throw; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>状态探测使用独立短超时，且不跟随其他网站的重定向。</summary>
    /// <returns>只用于本机环境检查的网络对象。</returns>
    private static HttpClient NewHttp() => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>即使模型缺失导致 503，也可证明问答进程已经运行；启动新进程时还核对实例编号。</summary>
    /// <param name="address">本机地址。</param>
    /// <param name="token">准备任务的取消信号。</param>
    /// <param name="instance">自己启动进程的随机编号；复用已有服务时为空。</param>
    /// <returns>确认为本项目健康协议才返回 true；其他软件占用端口时抛出明确错误。</returns>
    private static async Task<bool> ProbeAsync(string address, CancellationToken token, string? instance = null)
    {
        using var http = NewHttp();
        try
        {
            using var response = await http.GetAsync(new Uri(new Uri(address), "api/runtime"), token);
            // 旧服务也允许复用；新版使用轻量握手，完全不等待 Ollama 加载。
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && instance is null)
            {
                using var legacy = await http.GetAsync(new Uri(new Uri(address), "health"), token);
                using var legacyJson = JsonDocument.Parse(await legacy.Content.ReadAsStringAsync(token));
                return legacyJson.RootElement.TryGetProperty("catalog", out _) && legacyJson.RootElement.TryGetProperty("model", out _);
            }
            if (instance is not null && (!response.Headers.TryGetValues("X-Knowledge-Instance", out var values) || values.SingleOrDefault() != instance))
                throw new KnowledgeApiException("该端口已被另一个程序占用，请在设置中更换本机地址。", "port_in_use");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("application", out var application) || application.GetString() != "PrivateKnowledge")
                throw new KnowledgeApiException("该地址没有返回本项目的问答协议，请检查端口占用。", "port_in_use");
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (JsonException) { throw new KnowledgeApiException("端口被其他程序占用，无法读取问答环境状态。", "port_in_use"); }
    }

    /// <summary>仅在标准本机 Ollama 不可用时尝试启动已安装的程序；不安装、不下载、不关闭它。</summary>
    /// <param name="token">窗口关闭时取消检查。</param>
    /// <returns>启动请求完成的任务；Ollama 缺失最终由健康界面指引用户安装。</returns>
    private static async Task EnsureOllamaAsync(CancellationToken token)
    {
        using var http = NewHttp();
        try { using var response = await http.GetAsync("http://127.0.0.1:11434/api/version", token); if (response.IsSuccessStatusCode) return; }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(File.Exists(installed) ? installed : "ollama", "serve")
                { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (System.ComponentModel.Win32Exception) { /* 未安装时仍启动知识服务，由健康面板说明 Ollama 缺失。 */ }
    }

    /// <summary>只消耗日志管道；不把资料、问题或密钥写入客户端文件。</summary>
    /// <param name="sender">当前管理的服务进程。</param>
    /// <param name="e">一行日志，不持久化。</param>
    private static void IgnoreOutput(object sender, DataReceivedEventArgs e) { }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopChildAsync(); }
        finally { _gate.Release(); }
    }

    /// <summary>优先用标准输入通知 ASP.NET Core 正常结束，超时才终止持有的那个子进程。</summary>
    /// <returns>退出与资源释放完成的任务；调用者已经持有串行锁。</returns>
    private async Task StopChildAsync()
    {
        var process = _child; _child = null; _address = null; _path = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("stop"); process.StandardInput.Close(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                { /* Dispose 可能先关闭管道；仍须等待进程真实退出，不能提前把关窗当成收尾完成。 */ }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        finally { process.Dispose(); Description = "本次启动的本机问答程序已停止。"; }
    }

    /// <summary>取消场景的最后保障；只关闭输入管道，让子进程自行正常退出。</summary>
    public void Dispose()
    {
        _disposed = true;
        try { _child?.StandardInput.Close(); }
        catch (IOException) { }
        // 不 Dispose 锁；尚在取消收尾的 PrepareAsync 还需要释放它。
    }
}
