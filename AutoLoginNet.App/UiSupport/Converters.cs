using AutoLoginNet.Auth;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AutoLoginNet.App.UiSupport;

/// <summary>bool → Visibility 转换器（WinUI 3 未内置）。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>为 true 时返回 Collapsed 而不是 Visible（用于「空状态」提示）。</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary>DateTimeOffset → 本地时间文本。</summary>
public sealed class TimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is DateTimeOffset timestamp
            ? timestamp == DateTimeOffset.MinValue
                ? "—"
                : timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss")
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool → InfoBar 严重级别（true 为错误）。</summary>
public sealed class StatusSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool isError && isError
            ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error
            : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>form 用的登录方式选项。</summary>
public sealed class OperatorOption
{
    public OperatorOption(OperatorKind kind)
    {
        Kind = kind;
        DisplayName = kind.ToDisplayName();
    }

    public OperatorKind Kind { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}
