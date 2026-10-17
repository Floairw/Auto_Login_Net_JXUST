using AutoLoginNet.Credentials;
using AutoLoginNet.Services;

namespace AutoLoginNet.App;

/// <summary>
/// 启动模式路由。
/// <para>
/// 自启登录先于界面创建完成：静默认证期间不创建窗口、不做任何交互，
/// 结束后进程常驻，按界面设置决定「只留托盘」还是「正常显示主窗口」
/// （见 <see cref="App.RunAutoStartWithInterface"/>）。
/// </para>
/// <para>
/// 注意：非打包（unpackaged）WinUI 应用中 <c>LaunchActivatedEventArgs.Arguments</c> 恒为空字符串，
/// 命令行参数必须从 <see cref="Environment.GetCommandLineArgs"/> 读取——这是老实现里
/// <c>--autostart</c> 从未生效的根因。
/// </para>
/// </summary>
public static class StartupRouter
{
    /// <summary>自启模式参数（由计划任务传入）。</summary>
    public const string AutoStartArgument = "--autostart";

    /// <summary>本进程真实的命令行参数（不含可执行文件路径本身）。</summary>
    public static string[] GetCommandLineArguments()
    {
        string[] all = Environment.GetCommandLineArgs();
        return all.Length <= 1 ? Array.Empty<string>() : all[1..];
    }

    /// <summary>判断本次启动是否为静默自启登录。</summary>
    public static bool IsSilentLogin(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return false;
        }

        return arguments.Any(a =>
            string.Equals(a, AutoStartArgument, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 执行静默自启登录，返回进程退出码。
    /// <para>
    /// 刻意做成同步阻塞：认证必须在界面显示之前完成，
    /// 这样窗口一出现就能看到最终结果，而不是先亮一个「正在登录」再跳变。
    /// </para>
    /// <para>
    /// 这里不调用 <see cref="Environment.Exit(int)"/>：登录结果交回调用方，
    /// 由 <see cref="App.RunAutoStartWithInterface"/> 决定后续的界面形态并让进程常驻。
    /// </para>
    /// </summary>
    /// <returns>进程退出码。</returns>
    public static int RunSilentLogin()
    {
        // 兜底：即使后续逻辑抛错也不会卡住登录流程
        using var watchdog = new Timer(
            _ => Environment.Exit(4),
            null,
            TimeSpan.FromMinutes(3),
            Timeout.InfiniteTimeSpan);

        return RunSilentLoginCoreAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunSilentLoginCoreAsync()
    {
        AppServices? services = null;
        try
        {
            services = App.Services;
            services.Log.Info("以开机自启模式启动，开始静默登录，完成后按设置显示主界面或只留托盘图标", "自启");

            LoginCredential? credential = services.CredentialStore.Load();

            // 具体流程（等待网络 → 已在线上则跳过 → 带重试的认证）在 Core 中实现，便于单测覆盖
            var runner = new SilentLoginRunner(
                services.NetworkDetector,
                services.LoginService,
                services.Log,
                services.Options.Startup);

            SilentLoginOutcome outcome = await runner.RunAsync(credential).ConfigureAwait(false);
            return outcome.ExitCode;
        }
        catch (Exception ex)
        {
            services?.Log.Error($"自动登录失败：{ex}", "自启");
            return 3;
        }
    }

    /// <summary>读取参数值（形如 --name value）。</summary>
    public static string? ReadOptionValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
