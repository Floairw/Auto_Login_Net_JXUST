using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>主题模式。</summary>
public enum ThemeMode
{
    /// <summary>跟随系统。</summary>
    System,

    Light,

    Dark,
}

/// <summary>在 WinUI 3 中切换应用主题。</summary>
public sealed class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly FrameworkElement _root;

    public ThemeService(FrameworkElement root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>当前已应用的模式。</summary>
    public ThemeMode Current { get; private set; } = ThemeMode.System;

    /// <summary>解析并应用主题。</summary>
    public void Apply(ThemeMode mode)
    {
        Current = mode;
        _root.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ResolveSystemTheme(),
        };
    }

    /// <summary>把主题名解析为枚举（容错，未知值视为跟随系统）。</summary>
    public static ThemeMode Parse(string? name)
        => Enum.TryParse<ThemeMode>(name, ignoreCase: true, out var mode) ? mode : ThemeMode.System;

    /// <summary>把枚举转为可持久化的名称。</summary>
    public static string ToName(ThemeMode mode) => mode.ToString();

    /// <summary>读取系统的应用主题设置（AppsUseLightTheme）。</summary>
    public static ElementTheme ResolveSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
        catch
        {
            return ElementTheme.Default;
        }
    }
}
