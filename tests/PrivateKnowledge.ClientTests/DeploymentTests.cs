// 用途：验证两种使用方式的生命周期、设置迁移与真实控件，不启动实际服务。
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Interactivity;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop;
using PrivateKnowledge.Desktop.Services;
using PrivateKnowledge.Desktop.ViewModels;
using Xunit;

namespace PrivateKnowledge.ClientTests;

/// <summary>把服务拥有权作为可观察的测试依赖，避免企业关窗误停服务器。</summary>
public sealed class DeploymentTests
{
    /// <summary>本机首次打开自动准备；编辑模式不影响当前连接，保存后才切换。</summary>
    [Fact]
    public async Task ModeIsAppliedOnlyAfterSavingAndSecretsStayInMemory()
    {
        using var fixture = new VmFixture();
        var environment = new FakeEnvironment();
        using var vm = new MainViewModel(fixture.Store, _ => fixture.Api, environment);
        await vm.InitializeAsync(); Assert.Equal(1, environment.Prepared); Assert.True(vm.IsLocalMode);
        vm.Question = "旧问题"; await vm.SendAsync();
        vm.ModeIndex = 1; Assert.True(vm.IsLocalMode); Assert.True(vm.CanManageModels);
        vm.EnterpriseAddress = "https://knowledge.example/"; vm.EnterpriseKey = new string('k', 40);
        await vm.ConnectAsync();
        Assert.False(vm.IsLocalMode); Assert.False(vm.CanManageModels); Assert.Empty(vm.Turns);
        Assert.Equal(1, environment.Stopped); Assert.Equal("Enterprise", fixture.Store.Load().Mode);
        Assert.DoesNotContain(vm.EnterpriseKey, File.ReadAllText(fixture.Store.FilePath));
        await vm.ShutdownAsync(); Assert.Equal(1, environment.Prepared);
    }

    /// <summary>企业客户端打开和关窗不会请求启动本机环境；Stop 仅清理自己可能拥有的旧进程。</summary>
    [Fact]
    public async Task EnterpriseInitializationNeverStartsLocalProcess()
    {
        using var fixture = new VmFixture();
        fixture.Store.Save(new(Mode: "Enterprise", EnterpriseAddress: "http://192.168.1.50:5088/"));
        var environment = new FakeEnvironment();
        using var vm = new MainViewModel(fixture.Store, _ => fixture.Api, environment) { EnterpriseKey = new string('x', 40) };
        await vm.InitializeAsync(); Assert.Equal(0, environment.Prepared); Assert.False(vm.IsLocalMode);
        await vm.ShutdownAsync(); Assert.Equal(0, environment.Prepared);
    }

    /// <summary>旧版 JSON 只有两个字段时仍能按原本机地址恢复；不会丢失上次项目。</summary>
    [Fact]
    public async Task LegacySettingsMigrateWithoutLosingProject()
    {
        using var fixture = new VmFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Store.FilePath)!);
        await File.WriteAllTextAsync(fixture.Store.FilePath, """{"ServiceAddress":"http://127.0.0.1:5099/","LastKnowledgeBaseId":"b"}""");
        using var vm = new MainViewModel(fixture.Store, _ => fixture.Api, new FakeEnvironment());
        await vm.InitializeAsync(); Assert.Equal("b", vm.SelectedBase!.Id); Assert.Equal("http://127.0.0.1:5099/", vm.AppliedServiceAddress);
    }

    /// <summary>远程地址必须显式选择企业；绝不接受带用户名、路径或查询字符串的目标。</summary>
    [Fact]
    public void RemoteAddressRequiresExplicitEnterpriseSelection()
    {
        Assert.Throws<KnowledgeApiException>(() => KnowledgeApiClient.ValidateAddress("http://192.168.1.50:5088"));
        Assert.Equal("https://knowledge.example/", KnowledgeApiClient.ValidateAddress("https://knowledge.example", true).AbsoluteUri);
        foreach (var value in new[] { "https://u:p@knowledge.example", "https://knowledge.example/path", "https://knowledge.example/?key=secret" })
            Assert.Throws<KnowledgeApiException>(() => KnowledgeApiClient.ValidateAddress(value, true));
    }

    /// <summary>真实 XAML 切换设置后，个人下载入口隐藏，资料位置改为管理员维护说明。</summary>
    [AvaloniaFact]
    public async Task EnterpriseBindingsHideLocalManagement()
    {
        using var fixture = new VmFixture(); var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            fixture.Vm.SettingsVisible = true;
            window.FindControl<Button>("EnterpriseModeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Vm.EnterpriseSettingsVisible);
            fixture.Vm.EnterpriseAddress = "https://knowledge.example/"; fixture.Vm.EnterpriseKey = new string('z', 40);
            await fixture.Vm.ConnectAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Button>("DownloadModelsButton")!.IsVisible);
            Assert.Contains("IT", window.FindControl<SelectableTextBlock>("KnowledgeLocationLabel")!.Text);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TestResults"));
            // 全新拉取源码时目录尚不存在；每个截图测试自己创建，不能依赖其他用例先运行。
            Directory.CreateDirectory(output);
            fixture.Vm.SettingsVisible = true; Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "client-enterprise-settings.png"));
        }
        finally { window.Close(); }
    }
}

/// <summary>明确的进程管理测试桩；只累计调用，不启动或停止真实程序。</summary>
internal sealed class FakeEnvironment : ILocalEnvironment
{
    /// <summary>本机准备请求次数。</summary>
    public int Prepared { get; private set; }
    /// <summary>只清理拥有进程的请求次数。</summary>
    public int Stopped { get; private set; }
    /// <inheritdoc />
    public string Description => "测试桩：本机环境已准备。";
    /// <inheritdoc />
    public Task PrepareAsync(string address, string? knowledgePath, CancellationToken token) { Prepared++; return Task.CompletedTask; }
    /// <inheritdoc />
    public Task StopAsync() { Stopped++; return Task.CompletedTask; }
    /// <inheritdoc />
    public void Dispose() { }
}
