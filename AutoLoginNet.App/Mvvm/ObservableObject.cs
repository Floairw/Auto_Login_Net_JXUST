using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoLoginNet.App.Mvvm;

/// <summary>
/// 轻量 MVVM 基类。项目刻意不引入 CommunityToolkit.Mvvm：
/// 只有两个小类，自带实现可以少一个依赖、少一层源生成器。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>设置字段并在值变化时通知界面。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>批量通知依赖属性。</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}
