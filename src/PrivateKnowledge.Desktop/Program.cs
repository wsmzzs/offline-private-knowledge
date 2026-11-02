// 用途：桌面进程入口，与 ASP.NET Core 服务的 Program.cs 是两个独立进程。
using Avalonia;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrivateKnowledge.Desktop;

/// <summary>选择当前电脑的 Avalonia 平台实现并启动桌面窗口。</summary>
internal static class Program
{
    /// <summary>操作系统启动 exe 时调用；STA 用于 Windows 剪贴板等桌面功能。</summary>
    /// <param name="args">命令行参数，由 Avalonia 桌面生命周期处理。</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // 同一登录用户只开一个客户端，避免两个个人窗口共用一项“随窗口停止”的后台服务。
        // 再次双击时唤回已有窗口；企业的多台电脑各有自己的客户端，不受此限制。
        var userId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        using var single = new Mutex(true, "PrivateKnowledge.Desktop." + userId, out var first);
        if (!first)
        {
            if (OperatingSystem.IsWindows())
                foreach (var process in Process.GetProcessesByName("PrivateKnowledge.Desktop"))
                    using (process)
                        if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero)
                        { ShowWindow(process.MainWindowHandle, 9); SetForegroundWindow(process.MainWindowHandle); break; }
            return;
        }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { single.ReleaseMutex(); }
    }

    /// <summary>Windows 专用：恢复已有窗口（9 表示从最小化恢复）。</summary>
    /// <param name="window">已有客户端窗口句柄。</param>
    /// <param name="command">恢复命令，固定为 9。</param>
    /// <returns>调用前窗口是否可见，不作为业务判断。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    /// <summary>Windows 专用：请求把已有客户端带到前台；系统可能限制跨应用抢焦点。</summary>
    /// <param name="window">已有客户端窗口句柄。</param>
    /// <returns>是否成功取得前台位置。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>供正式启动、设计预览和界面测试共同创建应用。</summary>
    /// <returns>尚未启动窗口的应用配置。</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
