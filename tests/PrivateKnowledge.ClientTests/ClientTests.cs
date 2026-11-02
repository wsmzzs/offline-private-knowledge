// 用途：验证客户端协议、状态竞态和真实 XAML 绑定。假服务只用于稳定制造特定情况。
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop;
using PrivateKnowledge.Desktop.Services;
using PrivateKnowledge.Desktop.ViewModels;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PrivateKnowledge.ClientTests.TestAppBuilder))]

namespace PrivateKnowledge.ClientTests;

/// <summary>使用真正的 XAML 和 Skia 渲染，但不创建操作系统窗口。</summary>
public static class TestAppBuilder
{
    /// <summary>让界面测试可以生成截图并检查绑定。</summary>
    /// <returns>测试专用的 Avalonia 应用。</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>纯网络和 ViewModel 测试，不依赖真实模型速度。</summary>
public sealed class ClientBehaviorTests
{
    /// <summary>轮询没赶上短暂 updating 时，修订号变化仍留下完成提示，并保留输入草稿。</summary>
    [Fact]
    public async Task ReadyRevisionChangePersistsUpdateHintAndSourcePath()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        fixture.Api.Health = new("ready", "ready", "ready", @"D:\ExampleKnowledge", 5);
        await vm.InitializeAsync(); vm.Question = "保留草稿";
        Assert.Contains(@"D:\ExampleKnowledge", vm.KnowledgeLocation);
        Assert.Contains("5 秒", vm.KnowledgeUpdateHint);
        fixture.Api.BaseRevision = 2; await vm.RefreshAsync();
        Assert.Contains("修订 1 → 2", vm.KnowledgeUpdateHint);
        await vm.RefreshAsync(); Assert.Contains("修订 1 → 2", vm.KnowledgeUpdateHint);
        Assert.Equal("保留草稿", vm.Question);
        vm.SelectedBase = vm.Bases[1]; Assert.DoesNotContain("1 → 2", vm.KnowledgeUpdateHint);
        vm.SelectedBase = vm.Bases[0]; Assert.Contains("1 → 2", vm.KnowledgeUpdateHint);
        vm.ServiceAddress = "http://127.0.0.1:5099"; await vm.ConnectAsync();
        Assert.DoesNotContain("1 → 2", vm.KnowledgeUpdateHint);
    }

    /// <summary>默认选择、手动切换、请求锁定和模型删除都必须按实际列表处理。</summary>
    [Fact]
    public async Task ModelSelectionSurvivesRefreshAndDisablesMissingModel()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        await vm.InitializeAsync(); Assert.Equal("fake-chat", vm.SelectedModel);
        vm.SelectedModel = "fake-second"; await vm.RefreshAsync();
        Assert.Equal("fake-second", vm.SelectedModel);
        var completion = new TaskCompletionSource<AnswerResult>();
        fixture.Api.AskOverride = (request, _) =>
        { Assert.Equal("fake-second", request.Model); return completion.Task; };
        vm.Question = "问题"; var pending = vm.SendAsync();
        Assert.False(vm.CanSelectModel); vm.SelectedModel = "fake-chat";
        Assert.Equal("fake-second", vm.SelectedModel);
        completion.SetResult(FakeApi.Answer("a") with { Model = "fake-second" }); await pending;
        Assert.Contains("fake-second", vm.Turns[0].Status);
        fixture.Api.Catalog = new("fake-chat", [new("fake-chat", true)]);
        await vm.RefreshAsync(); vm.Question = "草稿";
        Assert.False(vm.CanSend); Assert.Equal("草稿", vm.Question);
        vm.SelectedModel = "fake-chat"; Assert.True(vm.CanSend);
        Assert.Contains("fake-second", vm.Turns[0].Status);
    }

    /// <summary>服务默认模型缺失时不偷偷选择替代品，但用户可以主动选择已安装模型。</summary>
    [Fact]
    public async Task MissingDefaultAllowsExplicitAlternative()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        fixture.Api.Catalog = new("missing-default", [new("fake-second", false)]);
        fixture.Api.Health = new("degraded", "ready", "chat_model_missing");
        await vm.InitializeAsync(); vm.Question = "问题";
        Assert.Null(vm.SelectedModel); Assert.False(vm.CanSend);
        vm.SelectedModel = "fake-second"; Assert.True(vm.CanSend);
        fixture.Api.Health = new("degraded", "ready", "embedding_model_missing");
        await vm.RefreshAsync(); Assert.False(vm.CanSend);
    }
    /// <summary>健康接口 503 是可解释的状态，而不是无信息的连接异常。</summary>
    [Fact]
    public async Task Health503PreservesModelDiagnosis()
    {
        using var client = new KnowledgeApiClient("http://127.0.0.1:5088", new ReplyHandler(HttpStatusCode.ServiceUnavailable,
            """{"status":"degraded","catalog":"ready","model":"chat_model_missing"}"""));
        var health = await client.GetHealthAsync(default);
        Assert.False(health.IsReady); Assert.Contains("聊天模型", health.Description);
    }

    /// <summary>原文失效保留服务错误编号和请求编号，界面可以给维护人员定位依据。</summary>
    [Fact]
    public async Task ErrorBodyIsPreserved()
    {
        using var client = new KnowledgeApiClient("http://127.0.0.1:5088", new ReplyHandler(HttpStatusCode.NotFound,
            """{"requestId":"r1","code":"document_not_found","message":"资料已更新"}"""));
        var error = await Assert.ThrowsAsync<KnowledgeApiException>(() => client.GetDocumentAsync("old", default));
        Assert.Equal("document_not_found", error.Code); Assert.Equal("r1", error.RequestId);
    }

    /// <summary>地址、非 JSON 和错误项目响应不能假装成成功。</summary>
    [Fact]
    public async Task InvalidResponsesAndRemoteAddressAreRejected()
    {
        Assert.Throws<KnowledgeApiException>(() => KnowledgeApiClient.ValidateAddress("https://example.com"));
        using var malformed = new KnowledgeApiClient("http://localhost:5088", new ReplyHandler(HttpStatusCode.OK, "<html>错误服务</html>"));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<KnowledgeApiException>(() => malformed.GetBasesAsync(default))).Code);
        using var wrongBase = new KnowledgeApiClient("http://localhost:5088", new ReplyHandler(HttpStatusCode.OK,
            """{"requestId":"r","status":"answered","knowledgeBaseId":"b","knowledgeRevision":1,"answer":"错误项目","citations":[]}"""));
        await Assert.ThrowsAsync<KnowledgeApiException>(() => wrongBase.AskAsync(new("a", "问题"), default));
    }

    /// <summary>同一问题按项目分开显示，切回项目时保留其本次会话记录。</summary>
    [Fact]
    public async Task HistoriesAndSourcesRemainInTheirProject()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        await vm.InitializeAsync(); vm.Question = "下一步？"; await vm.SendAsync();
        var first = Assert.Single(vm.Turns); Assert.Equal("先核对", first.Answer);
        await vm.OpenSourceAsync(Assert.Single(first.Sources)); Assert.Equal("资料 a", vm.DocumentText);
        vm.SelectedBase = vm.Bases[1]; Assert.Empty(vm.Turns); Assert.False(vm.SourceVisible);
        vm.Question = "下一步？"; await vm.SendAsync(); Assert.Equal("先登记", Assert.Single(vm.Turns).Answer);
        vm.SelectedBase = vm.Bases[0]; Assert.Same(first, Assert.Single(vm.Turns));
    }

    /// <summary>模拟服务不理会取消的迟到答案，也不能覆盖当前项目。</summary>
    [Fact]
    public async Task SwitchingProjectRejectsLateAnswer()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        var completion = new TaskCompletionSource<AnswerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.AskOverride = (_, _) => completion.Task;
        await vm.InitializeAsync(); vm.Question = "旧问题"; var pending = vm.SendAsync();
        var old = Assert.Single(vm.Turns); vm.SelectedBase = vm.Bases[1];
        completion.SetResult(FakeApi.Answer("a")); await pending;
        Assert.Empty(vm.Turns); Assert.Equal("已取消", old.Status); Assert.False(old.CanCopy); Assert.False(vm.IsAsking);
    }

    /// <summary>取消后可以再次提问，第二次答案不被第一次的 finally 清掉。</summary>
    [Fact]
    public async Task CancelThenAskAgainWorks()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        fixture.Api.AskOverride = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return FakeApi.Answer("a"); };
        await vm.InitializeAsync(); vm.Question = "取消此问题"; var first = vm.SendAsync();
        vm.CancelQuestion(); await first;
        fixture.Api.AskOverride = null; vm.Question = "新问题"; await vm.SendAsync();
        Assert.Equal("已取消", vm.Turns[0].Status); Assert.True(vm.Turns[1].CanCopy);
    }

    /// <summary>资料状态变化保留输入草稿，禁用发送，恢复后可继续发送。</summary>
    [Fact]
    public async Task RefreshPreservesDraftAndDisablesUnavailableProject()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        await vm.InitializeAsync(); vm.Question = "尚未发送的草稿";
        fixture.Api.BaseStatus = "updating"; await vm.RefreshAsync();
        Assert.Equal("尚未发送的草稿", vm.Question); Assert.False(vm.CanSend);
        fixture.Api.BaseStatus = "ready"; await vm.RefreshAsync(); Assert.True(vm.CanSend);
    }

    /// <summary>旧回答的引用片段可辨认，但新修订全文不伪装成旧依据。</summary>
    [Fact]
    public async Task ChangedDocumentRevisionIsNotShownAsOriginal()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        await vm.InitializeAsync(); vm.Question = "问题"; await vm.SendAsync();
        fixture.Api.DocumentRevision = 2;
        await vm.OpenSourceAsync(vm.Turns[0].Sources[0]);
        Assert.Empty(vm.DocumentText); Assert.NotEmpty(vm.SourceExcerpt); Assert.Contains("版本已变化", vm.SourceStatus);
    }

    /// <summary>更换服务即使出现同名项目，也不能沿用旧服务聊天记录。</summary>
    [Fact]
    public async Task ChangingConnectionClearsHistoryAndPersistsPreference()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        await vm.InitializeAsync(); vm.Question = "问题"; await vm.SendAsync();
        vm.ServiceAddress = "http://127.0.0.1:5099"; await vm.ConnectAsync();
        Assert.Empty(vm.Turns); Assert.Equal("http://127.0.0.1:5099/", fixture.Store.Load().ServiceAddress);
        Assert.DoesNotContain("问题", File.ReadAllText(fixture.Store.FilePath));
    }

    /// <summary>资料不足应显示业务提示，连接失败应显示故障，二者不能混成同一种答案。</summary>
    [Fact]
    public async Task InsufficientEvidenceAndFailureAreDifferentStates()
    {
        using var fixture = new VmFixture(); var vm = fixture.Vm;
        await vm.InitializeAsync();
        fixture.Api.AskOverride = (r, _) => Task.FromResult(new AnswerResult("r", "insufficient_evidence", r.KnowledgeBaseId, 1, "资料没有说明", []));
        vm.Question = "未知规则"; await vm.SendAsync(); Assert.Contains("依据不足", vm.Turns[0].Status); Assert.Empty(vm.Turns[0].Sources);
        fixture.Api.AskOverride = (_, _) => throw new KnowledgeApiException("无法连接服务", "connection_failed");
        vm.Question = "新问题"; await vm.SendAsync(); Assert.Equal("未能完成回答", vm.Turns[1].Status); Assert.False(vm.Turns[1].CanCopy);
    }
}

/// <summary>实际加载主窗口 XAML，验证事件和绑定，并输出可人工检查的截图。</summary>
public sealed class DesktopBindingTests
{
    /// <summary>实际 XAML 应显示服务返回的检索模型；换成纯关键词后不能继续展示旧模型。</summary>
    [AvaloniaFact]
    public async Task RetrievalLabelTracksActualServiceMode()
    {
        using var fixture = new VmFixture();
        fixture.Api.Health = new("ready", "ready", "ready", @"D:\ExampleKnowledge", 5,
            new RetrievalInfo("hybrid", "qwen3-embedding:0.6b", true, 0));
        var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show();
            await fixture.Vm.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            var label = window.FindControl<TextBlock>("RetrievalLabel")!;
            Assert.Contains("qwen3-embedding:0.6b", label.Text);
            Assert.Contains("CPU", label.Text);
            // 截图来自真实 XAML 和明确的测试服务状态，不冒充真实问答或公司数据。
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TestResults"));
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "client-semantic-status.png"));
            fixture.Api.Health = fixture.Api.Health with { Retrieval = new RetrievalInfo("keyword", null, true, 0) };
            await fixture.Vm.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("关键词检索", label.Text);
            Assert.DoesNotContain("qwen3-embedding", label.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>把按键交给真实输入框处理，复现多行文本框提前消费 Ctrl+Enter 的问题。</summary>
    [AvaloniaFact]
    public void CtrlEnterSendsFocusedQuestionExactlyOnce()
    {
        using var fixture = new VmFixture();
        var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var input = window.FindControl<TextBox>("QuestionInput")!;
            fixture.Vm.SelectedModel = "fake-second";
            input.Text = "连接成功后为什么没有温度曲线？"; input.CaretIndex = input.Text.Length;
            Assert.True(input.Focus()); Dispatcher.UIThread.RunJobs();
            // 模拟窗口实际收到按键，不直接调用事件方法，才能覆盖文本框自己的按键处理顺序。
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, fixture.Api.AskCount);
            Assert.Equal("连接成功后为什么没有温度曲线？", fixture.Api.LastRequest!.Question);
            Assert.Equal("fake-second", fixture.Api.LastRequest.Model);
            Assert.Empty(input.Text ?? "");
            Assert.Equal("先核对", Assert.Single(fixture.Vm.Turns).Answer);
        }
        finally { window.Close(); }
    }

    /// <summary>普通 Enter 保持换行；连接地址框按 Ctrl+Enter 不能误发问题草稿。</summary>
    [AvaloniaFact]
    public void PlainEnterAndOtherTextBoxKeepTheirOwnBehavior()
    {
        using var fixture = new VmFixture();
        var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var input = window.FindControl<TextBox>("QuestionInput")!;
            input.Text = "第一行"; input.CaretIndex = input.Text.Length;
            Assert.True(input.Focus()); Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("第一行\n", input.Text!.Replace("\r\n", "\n"));
            Assert.Equal(0, fixture.Api.AskCount);
            fixture.Vm.SettingsVisible = true; Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<TextBox>("AddressInput")!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, fixture.Api.AskCount);
            Assert.Empty(fixture.Vm.Turns);
        }
        finally { window.Close(); }
    }

    /// <summary>等待回答时重复按快捷键不能提交两次，也不能把正在编辑的下一条草稿插入换行。</summary>
    [AvaloniaFact]
    public void CtrlEnterWhileWaitingDoesNotDuplicateOrChangeDraft()
    {
        using var fixture = new VmFixture();
        var completion = new TaskCompletionSource<AnswerResult>();
        fixture.Api.AskOverride = (_, _) => completion.Task;
        var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var input = window.FindControl<TextBox>("QuestionInput")!;
            input.Text = "第一次提问"; Assert.True(input.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            Assert.Equal(1, fixture.Api.AskCount); Assert.True(fixture.Vm.IsAsking);
            input.Text = "下一条草稿"; input.CaretIndex = input.Text.Length;
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, fixture.Api.AskCount);
            Assert.Equal("下一条草稿", input.Text);
        }
        finally
        {
            // 先取消窗口等待再释放测试回答，保证测试没有留下悬挂的任务或窗口。
            window.Close(); completion.TrySetResult(FakeApi.Answer("a")); Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>输入、发送按钮、项目切换与数据刷新都走真实控件。</summary>
    [AvaloniaFact]
    public async Task WindowBindingsSendAndPreserveDraftOnRefresh()
    {
        using var fixture = new VmFixture();
        var window = new MainWindow(fixture.Vm);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("资料目录", window.FindControl<SelectableTextBlock>("KnowledgeLocationLabel")!.Text);
            var selector = window.FindControl<ComboBox>("ModelSelector")!;
            Assert.Equal("fake-chat", selector.SelectedItem);
            selector.SelectedItem = "fake-second"; Dispatcher.UIThread.RunJobs();
            Assert.Equal("fake-second", fixture.Vm.SelectedModel);
            var input = window.FindControl<TextBox>("QuestionInput")!;
            var send = window.FindControl<Button>("SendButton")!;
            input.Text = "连接成功后为什么没有温度曲线？"; Dispatcher.UIThread.RunJobs();
            Assert.True(send.IsEnabled);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.Equal("先核对", Assert.Single(fixture.Vm.Turns).Answer);
            Assert.Equal("fake-second", fixture.Api.LastRequest!.Model);
            await fixture.Vm.OpenSourceAsync(fixture.Vm.Turns[0].Sources[0]); Dispatcher.UIThread.RunJobs();
            Assert.Equal("资料 a", window.FindControl<TextBox>("DocumentTextBox")!.Text);
            input.Text = "保留草稿"; fixture.Api.BaseStatus = "updating";
            await fixture.Vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("保留草稿", input.Text); Assert.False(send.IsEnabled);
            fixture.Api.BaseStatus = "ready"; await fixture.Vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            fixture.Api.BaseRevision = 2; await fixture.Vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("修订 1 → 2", window.FindControl<TextBlock>("KnowledgeUpdateLabel")!.Text);
            Assert.Equal("fake-second", selector.SelectedItem);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TestResults"));
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "client-headless.png"));
            window.Width = 960; window.Height = 620; Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "client-headless-small.png"));
            window.FindControl<ListBox>("ProjectList")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", fixture.Vm.SelectedBase!.Id); Assert.Empty(fixture.Vm.Turns);
        }
        finally { window.Close(); }
    }
}

/// <summary>返回指定 HTTP 响应，测试错误转换，不伪装成真实服务。</summary>
/// <param name="status">测试指定状态码，如 503。</param>
/// <param name="body">手写协议 JSON 或故意错误的文本。</param>
internal sealed class ReplyHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    /// <summary>拦截测试请求，不访问网络。</summary>
    /// <param name="request">客户端生成的请求。</param>
    /// <param name="cancellationToken">调用取消信号。</param>
    /// <returns>测试构造的 HTTP 响应。</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}

/// <summary>每个测试独享的设置路径和假服务。</summary>
internal sealed class VmFixture : IDisposable
{
    /// <summary>当前测试的临时目录，释放前验证仍位于测试父目录。</summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "private-knowledge-client-tests", Guid.NewGuid().ToString("N"));
    /// <summary>可调整行为的假服务。</summary>
    public FakeApi Api { get; } = new();
    /// <summary>隔离设置，测试不修改用户配置。</summary>
    public SettingsStore Store { get; }
    /// <summary>被测操作逻辑。</summary>
    public MainViewModel Vm { get; }
    /// <summary>建立测试依赖，不连接真实模型。</summary>
    public VmFixture() { Store = new(Path.Combine(_directory, "settings.json")); Store.Save(new(SetupCompleted: true)); Vm = new(Store, _ => Api); }
    /// <summary>释放界面逻辑，只删除本次 GUID 测试目录。</summary>
    public void Dispose()
    {
        Vm.Dispose();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private-knowledge-client-tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_directory).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>确定性协议测试实现；所有回答均是手写测试数据。</summary>
internal sealed class FakeApi : IKnowledgeApi, ISetupApi
{
    /// <summary>模拟没有观察到 updating、直接收到新修订的情况。</summary>
    public long BaseRevision { get; set; } = 1;
    /// <summary>模拟已安装的两个本地模型，默认使用第一个。</summary>
    public ModelCatalog Catalog { get; set; } = new("fake-chat", [new("fake-chat", true), new("fake-second", false)]);
    /// <summary>可模拟默认模型或嵌入模型缺失。</summary>
    public HealthInfo Health { get; set; } = new("ready", "ready", "ready", @"D:\ExampleKnowledge\Projects", 5);
    /// <summary>首次配置协议的明确测试数据，不代表真实安装状态。</summary>
    public SetupInfo Setup { get; set; } = new(true, "fake-chat", true, "fake-embed", true, true, "测试模型已准备好。");
    /// <summary>记录用户实际发起的准备下载；正常页面打开不应调用。</summary>
    public List<string> SetupDownloads { get; } = [];
    /// <summary>取消测试可让下载停在半途；其他测试不设置，不使用真实网络。</summary>
    public TaskCompletionSource? SetupDownloadGate { get; set; }
    /// <inheritdoc />
    public Task<SetupInfo> GetSetupAsync(CancellationToken token) => Task.FromResult(Setup);
    /// <inheritdoc />
    public async IAsyncEnumerable<DownloadProgress> DownloadSetupAsync(string role, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        SetupDownloads.Add(role);
        yield return new("downloading", "测试下载进度", 50, 100);
        if (SetupDownloadGate is not null) await SetupDownloadGate.Task.WaitAsync(token);
        await Task.Yield(); token.ThrowIfCancellationRequested();
        Setup = role == "embedding" ? Setup with { EmbeddingInstalled = true } : Setup with { ChatInstalled = true };
        yield return new("success", "测试能力核验通过");
    }
    /// <summary>记录真实控件事件最终提交的请求。</summary>
    public AskRequest? LastRequest { get; private set; }
    /// <summary>统计真正提交的次数，用于检查快捷键重复触发。</summary>
    public int AskCount { get; private set; }
    /// <inheritdoc />
    public Task<ModelCatalog> GetModelsAsync(CancellationToken token) => Task.FromResult(Catalog);
    /// <summary>模拟目录更新状态。</summary>
    public string BaseStatus { get; set; } = "ready";
    /// <summary>模拟旧引用与新原文版本不一致。</summary>
    public long DocumentRevision { get; set; } = 1;
    /// <summary>特定测试可设置延迟、取消或失败。</summary>
    public Func<AskRequest, CancellationToken, Task<AnswerResult>>? AskOverride { get; set; }
    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeBaseInfo>> GetBasesAsync(CancellationToken token) =>
        Task.FromResult<IReadOnlyList<KnowledgeBaseInfo>>([new("a", BaseStatus, BaseRevision, 1, "测试资料"), new("b", "ready", 1, 1, "测试资料")]);
    /// <inheritdoc />
    public Task<HealthInfo> GetHealthAsync(CancellationToken token) => Task.FromResult(Health);
    /// <inheritdoc />
    public Task<AnswerResult> AskAsync(AskRequest request, CancellationToken token)
    {
        LastRequest = request;
        AskCount++;
        return AskOverride?.Invoke(request, token) ?? Task.FromResult(Answer(request.KnowledgeBaseId) with { Model = request.Model });
    }
    /// <summary>手写测试答案，项目不同答案不同。</summary>
    /// <param name="id">测试知识库 a 或 b。</param>
    /// <returns>有引用的测试结果，绝非真实模型结果。</returns>
    public static AnswerResult Answer(string id) => new("test-request", "answered", id, 1, id == "a" ? "先核对" : "先登记",
        [new("doc-" + id, "测试手册.md", "操作顺序", "这是测试引用片段")]);
    /// <inheritdoc />
    public Task<DocumentResult> GetDocumentAsync(string documentId, CancellationToken token)
    { var id = documentId[4..]; return Task.FromResult(new DocumentResult(documentId, id, DocumentRevision, "测试手册.md", "资料 " + id)); }
    /// <inheritdoc />
    public void Dispose() { /* 没有网络资源；可复用以验证切换连接。 */ }
}
