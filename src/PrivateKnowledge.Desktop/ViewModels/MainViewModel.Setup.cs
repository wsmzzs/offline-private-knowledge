// 用途：把现有连接、模型下载和资料目录设置串成首次配置，不另外启动一个引导程序。
using PrivateKnowledge.Client;

namespace PrivateKnowledge.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>首次配置是否显示，完成后持久保存；设置中可再次打开。</summary>
    private bool _setupVisible;
    /// <summary>0 选择方式，1 准备环境，2 选择资料，3 核对索引。</summary>
    private int _setupStep;
    /// <summary>最后一次真实模型准备结果，检查失败时清空，不复用旧的“已安装”。</summary>
    private SetupInfo? _setupInfo;
    /// <summary>防止用户连续点两次检查。</summary>
    private bool _setupChecking;
    /// <summary>记录 ConnectAsync 是否真的应用成功，避免目录错误后误读旧连接并宣称成功。</summary>
    private bool _connectionApplied;
    /// <summary>首次配置下载的取消源；只取消本次连接，不删除已经下载的模型。</summary>
    private CancellationTokenSource? _setupDownload;
    /// <summary>准备或下载时给用户看的真实提示。</summary>
    private string _setupMessage = "首次配置只需完成一次；日常知识问答可离线使用。";
    /// <summary>当前文件层的百分比，不表示整套模型完成百分比。</summary>
    private double _setupProgress;

    /// <summary>主窗口是否显示引导。</summary>
    public bool SetupVisible => _setupVisible;
    /// <summary>设置和引导互斥，避免两个页面叠在一起。</summary>
    public bool SettingsPageVisible => SettingsVisible && !SetupVisible;
    /// <summary>聊天只在未打开设置或引导时显示。</summary>
    public bool ChatPageVisible => !SettingsVisible && !SetupVisible;
    /// <summary>欢迎页可见性。</summary>
    public bool SetupWelcomeVisible => _setupStep == 0;
    /// <summary>环境检查页可见性。</summary>
    public bool SetupEnvironmentVisible => _setupStep == 1;
    /// <summary>资料选择页可见性。</summary>
    public bool SetupKnowledgeVisible => _setupStep == 2;
    /// <summary>索引准备页可见性。</summary>
    public bool SetupIndexVisible => _setupStep == 3;
    /// <summary>当前步骤指示。</summary>
    public string SetupStepLabel => $"首次配置 · 第 {_setupStep + 1} 步 / 4";
    /// <summary>引导中的实际准备提示。</summary>
    public string SetupMessage { get => _setupMessage; private set => Set(ref _setupMessage, value); }
    /// <summary>下载中的操作锁定。</summary>
    public bool IsSetupDownloading => _setupDownload is not null;
    /// <summary>准备或下载时不允许返回上一步改变模型用途和连接。</summary>
    public bool CanSetupAction => !_disposed && !IsPreparing && !IsSetupDownloading && !_setupChecking;
    /// <summary>回退仅改变页面，不停止企业服务。</summary>
    public bool CanSetupBack => _setupStep > 0 && CanSetupAction;
    /// <summary>实际 Ollama 状态。</summary>
    public string SetupOllamaText => _setupInfo is null ? "尚未检查" : _setupInfo.OllamaReady ? "已连接" : "未连接，请安装或启动后重新检查";
    /// <summary>回答模型可使用已有选择，缺少默认值时不强迫重复下载。</summary>
    public string SetupChatText => _setupInfo is null ? "尚未检查" : $"{_setupInfo.ChatModel} · {(_setupInfo.ChatInstalled ? "已安装" : "未安装，可下载推荐模型或选择已有模型")}";
    /// <summary>检索模型是服务统一配置，不能用回答模型冒充。</summary>
    public string SetupEmbeddingText => _setupInfo is null ? "尚未检查" : !_setupInfo.EmbeddingRequired ? "当前服务仅用关键词检索" :
        $"{_setupInfo.EmbeddingModel} · {(_setupInfo.EmbeddingInstalled ? "已安装" : "未安装或配置无效")}";
    /// <summary>环境检查说明区分个人与企业，不让企业员工安装本机模型。</summary>
    public string SetupEnvironmentHint => ModeIndex == 1 ? "填写 IT 提供的地址和密钥；此流程只连接服务器，不会启动或下载本机模型。" :
        "先检查本机环境。缺少 Ollama 时打开官网安装；模型仅在点击下载后联网准备。";
    /// <summary>只有本机且 Ollama 实际就绪才允许下载推荐回答模型。</summary>
    public bool CanSetupDownloadChat => CanSetupAction && ModeIndex == 0 && IsLocalMode && _setupInfo is { OllamaReady: true, ChatInstalled: false };
    /// <summary>只有本机且检索模型确实缺失时显示可下载。</summary>
    public bool CanSetupDownloadEmbedding => CanSetupAction && ModeIndex == 0 && IsLocalMode &&
        _setupInfo is { OllamaReady: true, EmbeddingRequired: true, EmbeddingInstalled: false };
    /// <summary>进度值对应服务返回的当前文件层。</summary>
    public double SetupProgress { get => _setupProgress; private set => Set(ref _setupProgress, value); }
    /// <summary>没有字节总量时显示不定进度。</summary>
    public bool SetupProgressIndeterminate { get; private set; } = true;
    /// <summary>实际已具备回答与检索模型；不以下载连接结束作为完成。</summary>
    private bool SetupModelsReady => _setupInfo is { OllamaReady: true } &&
        (_setupInfo.ChatInstalled || (SelectedModel is not null && Models.Contains(SelectedModel))) &&
        (!_setupInfo.EmbeddingRequired || _setupInfo.EmbeddingInstalled);
    /// <summary>模型和目录连接通过后可以结束引导；没有资料仍可进入界面稍后添加，不伪报资料已就绪。</summary>
    public bool CanFinishSetup => CanSetupAction && SetupModelsReady && _connectionApplied && _serviceReady && SelectedModel is not null && Models.Contains(SelectedModel);
    /// <summary>说明就绪、更新、空目录三种不同情况。</summary>
    public string SetupIndexHint => Bases.Any(item => item.Status == "ready") ? "已有资料可用，进入后选择项目开始问答。其他项目若仍在更新，请等它就绪后再提问。" :
        Bases.Any() ? "正在整理资料或有文件需要处理。可先进入主界面查看每个项目状态，资料就绪后才能提问。" :
        "还没有知识资料。请在资料根目录下创建一个项目子文件夹，放入支持的文件；也可先进入主界面稍后添加。";

    /// <summary>新用户首次打开，或用户在设置中主动重新配置；不会自动下载模型。</summary>
    public void OpenSetup()
    {
        if (IsSetupDownloading || IsPreparing) return;
        _setupVisible = true; _setupStep = 0; _setupInfo = null;
        _settings = _settings with { SetupCompleted = false };
        SettingsVisible = false;
        SetupMessage = "选择使用方式。已有服务和模型可以直接复用，首次配置不会自动下载。";
        NotifySetup();
    }

    /// <summary>返回一步只修改视图；正在下载或准备时不能修改连接。</summary>
    public void SetupBack()
    {
        if (!CanSetupBack) return;
        _setupStep--; NotifySetup();
    }

    /// <summary>按顺序检查环境、应用目录；企业模式跳过本机资料维护。</summary>
    /// <returns>本步检查或连接完成任务。</returns>
    public async Task SetupNextAsync()
    {
        if (!CanSetupAction) return;
        if (_setupStep == 0) { _setupStep = 1; NotifySetup(); await CheckSetupAsync(); }
        else if (_setupStep == 1)
        {
            // 地址和密钥仍可编辑；离开本页前重新应用，不能拿上一次连接的成功状态通过新草稿。
            await CheckSetupAsync();
            if (!SetupModelsReady) { SetupMessage = "请先完成环境检查，准备缺少的模型或选择已安装的回答模型。"; return; }
            _setupStep = ModeIndex == 1 ? 3 : 2; NotifySetup();
        }
        else if (_setupStep == 2)
        {
            await CheckSetupAsync();
            if (_connectionApplied && SetupModelsReady) { _setupStep = 3; NotifySetup(); }
        }
    }

    /// <summary>调用现有准备流程，再读取只读模型状态；失败时不保留上次成功结果。</summary>
    /// <returns>界面提示更新完成任务。</returns>
    public async Task CheckSetupAsync()
    {
        if (!CanSetupAction) return;
        _setupChecking = true; _setupInfo = null; NotifySetup();
        try
        {
            await ConnectAsync();
            if (!_connectionApplied) { SetupMessage = ConnectionStatus; return; }
            if (_api is not ISetupApi setup) { SetupMessage = "该服务版本不支持首次配置检查，请更新配套服务后重试。"; return; }
            _setupInfo = await setup.GetSetupAsync(_lifetime.Token);
            if (SelectedModel is null && _setupInfo.ChatInstalled && Models.Contains(_setupInfo.ChatModel)) SelectedModel = _setupInfo.ChatModel;
            SetupMessage = _setupInfo.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is KnowledgeApiException or HttpRequestException or System.Text.Json.JsonException)
        { SetupMessage = "未能完成准备检查，请核对连接地址、企业密钥及服务版本后重试。"; }
        finally { _setupChecking = false; NotifySetup(); }
    }

    /// <summary>用户主动准备某一种模型；下载成功后再次检查真实安装状态。</summary>
    /// <param name="role">按钮固定传 chat 或 embedding，不接受用户任意命令。</param>
    /// <returns>下载完成、失败或用户取消后结束。</returns>
    public async Task DownloadSetupModelAsync(string role)
    {
        if (role == "chat" ? !CanSetupDownloadChat : role != "embedding" || !CanSetupDownloadEmbedding) return;
        if (_api is not ISetupApi setup) return;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _setupDownload = pending; SetupProgress = 0; SetupProgressIndeterminate = true; NotifySetup();
        try
        {
            await foreach (var progress in setup.DownloadSetupAsync(role, pending.Token))
            {
                SetupProgressIndeterminate = progress.Total == 0;
                SetupProgress = progress.Total > 0 ? progress.Completed * 100.0 / progress.Total : 0;
                SetupMessage = progress.Total > 0 ? $"{progress.Message} 当前文件：{progress.Completed / 1048576.0:F1} / {progress.Total / 1048576.0:F1} MiB" : progress.Message;
                Notify(nameof(SetupProgressIndeterminate));
                if (progress.Status == "error") return;
                if (progress.Status == "success")
                {
                    // 安装完成不等于索引完成；分开刷新实际模型和资料状态。
                    _setupInfo = await setup.GetSetupAsync(pending.Token);
                    await RefreshAsync();
                    if (SelectedModel is null && Models.Contains(_setupInfo.ChatModel)) SelectedModel = _setupInfo.ChatModel;
                }
            }
        }
        catch (OperationCanceledException) { if (!_disposed) SetupMessage = "本次下载已取消，已有文件由 Ollama 管理，重新下载时可复用。"; }
        catch (Exception ex) when (ex is KnowledgeApiException or HttpRequestException or IOException or System.Text.Json.JsonException)
        { SetupMessage = "下载未完成，请检查网络、磁盘空间和 Ollama 后重试。"; }
        finally { _setupDownload = null; NotifySetup(); }
    }

    /// <summary>只取消本次模型下载，不删除模型，不停止企业服务。</summary>
    public void CancelSetupDownload() => _setupDownload?.Cancel();

    /// <summary>明确选择经过检查的虚构样例，不把“清空路径”误当成一定读取样例。</summary>
    public void UsePublicSamples()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "service", "SampleKnowledge");
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../PublicSamples"));
        var folder = Directory.Exists(packaged) ? packaged : Directory.Exists(development) ? development : null;
        if (folder is null) { SetupMessage = "随包示例目录缺失，请使用完整发布包或选择自己的资料目录。"; return; }
        KnowledgePath = folder;
        SetupMessage = "已选择虚构示例。点击下一步后，会按这个目录重新核对资料。";
    }

    /// <summary>先成功保存完成标记再进入问答；保存失败时留在引导，不虚报下次可跳过。</summary>
    public void FinishSetup()
    {
        if (_setupStep != 3 || !CanFinishSetup) return;
        try
        {
            var completed = _settings with { SetupCompleted = true, PreferredModel = SelectedModel };
            _settingsStore.Save(completed); _settings = completed;
            _setupVisible = false; SettingsVisible = false; Notice = "首次配置已保存。资料更新由服务自动检查。";
            NotifySetup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetupMessage = "配置无法保存，请检查当前用户设置目录权限后重试。"; }
    }

    /// <summary>统一通知依赖状态，轮询更新后也能刷新“进入问答”按钮和资料提示。</summary>
    private void NotifySetup()
    {
        foreach (var name in new[] { nameof(SetupVisible), nameof(SettingsPageVisible), nameof(ChatPageVisible), nameof(SetupWelcomeVisible),
            nameof(SetupEnvironmentVisible), nameof(SetupKnowledgeVisible), nameof(SetupIndexVisible), nameof(SetupStepLabel), nameof(CanSetupAction),
            nameof(CanSetupBack), nameof(SetupOllamaText), nameof(SetupChatText), nameof(SetupEmbeddingText), nameof(SetupEnvironmentHint),
            nameof(CanSetupDownloadChat), nameof(CanSetupDownloadEmbedding), nameof(IsSetupDownloading), nameof(CanFinishSetup), nameof(SetupIndexHint) }) Notify(name);
    }
}
