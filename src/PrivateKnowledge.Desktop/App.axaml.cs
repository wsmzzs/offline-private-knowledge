// 用途：加载全局 XAML，并在桌面运行时创建唯一主窗口。
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PrivateKnowledge.Desktop;

/// <summary>Avalonia 应用对象，不负责 HTTP 请求和资料处理。</summary>
public partial class App : Application
{
    /// <summary>加载 App.axaml 中的主题和公共样式。</summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>桌面平台准备好后创建窗口；测试的非桌面生命周期不会自动开窗。</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
