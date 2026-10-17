using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.Auth;
using AutoLoginNet.Configuration;
using AutoLoginNet.Credentials;
using AutoLoginNet.Diagnostics;
using AutoLoginNet.Hosts;
using AutoLoginNet.Network;
using AutoLoginNet.Services;
using AutoLoginNet.Windows;
using Microsoft.UI.Dispatching;

namespace AutoLoginNet.App;

/// <summary>
/// 组合根：集中构造并持有全部服务。
/// <para>
/// 项目刻意不引入 DI 容器：服务数量少、生命周期与进程一致，
/// 显式构造比容器更易读，也让依赖关系一眼可见。
/// </para>
/// </summary>
public sealed class AppServices
{
    private readonly INetworkProbe _probe;

    /// <summary>「本次运行是否已经发起过启动时自动登录」的会话级标记。</summary>
    private int _autoLoginClaimed;

    public AppServices()
    {
        // 日志要先就绪，后续所有服务与配置读取才能留下线索
        var bootstrapOptions = new AppOptions();
        bootstrapOptions.Normalize();

        Log = new FileLogService(
            AppPaths.LogFile,
            bootstrapOptions.Logging.MaxFileSizeMb,
            bootstrapOptions.Logging.RetainedFileCount,
            bootstrapOptions.Logging.InMemoryCapacity);

        Settings = new JsonSettingsService(AppPaths.SettingsFile, Log);
        Options = Settings.Load();

        HttpClient = CreateHttpClient(Options);
        _probe = new NetworkProbe(HttpClient, Log);

        CredentialStore = new JsonCredentialStore(
            AppPaths.CredentialsFile,
            new Windows.DpapiCredentialProtector(),
            Log);

        Authenticator = new DrComAuthenticator(Options, HttpClient, Log);
        NetworkDetector = new NetworkDetector(Options, _probe, Log);
        LoginService = new LoginService(Authenticator, NetworkDetector, CredentialStore, Log);
        LogoutService = new DrComLogoutService(Options, HttpClient, Log);
        PingService = new PingService(_probe);
        DnsLatencyProbe = new UdpDnsLatencyProbe();

        AdapterResolver = new WindowsNetworkAdapterResolver(Log);

        DnsService = new WindowsDnsService(
            AdapterResolver,
            Options.Auth.Host,
            Log);

        AutoStart = new ScheduledTaskAutoStartService(
            Log,
            Path.Combine(AppPaths.DataDirectory, "autostart.json"));

        HostsFile = new WindowsHostsFileService(Options, Log);

        UiSettings = new UiSettingsStore(Path.Combine(AppPaths.DataDirectory, "ui-settings.json"));
        UiSettings.Load();

        Log.Info(
            $"应用启动 v{typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "?"}，认证服务器 {Options.Auth.Host}:{Options.Auth.Port}",
            "应用");
    }

    public ISettingsService Settings { get; }

    public AppOptions Options { get; }

    public ILogService Log { get; }

    /// <summary>当前使用的 HttpClient（HTTP 超时变更后会被重建）。</summary>
    public HttpClient HttpClient { get; private set; }

    public ICredentialStore CredentialStore { get; }

    public IAuthenticator Authenticator { get; private set; }

    public ILoginService LoginService { get; private set; }

    /// <summary>校园网注销（断开在线认证会话）。</summary>
    public ILogoutService LogoutService { get; private set; }

    public INetworkDetector NetworkDetector { get; private set; }

    public IPingService PingService { get; }

    /// <summary>DNS 延迟探测（真实 UDP 查询，而非 ICMP）。</summary>
    public IDnsLatencyProbe DnsLatencyProbe { get; }

    /// <summary>上网网卡解析（路由表优先，网关回退）。DNS 相关功能与托盘菜单共用。</summary>
    public INetworkAdapterResolver AdapterResolver { get; }

    public IDnsService DnsService { get; }

    public IAutoStartService AutoStart { get; }

    /// <summary>hosts 文件读写（校园网域名静态解析）。</summary>
    public IHostsFileService HostsFile { get; }

    public UiSettingsStore UiSettings { get; }

    /// <summary>
    /// 开机自启路径的静默登录结果：true 成功、false 失败、null 表示本进程没有走过静默登录。
    /// <para>
    /// 界面据此还原「已在线」状态——否则用户打开自启实例后，注销按钮会因为「还没检测过网络环境」而不可用。
    /// </para>
    /// </summary>
    public bool? StartupLoginSucceeded { get; private set; }

    /// <summary>
    /// 界面线程队列。视图模型用它把自己的更新合并为一次界面刷新，
    /// 避免后台高频事件（例如日志写入）直接冲击 UI 线程。
    /// </summary>
    public DispatcherQueue Dispatcher
        => App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// 认领「本次运行的启动时自动登录」机会。
    /// <para>
    /// 每次返回首页都会新建 <see cref="ViewModels.DashboardViewModel"/>，
    /// 视图模型内部的标记会随之归零并再次自动登录（表现为凭据被重复保存、重复认证）。
    /// 因此把标记放在与进程同生命周期的组合根上，只有第一个调用者拿到 true。
    /// </para>
    /// </summary>
    /// <returns>本次调用是否为唯一的认领者。</returns>
    public bool TryClaimStartupAutoLogin()
        => Interlocked.CompareExchange(ref _autoLoginClaimed, 1, 0) == 0;

    /// <summary>
    /// 记录静默登录结果，并认领「启动时自动登录」机会。
    /// <para>
    /// 认领必须发生在界面创建之前：静默登录已经登录过一次，界面若再自动登录就会重复认证。
    /// </para>
    /// </summary>
    public void MarkStartupLoginCompleted(bool succeeded)
    {
        StartupLoginSucceeded = succeeded;
        TryClaimStartupAutoLogin();
    }

    /// <summary>
    /// 重建 HttpClient 并让依赖它的服务指向新实例。
    /// 仅用于「HTTP 超时」被修改的场景——该属性无法在实例创建后调整。
    /// </summary>
    public void RecreateHttpClient()
    {
        HttpClient old = HttpClient;
        HttpClient = CreateHttpClient(Options);
        old.Dispose();

        Authenticator = new DrComAuthenticator(Options, HttpClient, Log);
        NetworkDetector = new NetworkDetector(Options, _probe, Log);
        LoginService = new LoginService(Authenticator, NetworkDetector, CredentialStore, Log);
        LogoutService = new DrComLogoutService(Options, HttpClient, Log);

        Log.Info($"HTTP 客户端已重建（超时 {Options.Auth.HttpTimeoutSeconds} 秒）", "配置");
    }

    private static HttpClient CreateHttpClient(AppOptions options)
        => new() { Timeout = TimeSpan.FromSeconds(options.Auth.HttpTimeoutSeconds) };
}
