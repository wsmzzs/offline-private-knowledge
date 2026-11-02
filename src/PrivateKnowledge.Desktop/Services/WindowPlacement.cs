// 用途：开窗时按屏幕工作区调整大小和位置，避免显示缩放后标题栏跑到屏幕外。
using Avalonia;
using Avalonia.Controls;

namespace PrivateKnowledge.Desktop.Services;

/// <summary>只调整首次打开的普通窗口；不干预用户之后的拖动、缩放或最大化。</summary>
public static class WindowPlacement
{
    /// <summary>在 Opened 事件中读取真实边框尺寸，主窗口与下载窗口共用此规则。</summary>
    /// <param name="window">已经打开的窗口；此时系统才提供完整的标题栏和边框尺寸。</param>
    public static void FitOnOpen(Window window)
    {
        if (Design.IsDesignMode || window.WindowState != WindowState.Normal) return;
        // 对话框跟随主窗口所在的屏幕；工作区会排除任务栏，不能直接用显示器总分辨率。
        var owner = window.Owner as Window;
        var screen = window.Screens.ScreenFromWindow(owner ?? window) ?? window.Screens.Primary;
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return;
        var scale = window.DesktopScaling;
        var frame = window.FrameSize ?? window.ClientSize;
        var border = new Size(Math.Max(0, frame.Width - window.ClientSize.Width),
            Math.Max(0, frame.Height - window.ClientSize.Height));
        PixelRect? ownerBounds = owner is null ? null : new PixelRect(owner.Position,
            PixelSize.FromSize(owner.FrameSize ?? owner.ClientSize, owner.DesktopScaling));
        var placement = Calculate(screen.WorkingArea, scale, window.ClientSize, border, ownerBounds);

        // 小屏幕可能连原来的最小高度也放不下；先放宽下限，设置的新高度才不会被框架弹回去。
        window.MinWidth = Math.Min(window.MinWidth, placement.ClientSize.Width);
        window.MinHeight = Math.Min(window.MinHeight, placement.ClientSize.Height);
        window.Width = placement.ClientSize.Width;
        window.Height = placement.ClientSize.Height;
        window.Position = placement.Position;
    }

    /// <summary>把界面尺寸换算成屏幕像素，扣除标题栏后计算能完整放进工作区的窗口。</summary>
    /// <param name="workArea">系统返回的物理像素区域，例如 1920×1030；副屏坐标可以为负。</param>
    /// <param name="scale">系统缩放倍数，例如 Windows 的 125% 对应 1.25。</param>
    /// <param name="desiredClient">原有内容尺寸，例如主窗口 1180×840；单位为界面逻辑尺寸。</param>
    /// <param name="border">完整窗口减去内容的差值，包含系统标题栏；单位同内容尺寸。</param>
    /// <param name="ownerBounds">可选的主窗口物理区域；对话框优先在主窗口中央显示。</param>
    /// <returns>内容大小和完整窗口左上角；边框也会留在工作区内。</returns>
    public static StartupPlacement Calculate(PixelRect workArea, double scale, Size desiredClient,
        Size border, PixelRect? ownerBounds = null)
    {
        // 留少量屏幕边距，向下取整内容尺寸，避免缩放时进位后又多出一两个像素。
        const int margin = 12;
        var width = Math.Max(1, Math.Min(desiredClient.Width,
            Math.Floor((workArea.Width - margin * 2) / scale - border.Width)));
        var height = Math.Max(1, Math.Min(desiredClient.Height,
            Math.Floor((workArea.Height - margin * 2) / scale - border.Height)));
        var outerWidth = (int)Math.Ceiling((width + border.Width) * scale);
        var outerHeight = (int)Math.Ceiling((height + border.Height) * scale);
        var centerArea = ownerBounds ?? workArea;
        // 主窗口靠近屏幕边缘时，对话框仍须收回工作区，不能只照搬主窗口的中心点。
        var left = Math.Clamp(centerArea.X + (centerArea.Width - outerWidth) / 2,
            workArea.X + margin, workArea.Right - margin - outerWidth);
        var top = Math.Clamp(centerArea.Y + (centerArea.Height - outerHeight) / 2,
            workArea.Y + margin, workArea.Bottom - margin - outerHeight);
        return new StartupPlacement(new Size(width, height), new PixelPoint(left, top));
    }
}

/// <summary>开窗计算结果；内容尺寸用于 Width/Height，物理坐标用于 Position。</summary>
/// <param name="ClientSize">去除标题栏和边框后的内容大小。</param>
/// <param name="Position">包含系统标题栏的窗口左上角。</param>
public sealed record StartupPlacement(Size ClientSize, PixelPoint Position);
