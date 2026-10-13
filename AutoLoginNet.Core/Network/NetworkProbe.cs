using System.Diagnostics;
using System.Net.Sockets;
using AutoLoginNet.Diagnostics;

namespace AutoLoginNet.Network;

/// <summary>
/// 基于 Ping / HttpClient / TCP 的探测实现。
/// Ping 失败时回退 TCP 连接，因为部分网络会丢弃 ICMP，仅靠 Ping 会误判为不可达。
/// </summary>
public sealed class NetworkProbe : INetworkProbe
{
    private readonly HttpClient _http;
    private readonly ILogService _log;

    public NetworkProbe(HttpClient http, ILogService log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<ProbeResult> PingAsync(
        string host,
        int timeoutMs,
        int tcpFallbackPort = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new ProbeResult(host ?? string.Empty, false, TimeSpan.Zero, "未配置探测主机", "-");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
            stopwatch.Stop();

            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                string address = reply.Address?.ToString() ?? string.Empty;
                return new ProbeResult(host, true, stopwatch.Elapsed, $"{address} 延迟 {reply.RoundtripTime} ms", "ICMP");
            }

            if (tcpFallbackPort > 0
                && await TryTcpAsync(host, tcpFallbackPort, timeoutMs, cancellationToken).ConfigureAwait(false))
            {
                stopwatch.Stop();
                return new ProbeResult(host, true, stopwatch.Elapsed, $"ICMP 被丢弃，TCP {tcpFallbackPort} 可连通", "TCP");
            }

            stopwatch.Stop();
            return new ProbeResult(host, false, stopwatch.Elapsed, $"Ping 状态：{reply.Status}", "ICMP");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            if (tcpFallbackPort > 0)
            {
                try
                {
                    if (await TryTcpAsync(host, tcpFallbackPort, timeoutMs, cancellationToken).ConfigureAwait(false))
                    {
                        return new ProbeResult(host, true, stopwatch.Elapsed, $"TCP {tcpFallbackPort} 可连通", "TCP");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 回退探测同样失败，按原始异常返回
                }
            }

            return new ProbeResult(host, false, stopwatch.Elapsed, $"{ex.GetType().Name}: {ex.Message}", "ICMP");
        }
    }

    public async Task<ProbeResult> ProbeHttpAsync(string url, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new ProbeResult(url ?? string.Empty, false, TimeSpan.Zero, "探测地址无效", "-");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            // 只要拿到任何 HTTP 响应，就说明该地址处于可达网段
            return new ProbeResult(
                uri.Host,
                true,
                stopwatch.Elapsed,
                $"HTTP {(int)response.StatusCode}",
                "HTTP");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ProbeResult(uri.Host, false, stopwatch.Elapsed, $"超过 {timeoutMs} ms 未响应", "HTTP");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Debug($"HTTP 探测 {uri} 失败：{ex.Message}", "网络");
            return new ProbeResult(uri.Host, false, stopwatch.Elapsed, ex.Message, "HTTP");
        }
    }

    private static async Task<bool> TryTcpAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
        return client.Connected;
    }
}
