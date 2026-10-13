using AutoLoginNet.Network;

namespace AutoLoginNet.Services;

/// <summary>Ping 测试服务（供界面「网络诊断」使用）。</summary>
public interface IPingService
{
    Task<ProbeResult> PingAsync(string host, int timeoutMs = 2000, CancellationToken cancellationToken = default);
}

/// <summary>基于 <see cref="INetworkProbe"/> 的 Ping 服务，Ping 失败时回退 HTTPS(443) 探测。</summary>
public sealed class PingService : IPingService
{
    private readonly INetworkProbe _probe;

    public PingService(INetworkProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public Task<ProbeResult> PingAsync(string host, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return _probe.PingAsync(host.Trim(), timeoutMs, tcpFallbackPort: 443, cancellationToken);
    }
}
