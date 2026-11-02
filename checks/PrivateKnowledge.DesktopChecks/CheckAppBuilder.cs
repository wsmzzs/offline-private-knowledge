// 用途：真实客户端界面验收的图形平台，加载真实 XAML，通过 Skia 渲染，不伪造窗口截图。
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PrivateKnowledge.Desktop;

[assembly: AvaloniaTestApplication(typeof(PrivateKnowledge.DesktopChecks.CheckAppBuilder))]
namespace PrivateKnowledge.DesktopChecks;

/// <summary>真实渲染但不占用用户的桌面窗口；网络是否真实由各验收用例明确说明。</summary>
public static class CheckAppBuilder
{
    /// <summary>建立图形验收平台。</summary><returns>真实应用构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
