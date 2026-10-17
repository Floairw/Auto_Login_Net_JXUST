using Microsoft.Win32;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>本程序托盘图标在任务栏里的显示状态。</summary>
public enum TrayPromotionState
{
    /// <summary>系统里还没有本程序的托盘设置条目（首次运行、还没打开过「^」时就是这样）。</summary>
    NotListed = 0,

    /// <summary>条目存在，但图标被折叠进「^」溢出面板（或用户手动关掉过）。</summary>
    Hidden = 1,

    /// <summary>图标显示在任务栏（通知区域可见区）上。</summary>
    Promoted = 2,
}

/// <summary><see cref="TrayIconPromotion.EnsurePromoted"/> 的结果。</summary>
/// <param name="State">写完之后的显示状态。</param>
/// <param name="Changed">本次是否真的改动了注册表（改动后需要重新添加托盘图标才会生效）。</param>
public readonly record struct TrayPromotionResult(TrayPromotionState State, bool Changed);

/// <summary>
/// 把本程序的托盘图标设为「在任务栏上显示」。
/// <para>
/// Windows 11 默认把新程序的托盘图标折叠进任务栏右下角的「^」溢出面板，用户会以为程序没有托盘图标。
/// 系统没有提供「申请常驻显示」的 API，显示状态只存在
/// <c>HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;</c> 的 <c>IsPromoted</c>（DWORD，1 = 显示在任务栏，
/// 缺失或 0 = 折叠进「^」），资源管理器在添加图标时读这个值。
/// </para>
/// <para>
/// 键名 <c>&lt;id&gt;</c> 由资源管理器按图标身份生成（同一个 exe 的不同图标会有不同 id），无法自己造键，
/// 而且条目是**懒创建**的：用户第一次打开「^」或「设置 → 个性化 → 任务栏 → 其他系统托盘图标」之后才会出现。
/// 所以调用方必须周期性重试，直到条目出现。
/// </para>
/// <para>
/// 只在 <c>IsPromoted</c> **缺失**时写 1；用户手动关掉过（值为 0）时不覆盖，尊重用户的系统设置。
/// 任何异常都吞掉并当作「系统还没记录」，托盘这一层永远不能影响主程序。
/// </para>
/// </summary>
public static class TrayIconPromotion
{
    private const string SettingsKeyPath = @"Control Panel\NotifyIconSettings";
    private const string PathValueName = "ExecutablePath";
    private const string PromotedValueName = "IsPromoted";

    /// <summary>
    /// 确保本程序的托盘图标显示在任务栏上；条目不存则返回 <see cref="TrayPromotionState.NotListed"/>（调用方稍后重试）。
    /// </summary>
    /// <param name="executablePath">要匹配的 exe 路径；默认当前进程。</param>
    public static TrayPromotionResult EnsurePromoted(string? executablePath = null)
    {
        string? target = Normalize(executablePath ?? Environment.ProcessPath);
        if (target is null)
        {
            return new TrayPromotionResult(TrayPromotionState.NotListed, false);
        }

        try
        {
            using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
            if (settings is null)
            {
                return new TrayPromotionResult(TrayPromotionState.NotListed, false);
            }

            bool listed = false;
            bool promoted = false;
            bool changed = false;

            foreach (string name in settings.GetSubKeyNames())
            {
                if (!TryReadExecutablePath(settings, name, out string? entryPath)
                    || !string.Equals(entryPath, target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                listed = true;

                using RegistryKey? entry = settings.OpenSubKey(name, writable: true);
                if (entry is null)
                {
                    continue;
                }

                object? value = entry.GetValue(PromotedValueName);
                if (value is null)
                {
                    // 系统还没写过这个值 = 默认折叠进「^」，这是我们可以放心改的状态
                    entry.SetValue(PromotedValueName, 1, RegistryValueKind.DWord);
                    promoted = true;
                    changed = true;
                }
                else if (Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1)
                {
                    promoted = true;
                }
                // 值为 0：用户自己关掉过，保持原样
            }

            if (promoted)
            {
                return new TrayPromotionResult(TrayPromotionState.Promoted, changed);
            }

            return new TrayPromotionResult(
                listed ? TrayPromotionState.Hidden : TrayPromotionState.NotListed,
                false);
        }
        catch
        {
            // 读/写注册表被拒（策略、权限）时静默放弃：托盘显示只是体验问题
            return new TrayPromotionResult(TrayPromotionState.NotListed, false);
        }
    }

    private static bool TryReadExecutablePath(RegistryKey settings, string name, out string? path)
    {
        path = null;

        try
        {
            using RegistryKey? entry = settings.OpenSubKey(name, writable: false);
            path = Normalize(entry?.GetValue(PathValueName) as string);
            return path is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? Normalize(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
