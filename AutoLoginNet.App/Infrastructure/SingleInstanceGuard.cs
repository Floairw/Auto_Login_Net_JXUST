using Microsoft.Windows.AppLifecycle;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>
/// 单实例守卫：保证同一用户会话里只存在一个「正常界面」进程。
/// <para>
/// 第二次启动时不再新建窗口，而是把这次激活重定向给已有实例，由它把主窗口带到前台；
/// 已有实例若尚未创建窗口（启动瞬间），会先记下请求，等窗口建好后再置前。
/// </para>
/// <para>
/// 设计原则是「保守失败」：守卫本身任何一步出错（运行库缺失、AppLifecycle 不可用、
/// 重定向超时等）都降级为返回 true 放行，宁可多开一个进程，也不让用户点了图标打不开。
/// </para>
/// <para>
/// 只做单实例，不涉及托盘图标：最小化到任务栏后仍可从任务栏恢复。
/// </para>
/// </summary>
public static class SingleInstanceGuard
{
    /// <summary>
    /// 实例键。用固定字符串而不是 GUID：GUID 每次安装都不同，会让同一程序的
    /// 新旧进程互相看不见，反而破坏单实例语义。
    /// </summary>
    private const string InstanceKey = "AutoLoginNet.SingleInstance";

    /// <summary>重定向激活的最长等待时间；超时按失败处理（放行启动）。</summary>
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 主实例对象需要保活：<see cref="AppInstance.Activated"/> 的事件订阅挂在它上面，
    /// 一旦被回收，第二次启动就再也唤不醒已有窗口。
    /// </summary>
    private static AppInstance? _primaryInstance;

    /// <summary>
    /// 尝试成为主实例。
    /// </summary>
    /// <param name="silentStartup">本次启动是否带 <c>--autostart</c>（计划任务静默登录）。</param>
    /// <param name="onActivated">
    /// 主实例收到「来自第二个进程的重定向激活」时的回调。
    /// 该回调由 AppLifecycle 在非 UI 线程触发，调用方需自行切回界面线程。
    /// </param>
    /// <returns>
    /// <c>true</c>：本进程是主实例（或守卫不可用而放行），继续正常启动；
    /// <c>false</c>：已有主实例，本进程应静默退出且不创建窗口。
    /// </returns>
    public static bool TryAcquire(bool silentStartup, Action? onActivated)
    {
        try
        {
            AppInstance current = AppInstance.GetCurrent();
            AppInstance primary = AppInstance.FindOrRegisterForKey(InstanceKey);

            if (primary.IsCurrent)
            {
                _primaryInstance = primary;
                if (onActivated is not null)
                {
                    primary.Activated += (_, _) => SafeInvoke(onActivated);
                }

                return true;
            }

            // 计划任务重复触发：只静默退出，绝不把窗口弹到用户面前
            if (silentStartup)
            {
                return false;
            }

            // 已有主实例：把本次激活交给它（由它把窗口带到前台），本进程随后退出
            return !TryRedirectActivation(primary, current);
        }
        catch
        {
            // 降级：单实例机制不可用时不阻断启动
            return true;
        }
    }

    /// <summary>
    /// 把本次启动的激活重定向给主实例；成功返回 <c>true</c>。
    /// 调用方是 OnLaunched（UI 线程），这里放到线程池等待，避免同步阻塞异步 WinRT 调用而造成死锁。
    /// </summary>
    private static bool TryRedirectActivation(AppInstance primary, AppInstance current)
    {
        try
        {
            Task redirect = Task.Run(async () =>
                await primary.RedirectActivationToAsync(current.GetActivatedEventArgs()));

            if (!redirect.Wait(RedirectTimeout))
            {
                return false;
            }

            return redirect.Status == TaskStatus.RanToCompletion;
        }
        catch
        {
            return false;
        }
    }

    private static void SafeInvoke(Action callback)
    {
        try
        {
            callback();
        }
        catch
        {
            // 置前失败（例如系统前台锁）不应影响主实例运行
        }
    }
}
