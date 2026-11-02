// 用途：验证不同显示缩放、任务栏和副屏坐标下，完整窗口始终留在可操作的屏幕区域。
using Avalonia;
using PrivateKnowledge.Desktop.Services;
using Xunit;

namespace PrivateKnowledge.ClientTests;

/// <summary>验证窗口摆放规则；不依赖测试电脑恰好使用哪种缩放或分辨率。</summary>
public sealed class WindowPlacementTests
{
    /// <summary>覆盖用户实际的 125% 缩放，以及更小屏幕和高缩放；标题栏也必须能放下。</summary>
    /// <param name="width">工作区物理宽度。</param>
    /// <param name="height">扣除任务栏后的物理高度。</param>
    /// <param name="scale">系统显示缩放倍数。</param>
    [Theory]
    [InlineData(1920, 1030, 1.25)]
    [InlineData(1366, 728, 1)]
    [InlineData(1920, 1030, 1.5)]
    [InlineData(1920, 1030, 2)]
    public void EntireFrameFitsWorkArea(int width, int height, double scale)
    {
        var area = new PixelRect(0, 0, width, height);
        var border = new Size(16, 39);
        var result = WindowPlacement.Calculate(area, scale, new Size(1180, 840), border);
        AssertInside(area, result, border, scale);
        Assert.True(result.ClientSize.Width <= 1180 && result.ClientSize.Height <= 840);
    }

    /// <summary>左侧副屏坐标为负，主窗口又靠近底部时，下载对话框仍完整留在该副屏。</summary>
    [Fact]
    public void DialogStaysOnOwnersMonitorEvenNearItsEdge()
    {
        var area = new PixelRect(-1920, 40, 1920, 990);
        var owner = new PixelRect(-1100, 600, 1000, 800);
        var border = new Size(16, 39);
        var result = WindowPlacement.Calculate(area, 1.25, new Size(840, 780), border, owner);
        AssertInside(area, result, border, 1.25);
        Assert.True(result.Position.X < 0);
    }

    /// <summary>大屏幕不无故缩小现有内容尺寸，只调整居中位置。</summary>
    [Fact]
    public void LargeScreenKeepsPreferredSize()
    {
        var desired = new Size(1180, 840);
        var result = WindowPlacement.Calculate(new PixelRect(0, 0, 3840, 2100), 1.5, desired, new Size(16, 39));
        Assert.Equal(desired, result.ClientSize);
    }

    /// <summary>用最终物理边界验收，防止只验证内容而漏掉导致本次问题的系统标题栏。</summary>
    /// <param name="area">允许摆放的工作区。</param>
    /// <param name="result">实际计算结果。</param>
    /// <param name="border">测试模拟的系统标题栏和边框占用。</param>
    /// <param name="scale">本次测试缩放。</param>
    private static void AssertInside(PixelRect area, StartupPlacement result, Size border, double scale)
    {
        Assert.True(result.Position.X >= area.X && result.Position.Y >= area.Y);
        Assert.True(result.Position.X + Math.Ceiling((result.ClientSize.Width + border.Width) * scale) <= area.Right);
        Assert.True(result.Position.Y + Math.Ceiling((result.ClientSize.Height + border.Height) * scale) <= area.Bottom);
    }
}
