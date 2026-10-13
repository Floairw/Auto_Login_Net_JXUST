namespace AutoLoginNet.Configuration;

/// <summary>认证服务器与登录端点。</summary>
public sealed class AuthOptions
{
    /// <summary>Dr.COM ePortal 认证服务器地址。</summary>
    public string Host { get; set; } = "10.17.8.18";

    /// <summary>认证服务器端口。</summary>
    public int Port { get; set; } = 801;

    /// <summary>登录接口路径。</summary>
    public string LoginPath { get; set; } = "/eportal/portal/login";

    /// <summary>JSONP 回调名。</summary>
    public string LoginCallback { get; set; } = "dr1003";

    /// <summary>门户前端脚本版本。</summary>
    public string JsVersion { get; set; } = "4.1.3";

    /// <summary>HTTP 请求超时（秒）。</summary>
    public int HttpTimeoutSeconds { get; set; } = 12;
}

/// <summary>网络探测参数。</summary>
public sealed class ProbeOptions
{
    /// <summary>校园网探测地址，用于判断是否处于校园网内。</summary>
    public string CampusProbeUrl { get; set; } = "http://10.17.8.18/";

    /// <summary>校园网探测超时（毫秒）。</summary>
    public int CampusProbeTimeoutMs { get; set; } = 3000;

    /// <summary>外网探测主机。</summary>
    public string ExternalProbeHost { get; set; } = "baidu.com";

    /// <summary>外网探测超时（毫秒）。</summary>
    public int ExternalProbeTimeoutMs { get; set; } = 1500;

    /// <summary>Ping 失败时回退 TCP 探测的端口。</summary>
    public int ExternalFallbackTcpPort { get; set; } = 443;
}

/// <summary>日志与滚动策略。</summary>
public sealed class LoggingOptions
{
    /// <summary>单个日志文件上限（MB）。</summary>
    public int MaxFileSizeMb { get; set; } = 5;

    /// <summary>保留的历史日志份数。</summary>
    public int RetainedFileCount { get; set; } = 5;

    /// <summary>内存中保留的最近日志条数，供界面实时展示。</summary>
    public int InMemoryCapacity { get; set; } = 1000;
}

/// <summary>启动与网络就绪等待策略。</summary>
public sealed class StartupOptions
{
    /// <summary>等待网络就绪的最大重试次数。</summary>
    public int NetworkReadyMaxRetries { get; set; } = 12;

    /// <summary>每次重试的间隔（毫秒）。</summary>
    public int NetworkReadyRetryDelayMs { get; set; } = 1000;

    /// <summary>已完成认证后，后台巡检的间隔（分钟）；0 表示关闭巡检。</summary>
    public int HealthCheckIntervalMinutes { get; set; } = 5;

    /// <summary>登录失败时的自动重试次数。</summary>
    public int LoginRetryCount { get; set; } = 2;

    /// <summary>登录重试间隔（秒）。</summary>
    public int LoginRetryDelaySeconds { get; set; } = 3;
}

/// <summary>应用配置根对象。</summary>
public sealed class AppOptions
{
    public AuthOptions Auth { get; set; } = new();

    public ProbeOptions Probes { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public StartupOptions Startup { get; set; } = new();

    public ToolsOptions Tools { get; set; } = new();

    /// <summary>
    /// 登录接口的绝对地址（不含查询串）。
    /// </summary>
    public Uri BuildLoginUri()
    {
        string path = Auth.LoginPath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            path = "/eportal/portal/login";
        }
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var builder = new UriBuilder(Uri.UriSchemeHttp, Auth.Host, Auth.Port, path);
        return builder.Uri;
    }

    /// <summary>
    /// 登录请求的查询串（不含前导 <c>?</c>），已对账号与密码做 URL 编码。
    /// <para>
    /// 实测结论：本校园网 ePortal 的 <c>/eportal/portal/login</c> <b>只从查询串读取账号</b>，
    /// 完全不解析 POST body（把账号放在 body 里会得到「无法获取用户认证账号！」）。
    /// 密码同样需要出现在查询串中，否则返回「无法获取用户认证密码！」。
    /// 因此这里把两者都编码进查询串——这是服务器端的既有约定，客户端没有别的选择。
    /// </para>
    /// <para>
    /// 由此带来的风险由其他层收敛：日志写入前会把 <c>user_password</c> 脱敏，
    /// 且该请求仅发往校园网内网的明文 HTTP 门户。
    /// </para>
    /// </summary>
    public string BuildLoginQuery(string account, string password)
    {
        var pairs = new List<KeyValuePair<string, string>>(6)
        {
            new("callback", Auth.LoginCallback),
            new("login_method", "1"),
            new("user_account", account),
            new("user_password", password),
            new("jsVersion", Auth.JsVersion),
            new("terminal_type", "1"),
        };

        return string.Join(
            '&',
            pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <summary>修正越界或非法的配置值，保证运行时不会因配置写错而崩溃。</summary>
    public void Normalize()
    {
        Auth ??= new AuthOptions();
        Probes ??= new ProbeOptions();
        Logging ??= new LoggingOptions();
        Startup ??= new StartupOptions();
        Tools ??= new ToolsOptions();

        Auth.Host = string.IsNullOrWhiteSpace(Auth.Host) ? "10.17.8.18" : Auth.Host.Trim();
        Auth.Port = Math.Clamp(Auth.Port, 1, 65535);
        Auth.LoginPath = string.IsNullOrWhiteSpace(Auth.LoginPath) ? "/eportal/portal/login" : Auth.LoginPath.Trim();
        Auth.LoginCallback = string.IsNullOrWhiteSpace(Auth.LoginCallback) ? "dr1003" : Auth.LoginCallback.Trim();
        Auth.JsVersion = string.IsNullOrWhiteSpace(Auth.JsVersion) ? "4.1.3" : Auth.JsVersion.Trim();
        Auth.HttpTimeoutSeconds = Math.Clamp(Auth.HttpTimeoutSeconds, 1, 120);

        Probes.CampusProbeUrl = string.IsNullOrWhiteSpace(Probes.CampusProbeUrl)
            ? $"http://{Auth.Host}/"
            : Probes.CampusProbeUrl.Trim();
        Probes.CampusProbeTimeoutMs = Math.Clamp(Probes.CampusProbeTimeoutMs, 200, 30000);
        Probes.ExternalProbeHost = string.IsNullOrWhiteSpace(Probes.ExternalProbeHost)
            ? "baidu.com"
            : Probes.ExternalProbeHost.Trim();
        Probes.ExternalProbeTimeoutMs = Math.Clamp(Probes.ExternalProbeTimeoutMs, 200, 30000);
        Probes.ExternalFallbackTcpPort = Math.Clamp(Probes.ExternalFallbackTcpPort, 1, 65535);

        Logging.MaxFileSizeMb = Math.Clamp(Logging.MaxFileSizeMb, 1, 512);
        Logging.RetainedFileCount = Math.Clamp(Logging.RetainedFileCount, 1, 50);
        Logging.InMemoryCapacity = Math.Clamp(Logging.InMemoryCapacity, 100, 10000);

        Startup.NetworkReadyMaxRetries = Math.Clamp(Startup.NetworkReadyMaxRetries, 1, 300);
        Startup.NetworkReadyRetryDelayMs = Math.Clamp(Startup.NetworkReadyRetryDelayMs, 100, 60000);
        Startup.HealthCheckIntervalMinutes = Math.Clamp(Startup.HealthCheckIntervalMinutes, 0, 240);
        Startup.LoginRetryCount = Math.Clamp(Startup.LoginRetryCount, 0, 10);
        Startup.LoginRetryDelaySeconds = Math.Clamp(Startup.LoginRetryDelaySeconds, 1, 300);

        Tools.InternetSpeedTestUrl = string.IsNullOrWhiteSpace(Tools.InternetSpeedTestUrl)
            ? "http://wangsuceshi.bmcx.com"
            : Tools.InternetSpeedTestUrl.Trim();
        Tools.CampusSpeedTestUrl = string.IsNullOrWhiteSpace(Tools.CampusSpeedTestUrl)
            ? "http://speedtest.jxust.edu.cn"
            : Tools.CampusSpeedTestUrl.Trim();
        Tools.PingTargets = (Tools.PingTargets ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Tools.PingTargets.Count == 0)
        {
            Tools.PingTargets.Add("baidu.com");
        }

        // hosts 规则需剔除非法 IP 与空域名，避免写入无效行
        Tools.HostsEntries = (Tools.HostsEntries ?? new List<Hosts.HostsEntry>())
            .Where(e => e is { IsValidIp: true } && !string.IsNullOrWhiteSpace(e.HostName))
            .Select(e => e with { HostName = e.HostName.Trim(), Comment = e.Comment?.Trim() ?? string.Empty })
            .ToList();

        if (Tools.HostsEntries.Count == 0)
        {
            Tools.HostsEntries = ToolsOptions.CreateDefaultHostsEntries();
        }

        // 内置规则在版本升级后可能新增。已保存的 appsettings.json 会整组覆盖默认值，
        // 于是新增的内置规则在老用户机器上永远不出现（实测：2.0.7 新增的两条校内测速规则
        // 在已有配置的机器上完全不生效）。这里把「用户配置里没有的内置规则」补进来：
        //   * 用户在界面里改过注释或地址的内置规则不会被覆盖（判重按 IP+域名，不看注释）；
        //   * 用户自己加的规则一律保留（即便域名同样以 jxust.edu.cn 结尾）；
        //   * 完全自定义过的用户不会被塞进一堆内置规则——只有新增的内置规则会被补上。
        var builtIn = ToolsOptions.CreateDefaultHostsEntries();
        var existing = new HashSet<string>(
            Tools.HostsEntries.Select(e => $"{e.IpAddress}|{e.HostName}"),
            StringComparer.OrdinalIgnoreCase);
        var missing = builtIn
            .Where(e => existing.Add($"{e.IpAddress}|{e.HostName}"))
            .ToList();

        if (missing.Count > 0)
        {
            Tools.HostsEntries.AddRange(missing);
        }
    }
}
