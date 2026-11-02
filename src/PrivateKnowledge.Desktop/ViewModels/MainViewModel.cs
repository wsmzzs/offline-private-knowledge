// 用途：管理选择项目、连接、提问与引用。这里不依赖窗口控件，可独立测试竞态和取消逻辑。
using System.Collections.ObjectModel;
using PrivateKnowledge.Client;
using PrivateKnowledge.Desktop.Services;

namespace PrivateKnowledge.Desktop.ViewModels;

/// <summary>主窗口操作逻辑；所有绑定属性由界面线程调用和更新。</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>创建 HTTP 调用器的工厂；测试时替换为明确的测试实现。</summary>
    private readonly Func<string, IKnowledgeApi>? _createApi;
    /// <summary>只保存地址和上次项目的设置文件。</summary>
    private readonly SettingsStore _settingsStore;
    /// <summary>当前已经应用的设置；地址输入框的未保存内容不影响请求。</summary>
    private ClientSettings _settings = new();
    /// <summary>当前连接，地址更换时释放旧实例。</summary>
    private IKnowledgeApi? _api;
    /// <summary>窗口生命周期信号；更换连接时取消旧查询并创建新信号。</summary>
    private CancellationTokenSource _connection = new();
    /// <summary>当前提问信号；用户点取消后，迟到的结果不能覆盖界面。</summary>
    private CancellationTokenSource? _ask;
    /// <summary>当前原文查询信号；换引用时取消上一条。</summary>
    private CancellationTokenSource? _source;
    /// <summary>每次换连接递增，区分返回较晚的旧状态查询。</summary>
    private int _connectionVersion;
    /// <summary>各项目独立的内存记录；换服务器时清空，避免同名项目混用。</summary>
    private readonly Dictionary<string, ObservableCollection<QuestionTurn>> _histories = new(StringComparer.Ordinal);
    /// <summary>各项目最近观察到的修订变化；即使短暂 updating 被轮询错过，也保留更新完成提示。</summary>
    private readonly Dictionary<string, string> _knowledgeUpdates = new(StringComparer.Ordinal);
    /// <summary>服务真正监视的资料根目录，用于核对编辑的是源文件还是输出副本。</summary>
    private string? _knowledgeRootPath;
    /// <summary>服务返回的完整扫描间隔。</summary>
    private int _scanSeconds;
    /// <summary>由服务返回的检索说明，让使用者能确认语义检索是否真正启用。</summary>
    private string _retrievalHint = "检索方式：等待服务返回。";
    /// <summary>用于主界面展示的实际检索方式和模型。</summary>
    public string RetrievalHint => _retrievalHint;
    /// <summary>已关闭时禁止继续更新界面和保存选择。</summary>
    private bool _disposed;
    /// <summary>防止定时刷新与手动刷新重复执行。</summary>
    private bool _refreshing;
    /// <summary>最近一次健康检查是否允许发送。</summary>
    private bool _serviceReady;
    /// <summary>本次连接是否已经应用过服务默认模型，避免定时刷新覆盖手动选择。</summary>
    private bool _modelInitialized;
    /// <summary>下一次提问使用的模型名称；只保留在本次打开期间。</summary>
    private string? _selectedModel;
    /// <summary>最近从服务读取的默认名称，用于说明选择与默认值的区别。</summary>
    private string _defaultModel = "";
    /// <summary>用于解释默认模型缺失和模型列表为空等情况。</summary>
    private string _modelHint = "正在读取本地模型……";
    /// <summary>当前选中项目。</summary>
    private KnowledgeBaseInfo? _selectedBase;
    /// <summary>当前项目显示的记录集合。</summary>
    private ObservableCollection<QuestionTurn> _turns = [];
    /// <summary>问题输入框文字。</summary>
    private string _question = "";
    /// <summary>设置页待应用的地址。</summary>
    private string _serviceAddress = "http://127.0.0.1:5088/";
    /// <summary>页面顶部健康说明。</summary>
    private string _connectionStatus = "尚未连接";
    /// <summary>设置写入或剪贴板等非问答操作的提示。</summary>
    private string _notice = "";
    /// <summary>设置区域是否展开。</summary>
    private bool _settingsVisible;
    /// <summary>原文面板是否展开。</summary>
    private bool _sourceVisible;
    /// <summary>当前引用标题。</summary>
    private string _sourceTitle = "回答依据";
    /// <summary>回答时保留的原文片段。</summary>
    private string _sourceExcerpt = "";
    /// <summary>原文加载结果或过期说明。</summary>
    private string _sourceStatus = "";
    /// <summary>当前可访问的完整原文。</summary>
    private string _documentText = "";

    /// <summary>服务实际返回的项目列表。</summary>
    public ObservableCollection<KnowledgeBaseInfo> Bases { get; } = [];
    /// <summary>服务确认的已安装聊天模型名称。</summary>
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>集合刷新期间忽略控件产生的临时空选择。</summary>
    public bool IsUpdatingModels { get; private set; }
    /// <summary>下次提问使用的模型；等待回答时锁定，避免误认为途中已经切换。</summary>
    public string? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (IsAsking || !Set(ref _selectedModel, value)) return;
            UpdateModelHint();
            Notify(nameof(CanSend));
            NotifySetup();
        }
    }
    /// <summary>没有模型或当前模型已删除时给出明确说明。</summary>
    public string ModelHint { get => _modelHint; private set => Set(ref _modelHint, value); }
    /// <summary>当前没有提问且至少发现一个模型时允许切换。</summary>
    public bool CanSelectModel => !IsAsking && Models.Count > 0;
    /// <summary>服务刷新列表时为 true，窗口忽略集合重建造成的暂时空选择。</summary>
    public bool IsUpdatingBases { get; private set; }
    /// <summary>当前项目的内存问答列表。</summary>
    public ObservableCollection<QuestionTurn> Turns { get => _turns; private set => Set(ref _turns, value); }
    /// <summary>当前项目；切换时取消旧提问、清空输入并切换到独立历史。</summary>
    public KnowledgeBaseInfo? SelectedBase
    {
        get => _selectedBase;
        set
        {
            var previousId = _selectedBase?.Id;
            if (!Set(ref _selectedBase, value)) return;
            if (previousId != value?.Id)
            {
                CancelQuestion(); CloseSource(); Question = "";
                if (value is not null)
                {
                    if (!_histories.TryGetValue(value.Id, out var history)) _histories[value.Id] = history = [];
                    Turns = history;
                    _settings = _settings with { LastKnowledgeBaseId = value.Id };
                    SaveSettings();
                }
                else Turns = [];
            }
            Notify(nameof(ProjectTitle)); Notify(nameof(ProjectDescription)); Notify(nameof(CanSend));
            Notify(nameof(KnowledgeUpdateHint));
        }
    }
    /// <summary>当前项目标题，明确告知提问范围。</summary>
    public string ProjectTitle => SelectedBase?.Id ?? "选择项目知识库";
    /// <summary>处理状态和修订号；这里的版本不是生产工具版本。</summary>
    public string ProjectDescription => SelectedBase is null ? "服务启动后会自动发现资料文件夹。" :
        $"{SelectedBase.DisplayStatus} · {SelectedBase.DocumentCount} 份资料 · 修订 {SelectedBase.Revision} · {SelectedBase.Message}";
    /// <summary>实际资料路径；支持选择复制，方便用户在文件管理器中打开正确目录。</summary>
    public string KnowledgeLocation => string.IsNullOrWhiteSpace(_knowledgeRootPath)
        ? "资料目录：等待服务返回实际位置（旧服务请升级后重启）。" : $"资料目录：{_knowledgeRootPath}";
    /// <summary>更新完成后持续显示修订号变化；旧回答保留原修订，必须重新提问才会使用新内容。</summary>
    public string KnowledgeUpdateHint => SelectedBase is not null && _knowledgeUpdates.TryGetValue(SelectedBase.Id, out var text)
        ? text : _scanSeconds > 0 ? $"保存资料后自动核对，检查间隔约 {_scanSeconds} 秒；更新完成后修订号会增加。"
        : "保存资料后服务自动核对，更新完成后修订号会增加。";
    /// <summary>多行输入内容，限制长度同时由服务再次检查。</summary>
    public string Question { get => _question; set { if (Set(ref _question, value)) Notify(nameof(CanSend)); } }
    /// <summary>编辑中的连接地址，只有应用后生效。</summary>
    public string ServiceAddress { get => _serviceAddress; set => Set(ref _serviceAddress, value); }
    /// <summary>已经应用的服务地址；下载窗口不能读取连接设置中尚未应用的草稿。</summary>
    public string AppliedServiceAddress => _settings.ServiceAddress;
    /// <summary>窗口顶部的实际健康提示。</summary>
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    /// <summary>可供复制操作、设置错误等显示的小提示。</summary>
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    /// <summary>展开设置不会改变当前连接。</summary>
    public bool SettingsVisible { get => _settingsVisible; set { if (Set(ref _settingsVisible, value)) NotifySetup(); } }
    /// <summary>是否展示原文面板。</summary>
    public bool SourceVisible { get => _sourceVisible; private set => Set(ref _sourceVisible, value); }
    /// <summary>原文文件与章节标题。</summary>
    public string SourceTitle { get => _sourceTitle; private set => Set(ref _sourceTitle, value); }
    /// <summary>本次回答收到的引用片段，旧原文过期时仍保留供辨认。</summary>
    public string SourceExcerpt { get => _sourceExcerpt; private set => Set(ref _sourceExcerpt, value); }
    /// <summary>是否为当前版本、是否加载失败的说明。</summary>
    public string SourceStatus { get => _sourceStatus; private set => Set(ref _sourceStatus, value); }
    /// <summary>服务返回的原文全文，按纯文字显示，不执行其中的内容。</summary>
    public string DocumentText { get => _documentText; private set => Set(ref _documentText, value); }
    /// <summary>是否正在等待模型；决定取消按钮是否可用。</summary>
    public bool IsAsking => _ask is not null;
    /// <summary>只有连接、项目、问题都有效且没有重复请求时，发送按钮才可用。</summary>
    public bool CanSend => !_disposed && !IsPreparing && _serviceReady && SelectedBase?.Status == "ready" && !IsAsking &&
        SelectedModel is not null && Models.Contains(SelectedModel) &&
        !string.IsNullOrWhiteSpace(Question) && Question.Trim().Length <= 1000;

    /// <summary>注入设置与网络创建方式，构造函数不联网，设计预览也能安全创建。</summary>
    /// <param name="settingsStore">正常启动使用当前用户设置；测试用临时文件。</param>
    /// <param name="createApi">不填写时创建真实本机 HTTP 客户端。</param>
    /// <param name="environment">本机环境管理器；测试可以注入明确的假实现。</param>
    public MainViewModel(SettingsStore settingsStore, Func<string, IKnowledgeApi>? createApi = null, ILocalEnvironment? environment = null)
    {
        _settingsStore = settingsStore; _createApi = createApi;
        // 协议测试注入假 API 时不启动真实进程；正式窗口使用真实的本机进程管理器。
        _environment = environment ?? (createApi is null ? new LocalEnvironment() : null);
    }

    /// <summary>窗口首次打开后恢复模式；本机自动准备，企业只连接。</summary>
    /// <returns>首次状态检查完成的任务。</returns>
    public async Task InitializeAsync()
    {
        var existed = File.Exists(_settingsStore.FilePath);
        var loadFailed = false;
        try { _settings = _settingsStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Notice = "设置文件无法读取，已使用默认连接；原文件未被删除。"; loadFailed = true; }
        ServiceAddress = _settings.LocalAddress ?? (_settings.Mode == "Local" ? _settings.ServiceAddress : "http://127.0.0.1:5088/");
        EnterpriseAddress = _settings.EnterpriseAddress;
        ModeIndex = _settings.Mode == "Enterprise" ? 1 : 0;
        KnowledgePath = _settings.KnowledgePath ?? "";
        // 新用户先完成引导，不在刚开窗时主动下载或启动服务；旧版已有设置继续兼容。
        if (!existed || loadFailed || _settings.SetupCompleted == false)
        { OpenSetup(); return; }
        await ConnectAsync();
    }

    /// <summary>校验并应用连接。相同地址只检查状态；不同地址清空旧服务器记录。</summary>
    /// <returns>连接状态检查完成的任务；错误通过 Notice/ConnectionStatus 展示。</returns>
    public async Task ConnectAsync()
    {
        if (_disposed || IsPreparing) return;
        _connectionApplied = false;
        IsPreparing = true; _serviceReady = false; Notify(nameof(CanSend));
        try
        {
            var enterprise = ModeIndex == 1;
            var address = KnowledgeApiClient.ValidateAddress(enterprise ? EnterpriseAddress : ServiceAddress, enterprise).AbsoluteUri;
            if (enterprise && string.IsNullOrWhiteSpace(EnterpriseKey))
                throw new KnowledgeApiException("请填写 IT 提供的访问密钥；密钥只保留在本次打开期间。");
            if (enterprise && (EnterpriseKey.Length < 32 || EnterpriseKey.Any(char.IsControl)))
                throw new KnowledgeApiException("企业访问密钥至少 32 个字符，且不能包含换行；请使用 IT 提供的完整密钥。");
            var mode = enterprise ? "Enterprise" : "Local";
            var path = string.IsNullOrWhiteSpace(KnowledgePath) ? null : Path.GetFullPath(KnowledgePath);
            var changed = _api is null || address != _settings.ServiceAddress || mode != _settings.Mode ||
                (!enterprise && path != _settings.KnowledgePath) || (enterprise && _appliedKey != EnterpriseKey);
            ConnectionStatus = enterprise ? "正在连接企业知识服务……" : "正在准备本机问答环境……";
            CancelQuestion(); CloseSource();
            // 开始准备前取消旧轮询，旧服务的迟到结果不能把“准备中”误报成就绪。
            _connection.Cancel(); _connection.Dispose(); _connection = new();
            _connectionVersion++; _refreshing = false;
            if (_environment is not null)
            {
                if (enterprise) await _environment.StopAsync();
                else await _environment.PrepareAsync(address, path, _lifetime.Token);
            }
            if (_disposed) return;
            if (changed)
            {
                var newApi = _createApi?.Invoke(address) ?? new KnowledgeApiClient(address, enterprise: enterprise, apiKey: EnterpriseKey);
                _api?.Dispose(); _api = newApi;
                _modelInitialized = false; SelectedModel = null; Models.Clear();
                _histories.Clear(); SelectedBase = null; Bases.Clear(); Turns = [];
                _knowledgeUpdates.Clear(); _knowledgeRootPath = null; _scanSeconds = 0;
            }
            var remembered = !changed ? _settings.LastKnowledgeBaseId : null;
            // 旧设置迁移仍保留上次项目；绝不把访问密钥写入普通 JSON。
            if (_settings.ServiceAddress == address && _settings.Mode == mode) remembered = _settings.LastKnowledgeBaseId;
            _settings = _settings with { ServiceAddress = address, Mode = mode, KnowledgePath = path,
                EnterpriseAddress = EnterpriseAddress, LocalAddress = ServiceAddress, LastKnowledgeBaseId = remembered,
                PreferredModel = address == _settings.ServiceAddress && mode == _settings.Mode ? _settings.PreferredModel : null };
            _appliedKey = EnterpriseKey;
            _connectionApplied = true;
            NotifyEnvironment(); Notify(nameof(KnowledgeLocation)); Notify(nameof(KnowledgeUpdateHint));
            SaveSettings();
            await RefreshAsync();
            if (_serviceReady) { SettingsVisible = false; Notice = ""; }
        }
        catch (KnowledgeApiException ex) { ConnectionStatus = ex.Message; Notice = ex.Message; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        { ConnectionStatus = "本机环境准备失败，请检查目录权限和安装是否完整。"; Notice = ConnectionStatus; }
        finally { IsPreparing = false; NotifyEnvironment(); Notify(nameof(CanSend)); }
    }

    /// <summary>每隔几秒或点击刷新时核对项目与模型；不把正在生成的回答清掉。</summary>
    /// <returns>本次刷新任务；已有刷新时直接返回。</returns>
    public async Task RefreshAsync()
    {
        if (_disposed || _api is null || _refreshing) return;
        _refreshing = true;
        var version = _connectionVersion;
        var token = _connection.Token;
        try
        {
            var listTask = _api.GetBasesAsync(token);
            var healthTask = _api.GetHealthAsync(token);
            var modelsTask = _api.GetModelsAsync(token);
            await Task.WhenAll(listTask, healthTask, modelsTask);
            if (token.IsCancellationRequested || version != _connectionVersion || _disposed) return;
            var selectedId = SelectedBase?.Id ?? _settings.LastKnowledgeBaseId;
            var list = await listTask;
            foreach (var item in list)
            {
                var previous = Bases.FirstOrDefault(b => b.Id == item.Id);
                if (previous is not null && item.Revision > previous.Revision)
                    _knowledgeUpdates[item.Id] = $"资料已更新：修订 {previous.Revision} → {item.Revision}。请重新提问获取新资料的回答。";
            }
            // 只在列表内容变化时替换集合，日常轮询不会反复让列表跳动。
            if (!Bases.SequenceEqual(list))
            {
                var next = list.FirstOrDefault(b => b.Id == selectedId) ?? list.FirstOrDefault();
                // 先设置逻辑选中项，再更新集合；视图的选择同步会由窗口暂时保护。
                IsUpdatingBases = true;
                try { Bases.Clear(); foreach (var item in list) Bases.Add(item); SelectedBase = next; }
                finally { IsUpdatingBases = false; }
            }
            var health = await healthTask;
            _knowledgeRootPath = health.KnowledgeRootPath; _scanSeconds = health.ScanSeconds;
            _retrievalHint = health.Retrieval?.Mode == "hybrid"
                ? $"混合检索：关键词 + {health.Retrieval.EmbeddingModel}（{(health.Retrieval.EmbeddingOnCpu ? "CPU" : "自动分配硬件")}）。"
                : "关键词检索：当前服务未启用语义检索。";
            Notify(nameof(RetrievalHint));
            Notify(nameof(KnowledgeLocation)); Notify(nameof(KnowledgeUpdateHint));
            var catalog = await modelsTask;
            UpdateModels(catalog);
            // 默认模型被卸载后，只要嵌入模型和资料可用，仍可主动选择另一聊天模型。
            _serviceReady = health.Catalog == "ready" && health.Model is "ready" or "chat_model_missing";
            ConnectionStatus = health.Description;
            NotifySetup();
            NotifyEnvironment();
        }
        catch (OperationCanceledException) { /* 换连接或关窗口，旧状态不再展示。 */ }
        catch (KnowledgeApiException ex)
        {
            if (version == _connectionVersion && !_disposed)
            { _serviceReady = false; ConnectionStatus = ex.Message; }
        }
        finally
        {
            if (version == _connectionVersion)
            { _refreshing = false; Notify(nameof(CanSend)); Notify(nameof(CanSelectModel)); }
        }
    }

    /// <summary>首次选服务默认模型，后续保留手动选择；已删除的选择不会偷偷换成另一个模型。</summary>
    /// <param name="catalog">服务返回的默认名称和已经检查的模型列表。</param>
    private void UpdateModels(ModelCatalog catalog)
    {
        _defaultModel = catalog.DefaultModel;
        var names = catalog.Models.Select(item => item.Name).ToArray();
        IsUpdatingModels = true;
        try
        {
            if (!Models.SequenceEqual(names))
            { Models.Clear(); foreach (var name in names) Models.Add(name); }
            if (!_modelInitialized)
            {
                var initial = _settings.PreferredModel ?? catalog.DefaultModel;
                SelectedModel = names.Contains(initial) ? initial : null;
                _modelInitialized = true;
            }
            // 集合重建可能清掉控件的 SelectedItem，重新通知绑定恢复原来的选择。
            Notify(nameof(SelectedModel));
        }
        finally { IsUpdatingModels = false; }
        UpdateModelHint();
    }

    /// <summary>列表刷新和手动选择后立即更新说明，不让过期的“请选择”提示留在界面上。</summary>
    private void UpdateModelHint() => ModelHint = Models.Count == 0
        ? IsLocalMode ? "未发现本地聊天模型，请点击右上角“模型管理”选择并安装。" : "企业服务未提供聊天模型，请联系 IT。"
        : SelectedModel is null || !Models.Contains(SelectedModel)
            ? $"当前选择或默认模型不可用（默认：{_defaultModel}），请选择模型。"
            : $"服务默认：{_defaultModel} · 切换仅影响后续提问";

    /// <summary>把输入框完整问题交给当前项目，历史只用于显示，不发送给模型。</summary>
    /// <returns>本次回答或失败提示已更新的任务。</returns>
    public async Task SendAsync()
    {
        if (!CanSend || _api is null || SelectedBase is null) return;
        var turn = new QuestionTurn(Question.Trim(), SelectedBase.Id);
        var model = SelectedModel; // 在发送前固定选择；后续刷新不会改变已经提交的问题。
        var history = Turns;
        // 限制每项目本次会话最多 100 条；不持久化私有聊天内容。
        if (history.Count >= 100) history.RemoveAt(0);
        history.Add(turn); Question = "";
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        _ask = pending; Notify(nameof(IsAsking)); Notify(nameof(CanSend)); Notify(nameof(CanSelectModel));
        try
        {
            var answer = await _api.AskAsync(new(turn.KnowledgeBaseId, turn.Question, model), pending.Token);
            // 某些网络或测试实现可能取消后仍返回；必须再次检查，不能把迟到结果当成功。
            pending.Token.ThrowIfCancellationRequested();
            turn.Answer = answer.Answer;
            turn.Status = answer.Status switch { "answered" => $"根据项目资料 · 修订 {answer.KnowledgeRevision}",
                "needs_clarification" => "需要补充条件 · 请重新提交完整问题", _ => "当前资料依据不足" };
            turn.Status += answer.Model is null ? " · 未调用聊天模型" : $" · 模型：{answer.Model}";
            foreach (var source in answer.Citations) turn.Sources.Add(new(turn.KnowledgeBaseId, answer.KnowledgeRevision, source));
            turn.CanCopy = true;
        }
        catch (OperationCanceledException) { turn.Status = "已取消"; turn.Answer = "本次等待已取消，可重新输入完整问题。"; }
        catch (KnowledgeApiException ex)
        { turn.Status = "未能完成回答"; turn.Answer = ex.Message + (string.IsNullOrEmpty(ex.RequestId) ? "" : $"\n请求编号：{ex.RequestId}"); }
        finally
        {
            if (ReferenceEquals(_ask, pending)) _ask = null;
            Notify(nameof(IsAsking)); Notify(nameof(CanSend)); Notify(nameof(CanSelectModel));
        }
    }

    /// <summary>通知当前提问停止等待；信号由 SendAsync 的 using 最终释放。</summary>
    public void CancelQuestion()
    { _ask?.Cancel(); _ask = null; Notify(nameof(IsAsking)); Notify(nameof(CanSend)); Notify(nameof(CanSelectModel)); }

    /// <summary>展示引用片段，并异步读取该版本原文。</summary>
    /// <param name="link">从所点击的回答引用按钮取得的项目、版本和文档编号。</param>
    /// <returns>原文成功显示或明确失败的任务。</returns>
    public async Task OpenSourceAsync(SourceLink link)
    {
        if (_api is null || SelectedBase?.Id != link.KnowledgeBaseId || _disposed) return;
        CloseSource();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        _source = pending; SourceVisible = true;
        SourceTitle = link.Label; SourceExcerpt = link.Citation.Excerpt;
        SourceStatus = $"回答使用修订 {link.Revision} · 正在读取完整原文……";
        try
        {
            var document = await _api.GetDocumentAsync(link.Citation.DocumentId, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (document.KnowledgeBaseId != link.KnowledgeBaseId || document.DocumentId != link.Citation.DocumentId ||
                document.KnowledgeRevision != link.Revision)
                throw new KnowledgeApiException("资料版本已变化。上方保留回答时的引用片段，请重新提问获取最新依据。");
            DocumentText = document.Text;
            SourceStatus = $"完整原文 · 修订 {document.KnowledgeRevision}";
        }
        catch (OperationCanceledException) { /* 切换引用后，旧任务不能修改新面板。 */ }
        catch (KnowledgeApiException ex)
        { if (!pending.IsCancellationRequested) SourceStatus = ex.Message + " 上方片段仅代表回答时的资料。"; }
        finally { if (ReferenceEquals(_source, pending)) _source = null; }
    }

    /// <summary>关闭原文面板，同时取消未完成查询，避免它自行重新弹出。</summary>
    public void CloseSource()
    { _source?.Cancel(); _source = null; SourceVisible = false; DocumentText = ""; SourceExcerpt = ""; SourceStatus = ""; }

    /// <summary>保存小型配置，失败只提示，不影响已经连通的问答。</summary>
    private void SaveSettings()
    {
        if (_disposed) return;
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Notice = "设置无法保存，本次仍可使用；重启后可能恢复默认连接。"; }
    }

    /// <summary>窗口关闭时取消所有等待并释放网络资源，不停止独立问答服务。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelQuestion(); CloseSource();
        _setupDownload?.Cancel();
        _connection.Cancel(); _connection.Dispose(); _api?.Dispose();
        _lifetime.Cancel(); _environment?.Dispose();
    }
}
