// 用途：把 C# 属性变化通知给 XAML 控件，避免界面逻辑重复写刷新代码。
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PrivateKnowledge.Desktop.ViewModels;

/// <summary>可绑定对象的共同基类；属性改变后，显示它的控件会更新。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>Avalonia 订阅此事件，知道哪个属性需要重新读取。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>值发生变化时才通知界面，避免无意义重复刷新。</summary>
    /// <typeparam name="T">属性类型，如 string 或 bool。</typeparam>
    /// <param name="field">保存属性值的字段。</param>
    /// <param name="value">输入框或程序赋予的新值。</param>
    /// <param name="name">编译器自动填写属性名，例如 Question。</param>
    /// <returns>是否真正改变了值。</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    /// <summary>通知一个计算属性重新显示，例如 CanSend 随等待状态改变。</summary>
    /// <param name="name">需要重新读取的属性名称。</param>
    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
