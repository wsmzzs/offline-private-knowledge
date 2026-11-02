// 用途：官网模型库搜索、五项续页、按需版本读取、下载和选用；不接触知识库内容。
using System.Collections.ObjectModel;
using System.Text.Json;
using PrivateKnowledge.Client;

namespace PrivateKnowledge.Desktop.ViewModels;

/// <summary>官网版本卡片，附加本机是否已安装的状态。</summary>
/// <param name="option">来自服务的配置建议。</param>
/// <param name="installed">已通过本地聊天能力检查的名称集合。</param>
/// <param name="stateKnown">Ollama 查询成功才为 true；失败不能误报尚未安装。</param>
public sealed class DownloadModelItem(DownloadOption option, bool installed, bool stateKnown = true)
{
    /// <summary>包含下载名称和配置说明的原始选项。</summary>
    public DownloadOption Option { get; } = option;
    /// <summary>是否可直接选用；下载结束后重新查询再确定。</summary>
    public bool Installed { get; } = installed;
    /// <summary>是否已经成功读取本机安装状态。</summary>
    public bool StateKnown { get; } = stateKnown;
    /// <summary>卡片右侧状态。</summary>
    public string InstallLabel => !StateKnown ? "安装状态未确认" : Installed ? "已安装 · 可选用" : "尚未安装";
}

/// <summary>下载操作独立于主窗口问答状态；窗口关掉时终止本次连接。</summary>
public sealed class ModelDownloadsViewModel : ObservableObject, IDisposable
{
    /// <summary>负责读取官网系列、版本及下载。</summary>
    private readonly IModelDownloadsApi _downloads;
    /// <summary>读取已安装模型，确认下载后的模型确实能被主窗口选中。</summary>
    private readonly IKnowledgeApi _models;
    /// <summary>整个窗口关闭时取消在途请求。</summary>
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>当前下载的取消信号，取消后仍可再点击下载。</summary>
    private CancellationTokenSource? _download;
    /// <summary>避免异步结果在窗口关闭后写回。</summary>
    private bool _disposed;
    /// <summary>列表正在查询或文件正在下载时锁定选择。</summary>
    private bool _busy;
    /// <summary>当前选择的具体版本。</summary>
    private DownloadModelItem? _selected;
    /// <summary>面向用户的操作结果。</summary>
    private string _status = "正在检查本机模型……";
    /// <summary>当前文件下载百分比，不代表整个模型已经安装。</summary>
    private double _percent;
    /// <summary>不知道文件大小时显示不确定进度。</summary>
    private bool _indeterminate;
    /// <summary>已下载的字节数提示，只有 Ollama 提供总量才显示。</summary>
    private string _progressText = "";
    /// <summary>搜索框草稿，点击搜索或按 Enter 后才发送。</summary>
    private string _searchText = "";
    /// <summary>当前列表实际使用的词，不随未提交草稿变化。</summary>
    private string _activeQuery = "";
    /// <summary>当前选中的模型系列，版本按需读取。</summary>
    private LibraryFamily? _selectedFamily;
    /// <summary>续页游标，失败保留以便重试。</summary>
    private int? _nextOffset;
    /// <summary>续页期间保留原列表和滚动位置。</summary>
    private bool _loadingMore;
    /// <summary>官网来源和旧缓存状态说明。</summary>
    private string _catalogHint = "浏览模型库需要联网，只发送搜索词；私有资料不参与查询。";
    /// <summary>任一已展示目录使用旧缓存时持续提示，不能被另一条成功请求抹掉。</summary>
    private bool _catalogStale;

    /// <summary>已加载的系列，滚动续页时只追加。</summary>
    public ObservableCollection<LibraryFamily> Families { get; } = [];
    /// <summary>搜索输入框草稿。</summary>
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    /// <summary>只通过 SelectFamilyAsync 修改，避免属性 setter 隐式联网。</summary>
    public LibraryFamily? SelectedFamily => _selectedFamily;
    /// <summary>是否正在读取下一批。</summary>
    public bool LoadingMore { get => _loadingMore; private set { if (Set(ref _loadingMore, value)) NotifyActions(); } }
    /// <summary>空游标表示搜索结果已到末尾。</summary>
    public bool CanLoadMore => CanChoose && _nextOffset is not null;
    /// <summary>滚动失败也能点击按钮重试。</summary>
    public string MoreLabel => LoadingMore ? "正在加载……" : _nextOffset is null ? "已到搜索结果末尾" : "加载更多（每批 5 项）";
    /// <summary>当前目录的来源及缓存提示。</summary>
    public string CatalogHint { get => _catalogHint; private set => Set(ref _catalogHint, value); }
    /// <summary>版本列表标题，明确先选系列再选版本。</summary>
    public string VersionsTitle => SelectedFamily is null ? "选择一个模型系列" : $"{SelectedFamily.Name} · {Items.Count} 个本地版本";

    /// <summary>当前系列的具体版本。</summary>
    public ObservableCollection<DownloadModelItem> Items { get; } = [];
    /// <summary>当前项；忙碌时不允许切换，避免把 A 的进度误显示在 B 上。</summary>
    public DownloadModelItem? Selected
    {
        get => _selected;
        set { if (CanChoose && Set(ref _selected, value)) NotifyActions(); }
    }
    /// <summary>正在查询或下载。</summary>
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) NotifyActions(); } }
    /// <summary>列表是否允许点击。</summary>
    public bool CanChoose => !Busy && !LoadingMore;
    /// <summary>仅未安装模型可下载，避免重复点击消耗流量。</summary>
    public bool CanDownload => CanChoose && Selected is { Installed: false, StateKnown: true };
    /// <summary>已安装模型直接使用，不需要重新下载。</summary>
    public bool CanUse => CanChoose && Selected is { Installed: true };
    /// <summary>取消按钮仅在下载请求仍存在时可用。</summary>
    public bool CanCancel => _download is not null;
    /// <summary>中文结果、故障或下一步指引。</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>当前文件层百分比。</summary>
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    /// <summary>未知大小阶段滚动显示，不能用虚假的百分比代替。</summary>
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    /// <summary>当前文件的下载大小说明。</summary>
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    /// <summary>注入同一客户端的下载接口和模型接口；窗口负责释放真实客户端。</summary>
    /// <param name="downloads">下载协议，测试可替换。</param>
    /// <param name="models">安装清单协议，测试可替换。</param>
    public ModelDownloadsViewModel(IModelDownloadsApi downloads, IKnowledgeApi models)
    { _downloads = downloads; _models = models; }

    /// <summary>打开窗口或重试连接时刷新；本方法只查询，不下载模型。</summary>
    /// <returns>列表查询完成的任务；失败在 Status 中解释。</returns>
    public async Task RefreshAsync()
    {
        if (!CanChoose || _disposed) return;
        Busy = true; Status = "正在读取官方模型库……";
        try
        {
            var query = SearchText.Trim();
            var page = await _downloads.GetLibraryAsync(query, 0, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _activeQuery = query; _nextOffset = page.NextOffset;
            Families.Clear(); foreach (var family in page.Items) Families.Add(family);
            _selectedFamily = Families.FirstOrDefault();
            Items.Clear(); _selected = null;
            Notify(nameof(SelectedFamily)); Notify(nameof(Selected)); Notify(nameof(VersionsTitle));
            _catalogStale = page.IsStale; CatalogHint = SourceHint(_catalogStale);
            if (_selectedFamily is null)
            { Status = "本批没有可用于问答的系列；可继续加载下一批或更换搜索词。"; return; }
            await LoadItemsAsync(_lifetime.Token);
            Status = "先选模型系列，再选具体版本；只有点击下载才会拉取模型文件。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) Status = Explain(ex); }
        finally { if (!_disposed) Busy = false; }
    }

    /// <summary>读取官网版本与安装状态；安装查询失败仍保留版本，不把未知状态画成已安装。</summary>
    /// <param name="token">本次查询的关闭或取消信号。</param>
    /// <param name="readVersions">false 表示下载后仅更新安装状态，复用已经展示的版本。</param>
    /// <returns>列表更新完成。</returns>
    private async Task LoadItemsAsync(CancellationToken token, bool readVersions = true)
    {
        IReadOnlyList<DownloadOption> options = Items.Select(item => item.Option).ToArray();
        if (readVersions)
        {
            var versions = await _downloads.GetVersionsAsync(_selectedFamily!.Name, token);
            token.ThrowIfCancellationRequested();
            options = versions.Items; _catalogStale |= versions.IsStale; CatalogHint = SourceHint(_catalogStale);
        }
        var selectedName = Selected?.Option.Name;
        var names = new HashSet<string>();
        Exception? failure = null;
        try { names = (await _models.GetModelsAsync(token)).Models.Select(model => model.Name).ToHashSet(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { failure = ex; }
        token.ThrowIfCancellationRequested();
        Items.Clear();
        foreach (var option in options) Items.Add(new(option, names.Contains(option.Name), failure is null));
        // 优先恢复原版本，否则展示一个待安装版本；不再依赖“第二项是推荐档位”的旧清单约定。
        // 这里只选中，不自动下载；用户仍需看大小与配置后点击按钮。
        _selected = Items.FirstOrDefault(item => item.Option.Name == selectedName)
            ?? Items.FirstOrDefault(item => !item.Installed) ?? Items.FirstOrDefault();
        Notify(nameof(Selected)); Notify(nameof(VersionsTitle)); NotifyActions();
        if (failure is not null) throw failure;
    }

    /// <summary>下载期间只发模型标签；收到成功后再次读取安装清单，才允许“使用此模型”。</summary>
    /// <returns>成功、失败或取消后的任务。</returns>
    public async Task DownloadAsync()
    {
        if (!CanDownload || _disposed) return;
        var name = Selected!.Option.Name;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = cancel; Busy = true; NotifyActions();
        Percent = 0; Indeterminate = true; ProgressText = ""; Status = "正在连接本机 Ollama……";
        try
        {
            var succeeded = false;
            await foreach (var progress in _downloads.DownloadAsync(name, cancel.Token))
            {
                cancel.Token.ThrowIfCancellationRequested();
                Status = progress.Message;
                Indeterminate = progress.Total == 0 && progress.Status is not ("success" or "error");
                Percent = progress.Total > 0 ? 100d * progress.Completed / progress.Total : 0;
                ProgressText = progress.Total > 0
                    ? $"当前文件：{progress.Completed / 1048576d:F1} / {progress.Total / 1048576d:F1} MB（{Percent:F0}%）；全部文件校验后才算完成。" : "";
                if (progress.Status == "error") return;
                if (progress.Status == "success") { succeeded = true; break; }
            }
            if (!succeeded) throw new KnowledgeApiException("下载提前结束，尚未确认成功，请重试。");
            await LoadItemsAsync(cancel.Token, readVersions: false);
            if (Selected?.Option.Name != name || !Selected.Installed)
                throw new KnowledgeApiException("文件拉取已结束，但尚未在本地聊天模型列表中确认，请刷新检查。");
            Percent = 100;
            Status = "下载完成。点击“使用此模型”返回问答，后续回答即可使用它。";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        { if (!_disposed) Status = "已取消本次下载连接。已接收文件由 Ollama 管理，重新下载时可复用；其他程序的下载不受此按钮控制。"; }
        catch (Exception ex) { if (!_disposed) Status = Explain(ex); }
        finally
        {
            _download = null;
            if (!_disposed) { Busy = false; Indeterminate = false; NotifyActions(); }
        }
    }

    /// <summary>只停止本窗口的拉取连接，不删除已有模型文件。</summary>
    public void Cancel() => _download?.Cancel();

    /// <summary>追加下一批系列，失败不清空现有结果；同一批不会同时请求两次。</summary>
    /// <returns>追加完成的任务，错误在状态区说明。</returns>
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || _disposed) return;
        LoadingMore = true;
        try
        {
            var offset = _nextOffset!.Value;
            var page = await _downloads.GetLibraryAsync(_activeQuery, offset, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            foreach (var family in page.Items)
                if (!Families.Any(item => item.Name == family.Name)) Families.Add(family);
            // 异常服务返回相同位置时终止续页，避免无限重复请求。
            _nextOffset = page.NextOffset > offset ? page.NextOffset : null;
            _catalogStale |= page.IsStale; CatalogHint = SourceHint(_catalogStale);
            Status = $"已加载 {Families.Count} 个系列，可继续选择和查看版本。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) Status = Explain(ex); }
        finally { if (!_disposed) LoadingMore = false; }
    }

    /// <summary>用户点到某系列才读取其版本，不提前查询其他系列的全部版本。</summary>
    /// <param name="family">来自当前已加载列表的系列。</param>
    /// <returns>版本读取结束的任务。</returns>
    public async Task SelectFamilyAsync(LibraryFamily family)
    {
        if (!CanChoose || _disposed || (_selectedFamily?.Name == family.Name && Items.Count > 0)) return;
        Busy = true; _selectedFamily = family;
        Items.Clear(); _selected = null;
        Notify(nameof(SelectedFamily)); Notify(nameof(Selected)); Notify(nameof(VersionsTitle));
        Status = "正在读取所选系列的本地版本……";
        try { await LoadItemsAsync(_lifetime.Token); Status = "查看配置估算后，选择具体版本下载。"; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) Status = Explain(ex); }
        finally { if (!_disposed) Busy = false; }
    }

    /// <summary>官网失败时不能把旧缓存称为最新结果。</summary>
    /// <param name="stale">服务返回的过期标记。</param>
    /// <returns>目录来源说明。</returns>
    private static string SourceHint(bool stale) => stale ? "官网暂不可用，显示上次缓存；版本可能已变化。"
        : "来源：Ollama 官方模型库 · 每批最多 5 个系列，具体版本按需读取。";

    /// <summary>更新由选择、下载状态共同决定的按钮。</summary>
    private void NotifyActions()
    { Notify(nameof(CanChoose)); Notify(nameof(CanDownload)); Notify(nameof(CanUse)); Notify(nameof(CanCancel)); Notify(nameof(CanLoadMore)); Notify(nameof(MoreLabel)); }

    /// <summary>把常见网络错误翻译成用户能采取的下一步。</summary>
    /// <param name="ex">查询或下载过程中捕获的异常。</param>
    /// <returns>不包含堆栈的说明。</returns>
    private static string Explain(Exception ex) => ex switch
    {
        KnowledgeApiException known => known.Message,
        OperationCanceledException => "等待下载进度超时，请检查网络后重试。",
        HttpRequestException or IOException => "连接中断。请确认问答服务、Ollama 已启动，并检查网络和磁盘空间后重试。",
        JsonException => "下载信息无法读取，请确认客户端和问答服务都已更新。",
        _ => $"下载操作未完成（{ex.GetType().Name}），请重试或联系维护人员。"
    };

    /// <summary>关窗先取消请求，迟到的下载结果不再修改已关闭界面。</summary>
    public void Dispose()
    { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
