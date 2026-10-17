using AutoLoginNet.App.Infrastructure;
using Microsoft.UI.Xaml;

namespace AutoLoginNet.App;

public partial class App : Application
{
    /// <summary>主窗口实例（界面辅助类需要用它取 DispatcherQueue / XamlRoot）。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>全局服务集合，在窗口创建前完成构造。</summary>
    public static AppServices Services { get; private set; } = null!;

    /// <summary>窗口创建前收到的「第二次启动」置前请求，待窗口就绪后补做。</summary>
    private volatile bool _pendingForegroundRequest;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 非打包（unpackaged）应用不会把命令行写进 args.Arguments，必须自行读取真实命令行
        string[] commandLine = StartupRouter.GetCommandLineArguments();

        // 提权子命令（注册计划任务 / 应用 DNS）：执行完立即退出，不创建窗口。
        // 必须排在单实例守卫之前：它们是主实例用 RunElevated 拉起的辅助进程，
        // 一旦被重定向到主实例就会丢掉参数、导致提权操作悄悄不执行。
        int? elevatedExitCode = ElevatedCommands.TryExecute(commandLine);
        if (elevatedExitCode is not null)
        {
            Exit();
            Environment.Exit(elevatedExitCode.Value);
            return;
        }

        bool silentLogin = StartupRouter.IsSilentLogin(commandLine);

        // 单实例守卫：第二次启动不再建窗口，而是把激活重定向给已有实例由它置前。
        // 守卫内部失败会降级放行（返回 true），因此这里不会因为单实例逻辑而启动失败。
        // --autostart 重复触发时守卫直接让它静默退出，不弹窗、不打扰用户。
        if (!SingleInstanceGuard.TryAcquire(silentLogin, OnRedirectedActivation))
        {
            Exit();
            Environment.Exit(0);
            return;
        }

        // 开机自启：先完成静默登录，再把主界面显示出来。
        // 计划任务不会为用户带出窗口，因此这里必须自己 Activate；
        // 登录结果也会随之显示在首页，而不是登录完就悄悄退出。
        if (silentLogin)
        {
            RunAutoStartWithInterface();
            return;
        }

        // 正常启动：构造服务并显示主窗口
        Services = new AppServices();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        FlushPendingForegroundRequest();
    }

    /// <summary>
    /// 单实例守卫的重定向激活回调：把主窗口带到前台。
    /// <para>
    /// 该回调由 AppLifecycle 在非 UI 线程触发，所以必须切回界面线程再操作窗口；
    /// 若此刻窗口还没创建（启动瞬间收到第二次双击），先记下请求，等窗口建好后再置前。
    /// </para>
    /// </summary>
    private void OnRedirectedActivation()
    {
        var window = MainWindow;
        if (window is null)
        {
            _pendingForegroundRequest = true;
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                window.BringToForeground();
            }
            catch
            {
                // 置前失败（例如系统前台锁）不影响已有窗口继续使用
            }
        });
    }

    /// <summary>窗口创建完成前收到的置前请求，在这里补做一次（窗口已在前台时是幂等的）。</summary>
    private void FlushPendingForegroundRequest()
    {
        if (!_pendingForegroundRequest)
        {
            return;
        }

        _pendingForegroundRequest = false;
        MainWindow?.BringToForeground();
    }

    /// <summary>
    /// 开机自启路径：完成登录后拉起并置前主界面，随后退出本进程（窗口随即关闭）。
    /// <para>
    /// 之所以在界面显示后再退出，是因为计划任务的语义是「一次性执行完毕」：
    /// 让它一直挂着会长期占用一个进程，而用户此刻已经看到结果。
    /// </para>
    /// </summary>
    private void RunAutoStartWithInterface()
    {
        Services = new AppServices();

        int exitCode;
        try
        {
            exitCode = StartupRouter.RunSilentLogin();
        }
        catch (Exception ex)
        {
            Services.Log.Error($"自启登录流程异常：{ex}", "自启");
            exitCode = 3;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
        MainWindow.BringToForeground();

        // 退出码与自启登录结果一致，便于排查计划任务历史记录
        Exit();
        Environment.Exit(exitCode);
    }

    /// <summary>把未处理异常写入日志，避免应用静默崩溃且不留线索。</summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Services?.Log.Error($"未处理异常：{e.Exception}", "应用");
        }
        catch
        {
            // 日志不可用时忽略
        }
    }
}
