namespace AutoLoginNet.Network;

/// <summary>当前网络是否需要校园网认证。</summary>
public enum AuthRequirement
{
    /// <summary>需要认证：校园网可达但外网不可达。</summary>
    Required,

    /// <summary>无需认证：已在线，或当前并非校园网环境。</summary>
    NotRequired,

    /// <summary>无法判定：两次探测均失败，通常意味着未接入网络。</summary>
    Unknown,
}

/// <summary>单次网络探测的结果。</summary>
/// <param name="Target">探测目标（地址或主机名）。</param>
/// <param name="Reachable">是否可达。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Detail">补充说明（解析到的 IP、Ping 状态或异常信息）。</param>
/// <param name="AttemptedMethod">实际生效的探测方式：ICMP / TCP / HTTP。</param>
public readonly record struct ProbeResult(
    string Target,
    bool Reachable,
    TimeSpan Elapsed,
    string Detail,
    string AttemptedMethod)
{
    /// <summary>耗时展示文本。</summary>
    public string ElapsedText => Reachable ? $"{Elapsed.TotalMilliseconds:F0} ms" : "--";

    /// <summary>供界面展示的一行摘要。</summary>
    public string Summary => Reachable
        ? $"{Target} 可达 · {ElapsedText}{(string.IsNullOrEmpty(Detail) ? string.Empty : " · " + Detail)}"
        : $"{Target} 不可达{(string.IsNullOrEmpty(Detail) ? string.Empty : " · " + Detail)}";
}

/// <summary>一次完整的网络环境快照。</summary>
/// <param name="Campus">校园网认证服务器探测结果。</param>
/// <param name="External">外网探测结果。</param>
/// <param name="Requirement">判定结论。</param>
/// <param name="CheckedAt">探测时间。</param>
public sealed record NetworkSnapshot(
    ProbeResult Campus,
    ProbeResult External,
    AuthRequirement Requirement,
    DateTimeOffset CheckedAt)
{
    /// <summary>是否需要认证。</summary>
    public bool NeedsAuthentication => Requirement == AuthRequirement.Required;

    /// <summary>外网是否可用。</summary>
    public bool IsOnline => External.Reachable;

    /// <summary>界面用的一句话结论。</summary>
    public string Conclusion => Requirement switch
    {
        AuthRequirement.Required => "检测到校园网需要认证，建议立即登录",
        AuthRequirement.NotRequired when External.Reachable => "外网已连通，无需重复认证",
        AuthRequirement.NotRequired => "当前不在校园网环境，无需认证",
        _ => "网络状态未知，请检查是否已连接网络",
    };

    /// <summary>探测过程中的异常快照。</summary>
    public static NetworkSnapshot Failure(string reason)
    {
        var probe = new ProbeResult("探测失败", false, TimeSpan.Zero, reason, "-");
        return new NetworkSnapshot(probe, probe, AuthRequirement.Unknown, DateTimeOffset.Now);
    }
}

/// <summary>单次探测能力：ICMP Ping 与 HTTP 可达性。</summary>
public interface INetworkProbe
{
    /// <summary>Ping 主机；失败时按 <paramref name="tcpFallbackPort"/> 回退 TCP 探测。</summary>
    Task<ProbeResult> PingAsync(string host, int timeoutMs, int tcpFallbackPort = 0, CancellationToken cancellationToken = default);

    /// <summary>HTTP 探测指定地址（只要拿到响应即视为可达，与状态码无关）。</summary>
    Task<ProbeResult> ProbeHttpAsync(string url, int timeoutMs, CancellationToken cancellationToken = default);
}

/// <summary>网络环境探测。</summary>
public interface INetworkDetector
{
    /// <summary>等待网络就绪（存在已连接的非回环网卡）。</summary>
    Task<bool> WaitForReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>探测当前网络环境并给出认证需求结论。</summary>
    Task<NetworkSnapshot> DetectAsync(CancellationToken cancellationToken = default);
}
