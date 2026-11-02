// 用途：主窗口的使用模式、设置草稿和环境状态；与问答、引用逻辑分开，便于学习维护。
using PrivateKnowledge.Desktop.Services;

namespace PrivateKnowledge.Desktop.ViewModels;

/// <summary>主窗口的环境管理部分；只有已应用模式决定实际请求去向。</summary>
public sealed partial class MainViewModel
{
    /// <summary>本机进程管理器；纯协议测试允许为空。</summary>
    private readonly ILocalEnvironment? _environment;
    /// <summary>窗口关闭时取消启动等待；区别于每次连接变化的信号。</summary>
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>设置草稿的模式下标：0 本机，1 企业。</summary>
    private int _modeIndex;
    /// <summary>企业地址草稿，只有点击保存才改变当前问答目标。</summary>
    private string _enterpriseAddress = "";
    /// <summary>资料根目录草稿，空值沿用原服务配置。</summary>
    private string _knowledgePath = "";
    /// <summary>本次内存密钥；IT 也可以用环境变量预先提供，不保存到设置文件。</summary>
    private string _enterpriseKey = Environment.GetEnvironmentVariable("PRIVATEKNOWLEDGE_API_KEY") ?? "";
    /// <summary>已应用密钥，用于识别同一地址下的换密钥操作。</summary>
    private string _appliedKey = "";
    /// <summary>真实环境准备期间锁定保存按钮，避免重复启动。</summary>
    private bool _isPreparing;
    /// <summary>运行状态面板是否展开。</summary>
    private bool _runtimeVisible;

    /// <summary>正在编辑的方式，未保存前不影响已连接的服务。</summary>
    public int ModeIndex
    {
        get => _modeIndex;
        set { if (Set(ref _modeIndex, value)) { Notify(nameof(LocalSettingsVisible)); Notify(nameof(EnterpriseSettingsVisible)); Notify(nameof(ApplyLabel)); NotifySetup(); } }
    }
    /// <summary>本机设置表单显示条件。</summary>
    public bool LocalSettingsVisible => ModeIndex == 0;
    /// <summary>企业设置表单显示条件。</summary>
    public bool EnterpriseSettingsVisible => ModeIndex == 1;
    /// <summary>已经应用的模式；不能用尚未保存的下拉框来决定是否允许管理模型。</summary>
    public bool IsLocalMode => _settings.Mode != "Enterprise";
    /// <summary>企业连接地址。</summary>
    public string EnterpriseAddress { get => _enterpriseAddress; set => Set(ref _enterpriseAddress, value); }
    /// <summary>只在内存保留的共享访问密钥，界面使用密码框遮挡。</summary>
    public string EnterpriseKey { get => _enterpriseKey; set => Set(ref _enterpriseKey, value); }
    /// <summary>知识资料目录；选择文件夹按钮或输入框设置。</summary>
    public string KnowledgePath { get => _knowledgePath; set => Set(ref _knowledgePath, value); }
    /// <summary>正在准备时显示进度且禁止重复保存。</summary>
    public bool IsPreparing { get => _isPreparing; private set { if (Set(ref _isPreparing, value)) { Notify(nameof(CanPrepare)); Notify(nameof(CanManageModels)); NotifySetup(); } } }
    /// <summary>保存按钮是否允许点击。</summary>
    public bool CanPrepare => !IsPreparing && !IsSetupDownloading && !_disposed;
    /// <summary>模型下载只允许本机模式，企业由管理员提供。</summary>
    public bool CanManageModels => IsLocalMode && !IsPreparing;
    /// <summary>设置页主按钮根据草稿模式说明后续动作。</summary>
    public string ApplyLabel => ModeIndex == 0 ? "保存并进入问答" : "保存并连接";
    /// <summary>顶部简明状态，点击可看启动来源和退出行为。</summary>
    public string ModeStatus => $"{(IsLocalMode ? "本机使用" : "企业内网")} · {(IsPreparing ? "准备中" : _serviceReady ? "已就绪" : "需要检查")}";
    /// <summary>运行状态折叠面板。</summary>
    public bool RuntimeVisible { get => _runtimeVisible; set => Set(ref _runtimeVisible, value); }
    /// <summary>让普通用户理解模式和退出行为，维护人员可看到实际来源。</summary>
    public string EnvironmentDescription => IsLocalMode
        ? (_environment?.Description ?? "测试环境：不启动真实进程。")
        : "由 IT 统一管理。关闭客户端仅断开本次连接，服务器和其他同事继续使用。共享密钥可访问该实例全部知识库。";
    /// <summary>企业模式不展示服务器磁盘路径，避免让员工误以为要维护本机资料。</summary>
    public string LibraryLocation => IsLocalMode ? KnowledgeLocation : "企业知识资料由 IT 统一维护。";

    /// <summary>状态面板的重试使用已经保存的连接，不会悄悄应用设置页未保存的草稿。</summary>
    /// <returns>实际连接重新检查完成的任务。</returns>
    public async Task RetryAppliedAsync()
    {
        if (IsPreparing || _disposed) return;
        var mode = ModeIndex; var address = ServiceAddress; var enterprise = EnterpriseAddress; var path = KnowledgePath; var key = EnterpriseKey;
        ModeIndex = IsLocalMode ? 0 : 1;
        ServiceAddress = _settings.LocalAddress ?? _settings.ServiceAddress;
        EnterpriseAddress = _settings.EnterpriseAddress; KnowledgePath = _settings.KnowledgePath ?? "";
        EnterpriseKey = IsLocalMode ? key : _appliedKey;
        try { await ConnectAsync(); }
        finally { ModeIndex = mode; ServiceAddress = address; EnterpriseAddress = enterprise; KnowledgePath = path; EnterpriseKey = key; }
    }

    /// <summary>连接或健康状态变化后，更新依赖字段的界面。</summary>
    private void NotifyEnvironment()
    {
        Notify(nameof(IsLocalMode)); Notify(nameof(CanManageModels)); Notify(nameof(ModeStatus));
        Notify(nameof(EnvironmentDescription)); Notify(nameof(LibraryLocation));
    }

    /// <summary>关窗先取消问答和准备，再等待自己创建的服务正常退出。</summary>
    /// <returns>收尾完成后窗口才能真正退出。</returns>
    public async Task ShutdownAsync()
    {
        Dispose();
        if (_environment is not null) await _environment.StopAsync();
    }
}
