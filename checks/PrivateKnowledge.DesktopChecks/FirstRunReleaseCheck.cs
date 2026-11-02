// 用途：主动验收干净运行包的首次配置、真实本地问答与退出；只使用随包虚构资料，不下载模型。
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateKnowledge.Desktop;
using PrivateKnowledge.Desktop.Services;
using PrivateKnowledge.Desktop.ViewModels;
using Xunit;

namespace PrivateKnowledge.DesktopChecks;

/// <summary>需要本机 Ollama 已安装两种模型，故意不放进默认无模型单元测试。</summary>
public sealed class FirstRunReleaseCheck
{
    /// <summary>走真实首次配置和服务拥有权，不调用模拟回答，不覆盖用户原有设置。</summary>
    [AvaloniaFact]
    public async Task CleanPackageCompletesSetupAnswersAndStopsOwnedService()
    {
        var package = Environment.GetEnvironmentVariable("PRIVATEKNOWLEDGE_PACKAGE")
            ?? throw new InvalidOperationException("请设置 PRIVATEKNOWLEDGE_PACKAGE 为待验收运行包目录。");
        var solution = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var output = Path.Combine(solution, "TestResults", "first-run-release");
        Directory.CreateDirectory(output);
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/"; port.Stop();
        var store = new SettingsStore(Path.Combine(output, "isolated-settings.json"));
        store.Save(new(address, KnowledgePath: Path.Combine(package, "service", "SampleKnowledge"), SetupCompleted: false));
        using var vm = new MainViewModel(store, environment: new LocalEnvironment(Path.Combine(package, "service")));
        var window = new MainWindow(vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.True(vm.SetupWelcomeVisible); Assert.True(vm.SetupVisible);
            await vm.SetupNextAsync(); Assert.True(vm.SetupEnvironmentVisible);
            Assert.Contains("已安装", vm.SetupEmbeddingText);
            await vm.SetupNextAsync(); Assert.True(vm.SetupKnowledgeVisible);
            await vm.SetupNextAsync(); Assert.True(vm.SetupIndexVisible);
            await WaitAsync(async () => { await vm.RefreshAsync(); return vm.CanFinishSetup; }, 60);
            vm.FinishSetup(); Assert.False(vm.SetupVisible); Assert.True(store.Load().SetupCompleted);
            await WaitAsync(async () => { await vm.RefreshAsync(); return vm.Bases.Any(item => item.Id == "温度采集与监控软件" && item.Status == "ready"); }, 180);
            vm.SelectedBase = vm.Bases.Single(item => item.Id == "温度采集与监控软件");
            vm.Question = "设备已经连接成功，为什么没有温度曲线？";
            Assert.True(vm.CanSend, vm.ConnectionStatus);
            await vm.SendAsync();
            var turn = Assert.Single(vm.Turns);
            Assert.Contains("开始采集", turn.Answer); Assert.NotEmpty(turn.Sources);
            await vm.OpenSourceAsync(turn.Sources[0]); Assert.Contains("开始采集", vm.DocumentText);
            // 公开展示图隐藏开发机目录这一项；原问答、引用和全文均来自真实服务，资料路径验证仍使用实际值。
            window.FindControl<SelectableTextBlock>("KnowledgeLocationLabel")!.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            // 在截图前读取实际可见控件文字并留档；这样公开图片可以和本次虚构资料的问答对应核对。
            var visibleText = string.Join('\n', window.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsVisible && control.GetVisualAncestors().OfType<Control>().All(parent => parent.IsVisible))
                .Select(control => control switch
                {
                    TextBlock label => label.Text,
                    TextBox input => input.Text,
                    _ => null
                })
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            Assert.Contains("温度采集与监控软件", visibleText);
            Assert.Contains("设备已经连接成功，为什么没有温度曲线？", visibleText);
            Assert.Contains("开始采集", visibleText);
            // 可见区域不允许出现任意盘符开头的本机路径，也不应显示个人邮箱。
            Assert.DoesNotMatch(@"[A-Za-z]:[\\/]", visibleText);
            Assert.DoesNotContain("@qq.com", visibleText, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(Path.Combine(output, "public-client-visible-text.txt"), visibleText);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "public-client-answer.png"));
            vm.Question = "错误码 E999 应该怎么处理？";
            await vm.SendAsync(); Assert.Empty(vm.Turns.Last().Sources);
            window.Close(); await vm.ShutdownAsync();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(address + "api/runtime"));
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), System.Text.Json.JsonSerializer.Serialize(new
            { firstRun = "PASS", realAnswerAndCitation = "PASS", unknownErrorRefused = "PASS", ownedServiceStopped = "PASS", privateMaterialsUsed = false }));
        }
        finally { await vm.ShutdownAsync(); window.Close(); }
    }

    /// <summary>持续处理界面事件并按实际状态等待；不靠固定睡眠假定准备完成。</summary>
    /// <param name="condition">需要刷新网络并确认的条件。</param><param name="seconds">最多等待秒数。</param>
    /// <returns>条件成立后结束，超时明确失败。</returns>
    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "运行包首次配置未在期限内就绪。");
            await Task.Delay(300); Dispatcher.UIThread.RunJobs();
        }
    }
}
