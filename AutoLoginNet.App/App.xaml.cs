using Microsoft.UI.Xaml;

namespace AutoLoginNet.App;

public partial class App : Application
{
    /// <summary>主窗口实例（界面辅助类需要用它取 DispatcherQueue / XamlRoot）。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>全局服务集合，在窗口创建前完成构造。</summary>
    public static AppServices Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 非打包（unpackaged）应用不会把命令行写进 args.Arguments，必须自行读取真实命令行
        string[] commandLine = StartupRouter.GetCommandLineArguments();

        // 提权子命令（注册计划任务 / 应用 DNS）：执行完立即退出，不创建窗口
        int? elevatedExitCode = ElevatedCommands.TryExecute(commandLine);
        if (elevatedExitCode is not null)
        {
            Exit();
            Environment.Exit(elevatedExitCode.Value);
            return;
        }

        // 开机自启：先完成静默登录，再把主界面显示出来。
        // 计划任务不会为用户带出窗口，因此这里必须自己 Activate；
        // 登录结果也会随之显示在首页，而不是登录完就悄悄退出。
        if (StartupRouter.IsSilentLogin(commandLine))
        {
            RunAutoStartWithInterface();
            return;
        }

        // 正常启动：构造服务并显示主窗口
        Services = new AppServices();
        MainWindow = new MainWindow();
        MainWindow.Activate();
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
