// 用途：验证下载协议和真实 XAML 按钮；使用显式模拟模型，不消耗用户下载流量。
using System.Net;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop;
using PrivateKnowledge.Desktop.ViewModels;
using Xunit;

namespace PrivateKnowledge.ClientTests;

/// <summary>下载流、操作状态与窗口绑定测试。</summary>
public sealed class ModelDownloadsTests
{
    /// <summary>五项追加、草稿不影响续页、失败保留旧结果，快速滚动也不会请求同一页两次。</summary>
    [Fact]
    public async Task PagedLibraryKeepsSelectionAndRetriesWithoutDuplicateRequests()
    {
        using var models = new FakeApi(); var source = new DownloadFake(models);
        using var vm = new ModelDownloadsViewModel(source, models);
        vm.SearchText = "qwen"; await vm.RefreshAsync();
        Assert.Equal(5, vm.Families.Count); Assert.Single(source.VersionRequests);
        var selected = vm.Selected; vm.SearchText = "未提交的草稿";
        source.PageCompletion = new();
        var pending = vm.LoadMoreAsync(); await vm.LoadMoreAsync();
        Assert.Equal(2, source.LibraryRequests.Count);
        Assert.Equal(("qwen", 5), source.LibraryRequests.Last());
        source.PageCompletion.SetResult(new LibraryPage([new("later", "模拟续页", "tools")], 10, false, false));
        await pending; Assert.Equal(6, vm.Families.Count); Assert.Same(selected, vm.Selected);
        Assert.Single(source.VersionRequests);
        source.PageCompletion = null; source.FailPage = true; await vm.LoadMoreAsync();
        Assert.Equal(6, vm.Families.Count); Assert.True(vm.CanLoadMore); Assert.Contains("模拟断网", vm.Status);
        source.FailPage = false; await vm.LoadMoreAsync();
        Assert.Equal(11, vm.Families.Count); Assert.False(vm.CanLoadMore);
    }

    /// <summary>关窗会取消未结束的目录请求，迟到结果不再追加到已释放窗口。</summary>
    [Fact]
    public async Task ClosingCancelsPendingLibraryPage()
    {
        using var models = new FakeApi(); var source = new DownloadFake(models);
        using var vm = new ModelDownloadsViewModel(source, models); await vm.RefreshAsync();
        source.PageCompletion = new(); var pending = vm.LoadMoreAsync();
        vm.Dispose(); await pending;
        Assert.Equal(5, vm.Families.Count);
    }

    /// <summary>真实列表向下滚动到底会续页，而初次显示不会自动把整个目录加载完。</summary>
    [AvaloniaFact]
    public void ActualLibraryScrollbarLoadsNextBatch()
    {
        using var models = new FakeApi(); var source = new DownloadFake(models);
        using var vm = new ModelDownloadsViewModel(source, models);
        var window = new ModelDownloadsWindow(vm) { Width = 900, Height = 640 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, vm.Families.Count);
            var scroll = window.FindControl<ListBox>("LibraryModels")!.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.ScrollToEnd(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(10, vm.Families.Count); Assert.Equal(2, source.LibraryRequests.Count);
        }
        finally { window.Close(); }
    }

    /// <summary>下载正文没有 success 就必须报错，HTTP 200 本身不能代表模型安装好了。</summary>
    [Fact]
    public async Task InterruptedStreamIsNotSuccess()
    {
        using var client = new KnowledgeApiClient("http://127.0.0.1:5088", new ReplyHandler(HttpStatusCode.OK,
            "{\"status\":\"downloading\",\"message\":\"下载中\",\"completed\":2,\"total\":4}\n"));
        var values = new List<DownloadProgress>();
        var error = await Assert.ThrowsAsync<KnowledgeApiException>(async () =>
        { await foreach (var value in client.DownloadAsync("test", default)) values.Add(value); });
        Assert.Single(values); Assert.Equal("download_incomplete", error.Code);
    }

    /// <summary>服务流错误要保留原说明，不能等到超时才告诉用户。</summary>
    [Fact]
    public async Task ExplicitErrorStopsStream()
    {
        using var client = new KnowledgeApiClient("http://127.0.0.1:5088", new ReplyHandler(HttpStatusCode.OK,
            "{\"status\":\"error\",\"message\":\"磁盘空间不足\"}\n"));
        var values = new List<DownloadProgress>();
        await foreach (var value in client.DownloadAsync("test", default)) values.Add(value);
        Assert.Equal("磁盘空间不足", Assert.Single(values).Message);
    }

    /// <summary>取消后恢复下载按钮；再次下载成功后刷新安装状态，不需重新打开客户端。</summary>
    [Fact]
    public async Task CancelRetryAndUseRequiresConfirmedInstallation()
    {
        using var models = new FakeApi();
        var source = new DownloadFake(models) { WaitForCancel = true };
        using var vm = new ModelDownloadsViewModel(source, models);
        await vm.RefreshAsync();
        Assert.True(vm.CanDownload); Assert.False(vm.CanUse);
        var pending = vm.DownloadAsync();
        Assert.True(vm.CanCancel); Assert.False(vm.CanDownload); Assert.False(vm.CanChoose);
        vm.Cancel(); await pending;
        Assert.Contains("已取消", vm.Status); Assert.True(vm.CanDownload);
        source.WaitForCancel = false; await vm.DownloadAsync();
        Assert.True(vm.CanUse); Assert.False(vm.CanDownload); Assert.Equal(100, vm.Percent);
    }

    /// <summary>流错误保留失败提示，并且不会显示“使用此模型”。</summary>
    [Fact]
    public async Task FailedDownloadCanRetryWithoutPretendingInstalled()
    {
        using var models = new FakeApi();
        var source = new DownloadFake(models) { Fail = true };
        using var vm = new ModelDownloadsViewModel(source, models);
        await vm.RefreshAsync(); await vm.DownloadAsync();
        Assert.Contains("测试磁盘不足", vm.Status);
        Assert.True(vm.CanDownload); Assert.False(vm.CanUse);
        source.Fail = false; await vm.DownloadAsync(); Assert.True(vm.CanUse);
    }

    /// <summary>真实下载窗口能选择模型、点击下载并更新按钮，同时保存渲染截图供人工检查。</summary>
    [AvaloniaFact]
    public void DownloadWindowButtonsAndBrandRender()
    {
        using var models = new FakeApi();
        using var vm = new ModelDownloadsViewModel(new DownloadFake(models), models);
        var window = new ModelDownloadsWindow(vm);
        using var mainFixture = new VmFixture();
        var main = new MainWindow(mainFixture.Vm);
        try
        {
            window.Show(); main.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("离线私库 · 有据问答", main.Title);
            Assert.NotNull(main.FindControl<Button>("DownloadModelsButton"));
            var button = window.FindControl<Button>("StartDownload")!;
            Assert.True(button.IsEnabled);
            Save(window, "model-download-window.png"); Save(main, "brand-main-window.png");
            window.Width = 720; window.Height = 640;
            main.Width = 960; main.Height = 680; Dispatcher.UIThread.RunJobs();
            Save(window, "model-download-small.png"); Save(main, "brand-main-small.png");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled); Assert.True(window.FindControl<Button>("UseModel")!.IsEnabled);
            Assert.Contains("下载完成", window.FindControl<TextBlock>("DownloadStatus")!.Text);
        }
        finally { window.Close(); main.Close(); }
    }

    /// <summary>把真实控件渲染为 PNG，展示内容为明确的测试数据。</summary>
    /// <param name="window">测试窗口。</param>
    /// <param name="name">截图文件名。</param>
    private static void Save(Window window, string name)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TestResults"));
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name));
    }
}

/// <summary>只用于测试的模型下载器；名称和配置说明也明确标注模拟。</summary>
/// <param name="models">下载成功时修改的模拟安装清单。</param>
internal sealed class DownloadFake(FakeApi models) : IModelDownloadsApi
{
    /// <summary>记录每批查询，检查使用的是已提交搜索词和正确游标。</summary>
    public List<(string Query, int Offset)> LibraryRequests { get; } = [];
    /// <summary>记录按需版本查询，确保没有提前读取所有系列。</summary>
    public List<string> VersionRequests { get; } = [];
    /// <summary>暂停下一页，模拟快速滚动与关窗。</summary>
    public TaskCompletionSource<LibraryPage>? PageCompletion { get; set; }
    /// <summary>模拟下一批网络错误。</summary>
    public bool FailPage { get; set; }
    /// <summary>模拟等待用户取消。</summary>
    public bool WaitForCancel { get; set; }
    /// <summary>模拟可重试的错误。</summary>
    public bool Fail { get; set; }
    /// <inheritdoc />
    public async Task<LibraryPage> GetLibraryAsync(string query, int offset, CancellationToken token)
    {
        LibraryRequests.Add((query, offset));
        if (offset > 0 && FailPage) throw new KnowledgeApiException("模拟断网，可重试");
        if (offset > 0 && PageCompletion is not null) return await PageCompletion.Task.WaitAsync(token);
        return new LibraryPage(Enumerable.Range(offset, 5).Select(i => new LibraryFamily("test-family-" + i, "模拟官网简介，不下载模型", "tools")).ToArray(), offset < 10 ? offset + 5 : null, false, false);
    }
    /// <inheritdoc />
    public Task<LibraryVersions> GetVersionsAsync(string family, CancellationToken token)
    {
        VersionRequests.Add(family);
        return Task.FromResult(new LibraryVersions(
        [new("fake-chat", "轻量入门 · 测试模型", "模拟已安装模型，用于验证界面。", "约 1.4 GB",
            "起步内存 8 GB，建议 16 GB；可用 CPU（较慢），加速建议兼容显卡、专用显存 4 GB+；磁盘预留 3 GB。", "https://ollama.com"),
         new("fake-download", "日常使用 · 测试模型", "模拟下载选项，不会下载真实模型。", "约 2.5 GB",
            "起步内存 16 GB；可用 CPU（较慢），加速建议兼容显卡、专用显存 6 GB+；磁盘预留 5 GB。", "https://ollama.com"),
         new("fake-large", "较高配置 · 测试模型", "用于验证滚动和配置说明。", "约 5.2 GB",
            "起步内存 16 GB，建议 32 GB；可用 CPU（较慢），加速建议兼容显卡、专用显存 10 GB+；磁盘预留 10 GB。", "https://ollama.com")], false, false));
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<DownloadProgress> DownloadAsync(string name, [EnumeratorCancellation] CancellationToken token)
    {
        yield return new("downloading", "测试下载中", 50, 100);
        if (WaitForCancel) await Task.Delay(Timeout.Infinite, token);
        if (Fail) { yield return new("error", "测试磁盘不足，请检查后重试。"); yield break; }
        models.Catalog = new(models.Catalog.DefaultModel, [.. models.Catalog.Models, new(name, false)]);
        yield return new("success", "测试下载完成");
    }
}
