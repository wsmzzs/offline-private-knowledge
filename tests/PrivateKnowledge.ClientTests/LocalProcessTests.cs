// 用途：真实启动配套 API 子进程，验证拥有权、目录选择与退出；不发送任何真实模型问题。
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop.Services;
using Xunit;

namespace PrivateKnowledge.ClientTests;

/// <summary>这些检查会启动真实 API，但资料完全虚构，且不请求聊天或下载模型。</summary>
public sealed class LocalProcessTests
{
    /// <summary>客户端创建的进程可以正常退出；另一个仅复用连接的管理器不能关闭它。</summary>
    [Fact]
    public async Task OwnedProcessStopsAndReusedProcessSurvives()
    {
        var service = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/PrivateKnowledge.Desktop/bin/Debug/net9.0/service"));
        // Release 测试同样使用对应构建，不偷用可能过期的 Debug 程序。
        if (AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar)) service = service.Replace("/Debug/", "/Release/").Replace("\\Debug\\", "\\Release\\");
        var root = Path.Combine(Path.GetTempPath(), "private-knowledge-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sample"));
        await File.WriteAllTextAsync(Path.Combine(root, "sample", "指南.md"), "# 测试指南\n先导入数据，再核对结果。");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"; listener.Stop();
        using var owner = new LocalEnvironment(service); using var reused = new LocalEnvironment(service);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            await owner.PrepareAsync(address, root, deadline.Token);
            Assert.Contains("当前客户端", owner.Description);
            using var runtime = JsonDocument.Parse(await http.GetStringAsync(address + "api/runtime"));
            Assert.Equal(root, runtime.RootElement.GetProperty("knowledgeRootPath").GetString());
            await reused.PrepareAsync(address, root, deadline.Token);
            Assert.Contains("复用", reused.Description);
            await reused.StopAsync();
            Assert.Contains("PrivateKnowledge", await http.GetStringAsync(address + "api/runtime"));
            await Assert.ThrowsAsync<KnowledgeApiException>(() => reused.PrepareAsync(address, Path.GetTempPath(), deadline.Token));
            // 模拟真实关窗：先 Dispose 取消连接并关闭管道，再等子进程收尾完成。
            owner.Dispose(); await owner.StopAsync();
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(address + "api/runtime"));
        }
        finally
        {
            await owner.StopAsync(); await reused.StopAsync();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private-knowledge-lifecycle")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
