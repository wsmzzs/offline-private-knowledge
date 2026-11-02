// 用途：窗口只处理控件事件、定时刷新和剪贴板；业务操作全部交给 MainViewModel。
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop.Services;
using PrivateKnowledge.Desktop.ViewModels;

namespace PrivateKnowledge.Desktop;

/// <summary>主窗口，与 ViewModel 通过 DataContext 建立属性绑定。</summary>
public partial class MainWindow : Window
{
    /// <summary>窗口当前操作对象，也用于界面集成测试。</summary>
    public MainViewModel ViewModel { get; }
    /// <summary>每五秒更新状态，RefreshAsync 自身会防止重叠请求。</summary>
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    /// <summary>问题输入框；在控件自身处理回车之前接收快捷键，关闭窗口时解除事件。</summary>
    private readonly TextBox _questionInput;
    /// <summary>只在异步收尾完成后允许第二次关闭，防止关窗时后台服务来不及退出。</summary>
    private bool _allowClose;

    /// <summary>正常启动入口，创建真实网络和用户设置。</summary>
    public MainWindow() : this(new MainViewModel(new SettingsStore())) { }

    /// <summary>测试可以传入隔离的 ViewModel，避免访问真实私有资料。</summary>
    /// <param name="viewModel">已经配置好网络和设置依赖的操作对象。</param>
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent(); DataContext = viewModel;
        _questionInput = this.FindControl<TextBox>("QuestionInput")!;
        // 多行 TextBox 会先把回车当成换行并标记已处理，普通 XAML KeyDown 因而收不到 Ctrl+Enter。
        // Tunnel 相当于“先看一眼按键”：只有 Ctrl+Enter 被截获，普通 Enter 仍交给输入框换行。
        _questionInput.AddHandler(InputElement.KeyDownEvent, OnQuestionKeyDown, RoutingStrategies.Tunnel);
        // 开窗后才联网，构造函数保持轻量；关闭时解绑并取消等待，避免后台任务保留窗口。
        Opened += OnOpened; Closing += OnClosing; Closed += OnClosed;
        _refreshTimer.Tick += OnTimerTick;
    }

    /// <summary>界面就绪后读取设置、连接服务，然后开始周期核对。</summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="e">窗口打开事件，无业务数据。</param>
    private async void OnOpened(object? sender, EventArgs e)
    {
        if (Design.IsDesignMode) return;
        // 先保证标题栏在屏幕内，再等待服务启动；用户从打开窗口起就能移动、最大化和关闭。
        WindowPlacement.FitOnOpen(this);
        await RunUiAsync(ViewModel.InitializeAsync);
        if (IsVisible) _refreshTimer.Start();
    }

    /// <summary>首次关闭先停轮询、取消准备，再等待本窗口拥有的后台进程退出。</summary>
    /// <param name="sender">当前主窗口。</param>
    /// <param name="e">先阻止关窗，收尾结束后重新关闭；企业服务不会收到关闭请求。</param>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; _refreshTimer.Stop();
        await RunUiAsync(ViewModel.ShutdownAsync);
        _allowClose = true; Close();
    }

    /// <summary>异步收尾后释放本窗口资源；已有的独立服务不受影响。</summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="e">关闭事件。</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop(); _refreshTimer.Tick -= OnTimerTick;
        _questionInput.RemoveHandler(InputElement.KeyDownEvent, OnQuestionKeyDown);
        Opened -= OnOpened; Closing -= OnClosing; Closed -= OnClosed; ViewModel.Dispose();
    }

    /// <summary>手动按钮或定时器都调用同一刷新逻辑。</summary>
    /// <param name="sender">按钮或定时器。</param>
    /// <param name="e">事件参数，不含请求数据。</param>
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RunUiAsync(ViewModel.RetryAppliedAsync);

    /// <summary>定时器使用普通事件参数，与按钮事件分别接入同一刷新方法。</summary>
    /// <param name="sender">窗口的定时器。</param>
    /// <param name="e">定时事件，不携带业务数据。</param>
    private async void OnTimerTick(object? sender, EventArgs e)
    { if (!ViewModel.IsPreparing) await RunUiAsync(ViewModel.RefreshAsync); }

    /// <summary>展开或收起连接设置，不立即改变服务地址。</summary>
    /// <param name="sender">连接设置按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnToggleSettings(object? sender, RoutedEventArgs e) => ViewModel.SettingsVisible = !ViewModel.SettingsVisible;

    /// <summary>顶部状态按钮只展开说明和重试入口，不把启动/停止开关交给企业员工。</summary>
    /// <param name="sender">顶部模式状态按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnToggleRuntime(object? sender, RoutedEventArgs e) => ViewModel.RuntimeVisible = !ViewModel.RuntimeVisible;

    /// <summary>点击本机卡片只修改草稿，不启动或关闭程序。</summary>
    /// <param name="sender">本机使用卡片。</param>
    /// <param name="e">点击事件。</param>
    private void OnLocalMode(object? sender, RoutedEventArgs e) => ViewModel.ModeIndex = 0;

    /// <summary>点击企业卡片只修改草稿，保存后才应用。</summary>
    /// <param name="sender">企业使用卡片。</param>
    /// <param name="e">点击事件。</param>
    private void OnEnterpriseMode(object? sender, RoutedEventArgs e) => ViewModel.ModeIndex = 1;

    /// <summary>使用系统文件夹选择器；选完只是设置草稿，点击保存后才启用。</summary>
    /// <param name="sender">选择资料文件夹按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnChooseFolder(object? sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "选择知识资料根目录（每个子文件夹为一个项目）", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) ViewModel.KnowledgePath = path;
    });

    /// <summary>应用输入框中的地址并显示检查结果。</summary>
    /// <param name="sender">应用按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnConnect(object? sender, RoutedEventArgs e) => await RunUiAsync(ViewModel.ConnectAsync);

    /// <summary>设置中的重新配置入口；仅打开页面，不立即停止服务。</summary>
    /// <param name="sender">重新配置按钮。</param><param name="e">点击事件，没有业务数据。</param>
    private void OnOpenSetup(object? sender, RoutedEventArgs e) => ViewModel.OpenSetup();
    /// <summary>引导按钮将步骤推进交给操作逻辑，错误仍在窗口内解释。</summary>
    /// <param name="sender">下一步按钮。</param><param name="e">点击事件。</param>
    private async void OnSetupNext(object? sender, RoutedEventArgs e) => await RunUiAsync(ViewModel.SetupNextAsync);
    /// <summary>回退只切换引导页面。</summary>
    /// <param name="sender">上一步按钮。</param><param name="e">点击事件。</param>
    private void OnSetupBack(object? sender, RoutedEventArgs e) => ViewModel.SetupBack();
    /// <summary>只有真实检查通过并保存设置成功才退出首次配置。</summary>
    /// <param name="sender">进入问答按钮。</param><param name="e">点击事件。</param>
    private void OnSetupFinish(object? sender, RoutedEventArgs e) => ViewModel.FinishSetup();
    /// <summary>检查已有安装和模型，不自动下载。</summary>
    /// <param name="sender">重新检查按钮。</param><param name="e">点击事件。</param>
    private async void OnSetupCheck(object? sender, RoutedEventArgs e) => await RunUiAsync(ViewModel.CheckSetupAsync);
    /// <summary>用户点击后主动下载服务推荐的回答模型。</summary>
    /// <param name="sender">下载回答模型按钮。</param><param name="e">点击事件。</param>
    private async void OnSetupDownloadChat(object? sender, RoutedEventArgs e) => await RunUiAsync(() => ViewModel.DownloadSetupModelAsync("chat"));
    /// <summary>用户点击后主动下载服务配置的检索模型。</summary>
    /// <param name="sender">下载检索模型按钮。</param><param name="e">点击事件。</param>
    private async void OnSetupDownloadEmbedding(object? sender, RoutedEventArgs e) => await RunUiAsync(() => ViewModel.DownloadSetupModelAsync("embedding"));
    /// <summary>取消下载连接，保留 Ollama 已接收的文件供下次复用。</summary>
    /// <param name="sender">取消下载按钮。</param><param name="e">点击事件。</param>
    private void OnSetupCancelDownload(object? sender, RoutedEventArgs e) => ViewModel.CancelSetupDownload();
    /// <summary>显式选择虚构样例目录，避免已有配置指向私库时把它误称为演示资料。</summary>
    /// <param name="sender">使用虚构样例按钮。</param><param name="e">点击事件。</param>
    private void OnSetupUseSamples(object? sender, RoutedEventArgs e) => ViewModel.UsePublicSamples();
    /// <summary>仅在用户点击时用系统浏览器打开固定官方安装页，不下载或执行安装器。</summary>
    /// <param name="sender">打开 Ollama 官网按钮。</param><param name="e">点击事件。</param>
    private async void OnOpenOllama(object? sender, RoutedEventArgs e) => await RunUiAsync(() =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ollama.com/download/windows") { UseShellExecute = true });
        return Task.CompletedTask;
    });

    /// <summary>打开下载窗口，返回时重新核对模型清单；用户明确点“使用”才改变当前选择。</summary>
    /// <param name="sender">模型下载按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnDownloadModels(object? sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (!ViewModel.CanManageModels) return;
        var dialog = new ModelDownloadsWindow(ViewModel.AppliedServiceAddress);
        var selected = await dialog.ShowDialog<string?>(this);
        await ViewModel.RefreshAsync();
        if (selected is null) return;
        if (ViewModel.IsAsking)
            ViewModel.Notice = "模型已就绪；当前问题仍使用原模型，回答结束后可从下拉框选择新模型。";
        else if (ViewModel.Models.Contains(selected))
        { ViewModel.SelectedModel = selected; ViewModel.Notice = $"已选用 {selected}，后续提问使用此模型。"; }
        else ViewModel.Notice = "模型已下载，请刷新状态确认模型列表后再选择。";
    });

    /// <summary>只处理真实非空选择；集合刷新短暂清空列表时不能清掉用户草稿。</summary>
    /// <param name="sender">项目列表控件。</param>
    /// <param name="e">选择变化事件。</param>
    private void OnProjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsUpdatingBases && sender is ListBox { SelectedItem: KnowledgeBaseInfo item })
            ViewModel.SelectedBase = item;
    }

    /// <summary>把模型选择交给操作逻辑；刷新列表产生的临时事件不改变用户选择。</summary>
    /// <param name="sender">顶部模型下拉框。</param>
    /// <param name="e">选择变化事件，不携带请求正文。</param>
    private void OnModelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsUpdatingModels && sender is ComboBox { SelectedItem: string name })
            ViewModel.SelectedModel = name;
    }

    /// <summary>发送后滚到新回答位置；网络等待不阻塞窗口。</summary>
    /// <param name="sender">发送按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnSend(object? sender, RoutedEventArgs e) => await SendAndScrollAsync();

    /// <summary>在问题输入框处理按键之前识别 Ctrl+Enter；普通 Enter 仍然换行。</summary>
    /// <param name="sender">问题输入框。</param>
    /// <param name="e">按键及修饰键，符合快捷键时标记已处理。</param>
    private async void OnQuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // 先阻止输入框继续插入换行；能否发送仍由同按钮共用的 CanSend 条件判断。
            // 因此空问题、资料未就绪或正在等待时，快捷键不会绕过限制，也不会改变草稿。
            e.Handled = true;
            await SendAndScrollAsync();
        }
    }

    /// <summary>统一按钮与快捷键的滚动体验，先显示等待记录，再显示最终结果。</summary>
    /// <returns>界面操作结束的任务。</returns>
    private async Task SendAndScrollAsync()
    {
        var task = RunUiAsync(ViewModel.SendAsync);
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("ConversationScroll")?.ScrollToEnd(), DispatcherPriority.Background);
        await task;
        this.FindControl<ScrollViewer>("ConversationScroll")?.ScrollToEnd();
    }

    /// <summary>通知当前请求取消；不关闭服务。</summary>
    /// <param name="sender">取消按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnCancel(object? sender, RoutedEventArgs e) => ViewModel.CancelQuestion();

    /// <summary>引用按钮的 Tag 保存 SourceLink，包含项目和版本，避免凭文件名猜原文。</summary>
    /// <param name="sender">当前引用按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnSource(object? sender, RoutedEventArgs e)
    { if (sender is Button { Tag: SourceLink link }) await RunUiAsync(() => ViewModel.OpenSourceAsync(link)); }

    /// <summary>关闭原文面板并取消未完成加载。</summary>
    /// <param name="sender">收起按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnCloseSource(object? sender, RoutedEventArgs e) => ViewModel.CloseSource();

    /// <summary>只有用户主动点击才把答案写入系统剪贴板。</summary>
    /// <param name="sender">Tag 内带有对应问答记录的按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuestionTurn { CanCopy: true } turn }) return;
        await RunUiAsync(async () =>
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { ViewModel.Notice = "当前环境无法使用剪贴板，请选中文字复制。"; return; }
            await clipboard.SetTextAsync(turn.Answer);
            ViewModel.Notice = "回答已复制到剪贴板。";
        });
    }

    /// <summary>事件必须使用 async void；在这一层捕获意外异常，避免关闭整个桌面进程。</summary>
    /// <param name="operation">需要等待的界面操作，例如 SendAsync。</param>
    /// <returns>操作结束的任务；已知网络错误由 ViewModel 给出具体说明。</returns>
    private async Task RunUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { /* 窗口关闭产生的取消无需再次弹窗。 */ }
        catch (Exception ex)
        { ViewModel.Notice = $"客户端操作未完成（{ex.GetType().Name}），请重试；持续出现时联系维护人员。"; }
    }
}
