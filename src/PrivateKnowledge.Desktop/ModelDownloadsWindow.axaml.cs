// 用途：下载窗口事件只转交 ViewModel，完成后把用户选中的模型名称返回主窗口。
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop.Services;
using PrivateKnowledge.Desktop.ViewModels;

namespace PrivateKnowledge.Desktop;

/// <summary>模态下载窗口；独立连接避免关闭它时影响主窗口的问答连接。</summary>
public partial class ModelDownloadsWindow : Window
{
    /// <summary>正常运行时创建的客户端；测试注入 ViewModel 时为空。</summary>
    private readonly KnowledgeApiClient? _client;
    /// <summary>系列列表内部的滚动条，打开后才有真实控件实例，关窗时解绑。</summary>
    private ScrollViewer? _libraryScroll;
    /// <summary>可独立测试的下载状态。</summary>
    public ModelDownloadsViewModel ViewModel { get; }

    /// <summary>设计器入口，只建立对象，不在构造函数下载或联网。</summary>
    public ModelDownloadsWindow() : this("http://127.0.0.1:5088/") { }

    /// <summary>使用主窗口已经应用的地址，不能使用尚未确认的地址草稿。</summary>
    /// <param name="address">例如 http://127.0.0.1:5088/。</param>
    public ModelDownloadsWindow(string address)
    {
        _client = new KnowledgeApiClient(address);
        ViewModel = new(_client, _client);
        InitializeWindow();
    }

    /// <summary>测试使用隔离的假下载数据，不访问真实模型或资料。</summary>
    /// <param name="viewModel">测试创建的状态对象。</param>
    public ModelDownloadsWindow(ModelDownloadsViewModel viewModel)
    { ViewModel = viewModel; InitializeWindow(); }

    /// <summary>连接 XAML 与事件；关闭时取消网络，防止下载默默在后台继续等待。</summary>
    private void InitializeWindow()
    {
        InitializeComponent(); DataContext = ViewModel;
        Opened += OnOpened; Closed += OnClosed;
    }

    /// <summary>展示后才检查模型状态，避免设计预览启动网络请求。</summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="e">打开事件。</param>
    private async void OnOpened(object? sender, EventArgs e)
    {
        // 下载窗口同样会受显示缩放影响，先适配主窗口所在屏幕，再检查已安装模型。
        WindowPlacement.FitOnOpen(this);
        _libraryScroll = this.FindControl<ListBox>("LibraryModels")?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_libraryScroll is not null) _libraryScroll.ScrollChanged += OnLibraryScrolled;
        await ViewModel.RefreshAsync();
    }
    /// <summary>关闭窗口释放下载请求与独立连接。</summary>
    /// <param name="sender">当前窗口。</param>
    /// <param name="e">关闭事件。</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_libraryScroll is not null) _libraryScroll.ScrollChanged -= OnLibraryScrolled;
        Opened -= OnOpened; Closed -= OnClosed; ViewModel.Dispose(); _client?.Dispose();
    }
    /// <summary>仅用户向下滚动且接近底部才续页；内容变高不会连锁加载整个官网。</summary>
    /// <param name="sender">系列列表滚动条。</param>
    /// <param name="e">本次滚动位移。</param>
    private async void OnLibraryScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y > 0 && sender is ScrollViewer scroll &&
            scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 24)
            await ViewModel.LoadMoreAsync();
    }
    /// <summary>手动续页也作为滚动失败后的重试入口。</summary>
    /// <param name="sender">加载更多按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnLoadMore(object? sender, RoutedEventArgs e) => await ViewModel.LoadMoreAsync();
    /// <summary>系列改变后才获取该系列的本地版本。</summary>
    /// <param name="sender">系列列表。</param>
    /// <param name="e">选择事件。</param>
    private async void OnFamilySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: LibraryFamily family }) await ViewModel.SelectFamilyAsync(family);
    }
    /// <summary>搜索框 Enter 与搜索按钮使用同一逻辑。</summary>
    /// <param name="sender">搜索输入框。</param>
    /// <param name="e">用户按键；Enter 标记为已处理。</param>
    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await ViewModel.RefreshAsync(); } }
    /// <summary>刷新不会下载；也可用于 Ollama 启动后的重试。</summary>
    /// <param name="sender">刷新按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    /// <summary>用户点击后才开始联网拉取所选模型。</summary>
    /// <param name="sender">下载按钮。</param>
    /// <param name="e">点击事件。</param>
    private async void OnDownload(object? sender, RoutedEventArgs e) => await ViewModel.DownloadAsync();
    /// <summary>停止当前连接，不移除已完成的文件。</summary>
    /// <param name="sender">取消按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnCancel(object? sender, RoutedEventArgs e) => ViewModel.Cancel();
    /// <summary>只有已经安装并核对过的模型才可返回主窗口。</summary>
    /// <param name="sender">使用按钮。</param>
    /// <param name="e">点击事件。</param>
    private void OnUse(object? sender, RoutedEventArgs e)
    { if (ViewModel.CanUse) Close(ViewModel.Selected!.Option.Name); }
}
