// 用途：验证首次配置、继续未完成配置、企业隔离和真实 XAML；网络使用明确的测试实现。
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop;
using PrivateKnowledge.Desktop.Services;
using PrivateKnowledge.Desktop.ViewModels;
using Xunit;

namespace PrivateKnowledge.ClientTests;

/// <summary>首次配置必须由用户推进，不能开窗就自动下载或误标完成。</summary>
public sealed class SetupTests
{
    /// <summary>首次配置不启动进程、不下载；完成时才保存，下次直接进入问答。</summary>
    [Fact]
    public async Task FirstRunRequiresExplicitStepsAndCompletionPersists()
    {
        using var fixture = new VmFixture();
        fixture.Store.Save(new(SetupCompleted: false));
        var environment = new FakeEnvironment();
        using var vm = new MainViewModel(fixture.Store, _ => fixture.Api, environment);
        await vm.InitializeAsync();
        Assert.True(vm.SetupWelcomeVisible); Assert.True(vm.SetupVisible); Assert.Equal(0, environment.Prepared);
        Assert.Empty(fixture.Api.SetupDownloads);
        await vm.SetupNextAsync(); Assert.True(vm.SetupEnvironmentVisible); Assert.Equal(1, environment.Prepared);
        await vm.SetupNextAsync(); Assert.True(vm.SetupKnowledgeVisible);
        await vm.SetupNextAsync(); Assert.True(vm.SetupIndexVisible); Assert.True(vm.CanFinishSetup);
        Assert.False(fixture.Store.Load().SetupCompleted);
        vm.FinishSetup(); Assert.False(vm.SetupVisible); Assert.True(fixture.Store.Load().SetupCompleted);
        using var reopened = new MainViewModel(fixture.Store, _ => fixture.Api, environment);
        await reopened.InitializeAsync(); Assert.False(reopened.SetupVisible);
    }

    /// <summary>Ollama 未连接时停留在准备页，不允许下载或结束引导。</summary>
    [Fact]
    public async Task MissingOllamaCannotBeReportedAsReady()
    {
        using var fixture = new VmFixture();
        fixture.Store.Save(new(SetupCompleted: false));
        fixture.Api.Setup = new(false, "fake-chat", false, "fake-embed", true, false, "Ollama 未连接");
        await fixture.Vm.InitializeAsync(); await fixture.Vm.SetupNextAsync();
        Assert.Contains("未连接", fixture.Vm.SetupOllamaText);
        Assert.False(fixture.Vm.CanSetupDownloadChat); Assert.False(fixture.Vm.CanSetupDownloadEmbedding);
        await fixture.Vm.SetupNextAsync(); Assert.True(fixture.Vm.SetupEnvironmentVisible);
        fixture.Vm.FinishSetup(); Assert.True(fixture.Vm.SetupVisible);
    }

    /// <summary>检索模型缺少时必须显式下载，收到 success 后重查状态才能继续。</summary>
    [Fact]
    public async Task EmbeddingDownloadIsExplicitAndRechecksInstalledState()
    {
        using var fixture = new VmFixture();
        fixture.Store.Save(new(SetupCompleted: false));
        fixture.Api.Setup = fixture.Api.Setup with { EmbeddingInstalled = false };
        await fixture.Vm.InitializeAsync(); await fixture.Vm.SetupNextAsync();
        Assert.Empty(fixture.Api.SetupDownloads); Assert.True(fixture.Vm.CanSetupDownloadEmbedding);
        await fixture.Vm.SetupNextAsync(); Assert.True(fixture.Vm.SetupEnvironmentVisible);
        await fixture.Vm.DownloadSetupModelAsync("embedding");
        Assert.Equal(new[] { "embedding" }, fixture.Api.SetupDownloads);
        Assert.Contains("已安装", fixture.Vm.SetupEmbeddingText);
        await fixture.Vm.SetupNextAsync(); Assert.True(fixture.Vm.SetupKnowledgeVisible);
    }

    /// <summary>企业首次配置只连接 IT 地址；本地环境永远不收到 Prepare 或下载请求。</summary>
    [Fact]
    public async Task EnterpriseSetupSkipsLocalModelsAndFolderMaintenance()
    {
        using var fixture = new VmFixture(); fixture.Store.Save(new(SetupCompleted: false));
        var environment = new FakeEnvironment();
        using var vm = new MainViewModel(fixture.Store, _ => fixture.Api, environment);
        await vm.InitializeAsync(); vm.ModeIndex = 1;
        vm.EnterpriseAddress = "https://knowledge.example/"; vm.EnterpriseKey = new string('k', 40);
        await vm.SetupNextAsync(); Assert.Equal(0, environment.Prepared);
        Assert.False(vm.CanSetupDownloadChat); Assert.False(vm.CanSetupDownloadEmbedding);
        await vm.DownloadSetupModelAsync("embedding"); Assert.Empty(fixture.Api.SetupDownloads);
        await vm.SetupNextAsync(); Assert.True(vm.SetupIndexVisible); Assert.False(vm.SetupKnowledgeVisible);
        vm.FinishSetup(); Assert.DoesNotContain(vm.EnterpriseKey, File.ReadAllText(fixture.Store.FilePath));
    }

    /// <summary>检查成功后改成无效地址，不能拿旧服务的模型状态跳到下一页。</summary>
    [Fact]
    public async Task ChangedInvalidDraftInvalidatesPreviousSuccessfulCheck()
    {
        using var fixture = new VmFixture(); fixture.Store.Save(new(SetupCompleted: false));
        await fixture.Vm.InitializeAsync(); await fixture.Vm.SetupNextAsync();
        Assert.Contains("已连接", fixture.Vm.SetupOllamaText);
        fixture.Vm.ServiceAddress = "http://remote.example/";
        await fixture.Vm.SetupNextAsync();
        Assert.True(fixture.Vm.SetupEnvironmentVisible); Assert.False(fixture.Vm.CanFinishSetup);
        Assert.Equal("尚未检查", fixture.Vm.SetupOllamaText);
        fixture.Vm.FinishSetup(); Assert.False(fixture.Store.Load().SetupCompleted);
    }

    /// <summary>默认模型没装时，用户选的已有模型必须在重开后继续可用，不强迫下载默认模型。</summary>
    [Fact]
    public async Task InstalledAlternativeModelIsRememberedAcrossRestart()
    {
        using var fixture = new VmFixture(); fixture.Store.Save(new(SetupCompleted: false));
        fixture.Api.Catalog = new("missing-default", [new("fake-second", false)]);
        fixture.Api.Setup = fixture.Api.Setup with { ChatModel = "missing-default", ChatInstalled = false };
        fixture.Api.Health = fixture.Api.Health with { Model = "chat_model_missing" };
        await fixture.Vm.InitializeAsync(); await fixture.Vm.SetupNextAsync();
        Assert.Null(fixture.Vm.SelectedModel);
        fixture.Vm.SelectedModel = "fake-second";
        await fixture.Vm.SetupNextAsync(); await fixture.Vm.SetupNextAsync();
        Assert.True(fixture.Vm.CanFinishSetup); fixture.Vm.FinishSetup();
        Assert.Equal("fake-second", fixture.Store.Load().PreferredModel);
        using var reopened = new MainViewModel(fixture.Store, _ => fixture.Api);
        await reopened.InitializeAsync();
        reopened.Question = "设备连接后下一步做什么？";
        Assert.False(reopened.SetupVisible); Assert.Equal("fake-second", reopened.SelectedModel); Assert.True(reopened.CanSend);
        Assert.Empty(fixture.Api.SetupDownloads);
    }

    /// <summary>中断下载后释放操作锁；没有 success 就不能把检索模型标成已安装。</summary>
    [Fact]
    public async Task CancelledDownloadRemainsIncompleteAndUnlocksSetup()
    {
        using var fixture = new VmFixture(); fixture.Store.Save(new(SetupCompleted: false));
        fixture.Api.Setup = fixture.Api.Setup with { EmbeddingInstalled = false };
        fixture.Api.SetupDownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Vm.InitializeAsync(); await fixture.Vm.SetupNextAsync();
        var downloading = fixture.Vm.DownloadSetupModelAsync("embedding");
        Assert.True(fixture.Vm.IsSetupDownloading); Assert.False(fixture.Vm.CanSetupAction);
        fixture.Vm.CancelSetupDownload(); await downloading;
        Assert.False(fixture.Vm.IsSetupDownloading); Assert.True(fixture.Vm.CanSetupAction);
        Assert.Contains("未安装", fixture.Vm.SetupEmbeddingText); Assert.Contains("取消", fixture.Vm.SetupMessage);
        Assert.False(fixture.Vm.CanFinishSetup);
    }

    /// <summary>真实页面的按钮能推进引导；截图明确使用虚构协议状态，不冒充真实模型结果。</summary>
    [AvaloniaFact]
    public async Task RealSetupPageButtonsAndStatusBindingsWork()
    {
        using var fixture = new VmFixture(); fixture.Store.Save(new(SetupCompleted: false));
        var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Vm.SetupVisible);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TestResults"));
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "setup-welcome.png"));
            window.FindControl<Button>("SetupNextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Vm.SetupEnvironmentVisible);
            Assert.Contains("已连接", window.FindControl<TextBlock>("SetupOllamaStatus")!.Text);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "setup-models.png"));
            await fixture.Vm.SetupNextAsync(); await fixture.Vm.SetupNextAsync(); Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("SetupFinishButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(fixture.Vm.SetupVisible); Assert.True(fixture.Vm.ChatPageVisible);
        }
        finally { window.Close(); }
    }
}
