using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AutoLoginNet.Network;

/// <summary>
/// DNS 服务器延迟探测：直接向目标发出一个真实的 UDP DNS 查询并计时。
/// <para>
/// 为什么不复用 Ping：DNS 服务器普遍<b>丢弃 ICMP</b>（实测 8.8.8.8、119.29.29.29、114.114.114.114
/// 在本机均 ping 不通），但它们的 53 端口工作完全正常。用 ICMP 判定会把可用服务器误判为不可达，
/// 而且「ping 通」也完全不等于「解析快」。因此这里测的是真正的解析往返耗时。
/// </para>
/// </summary>
public interface IDnsLatencyProbe
{
    /// <summary>
    /// 测量指定 DNS 服务器的解析延迟。
    /// </summary>
    /// <param name="serverAddress">DNS 服务器 IPv4 地址。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <param name="sourceAddress">
    /// 探测包必须使用的本地源地址（要测的那张网卡自己的 IPv4 地址）。
    /// <para>
    /// 传了就<b>绑定该地址再发包</b>，因此测到的确实是这张网卡的链路；
    /// 不传则交给系统路由表选源地址——那样测到的可能不是用户指定的那张网卡。
    /// </para>
    /// </param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ProbeResult> MeasureAsync(
        string serverAddress,
        int timeoutMs = 2000,
        string? sourceAddress = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDnsLatencyProbe" />
public sealed class UdpDnsLatencyProbe : IDnsLatencyProbe
{
    /// <summary>探测用域名。选国内可达性最好的公共域名，避免域名本身影响测量结果。</summary>
    private const string ProbeDomain = "www.baidu.com";

    private static ushort _queryId = (ushort)Random.Shared.Next(1, ushort.MaxValue);

    public async Task<ProbeResult> MeasureAsync(
        string serverAddress,
        int timeoutMs = 2000,
        string? sourceAddress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!IPAddress.TryParse(serverAddress, out var server) || server.AddressFamily != AddressFamily.InterNetwork)
        {
            return new ProbeResult(serverAddress, false, TimeSpan.Zero, "不是合法的 IPv4 地址", "DNS");
        }

        // 指定了源地址就必须真的从它发出：UdpClient 绑不上该地址时不会改用默认路由，
        // 否则「测的是 A 网卡」会悄悄变成「测的是系统选的那张」。
        IPEndPoint? boundEndPoint = null;
        if (!string.IsNullOrWhiteSpace(sourceAddress))
        {
            if (!IPAddress.TryParse(sourceAddress, out var localAddress) || localAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                return new ProbeResult(serverAddress, false, TimeSpan.Zero, $"指定的网卡地址「{sourceAddress}」不是合法的 IPv4 地址", "DNS");
            }

            boundEndPoint = new IPEndPoint(localAddress, 0);
        }

        ushort queryId = NextQueryId();
        byte[] query = BuildQuery(queryId, ProbeDomain);

        try
        {
            using var client = boundEndPoint is null
                ? new UdpClient(server.AddressFamily)
                : new UdpClient(boundEndPoint);
            client.Connect(server, 53);
            await client.SendAsync(query, cancellationToken).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            UdpReceiveResult response = await client.ReceiveAsync(linked.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!IsValidResponse(response.Buffer, queryId))
            {
                return new ProbeResult(serverAddress, false, stopwatch.Elapsed, "响应不是有效的 DNS 应答", "DNS");
            }

            string sourceNote = boundEndPoint is null
                ? string.Empty
                : $"（源地址 {boundEndPoint.Address}）";

            return new ProbeResult(
                serverAddress,
                true,
                stopwatch.Elapsed,
                $"{ProbeDomain} 解析 {(long)stopwatch.Elapsed.TotalMilliseconds} ms{sourceNote}",
                "DNS");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ProbeResult(serverAddress, false, stopwatch.Elapsed, $"超过 {timeoutMs} ms 无应答", "DNS");
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();

            // 部分网络会丢弃 UDP 53，此时退回 TCP 53 再确认一次（同样从指定源地址发出）
            bool tcpReachable = await TryTcpAsync(server, timeoutMs, boundEndPoint, cancellationToken).ConfigureAwait(false);
            if (tcpReachable)
            {
                return new ProbeResult(serverAddress, true, stopwatch.Elapsed, $"UDP 不可用，TCP 53 可连通（{ex.SocketErrorCode}）", "TCP");
            }

            string failure = boundEndPoint is null
                ? $"SocketException: {ex.SocketErrorCode}"
                : $"从网卡地址 {boundEndPoint.Address} 发出查询失败：{ex.SocketErrorCode}";

            return new ProbeResult(serverAddress, false, stopwatch.Elapsed, failure, "DNS");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProbeResult(serverAddress, false, stopwatch.Elapsed, $"{ex.GetType().Name}: {ex.Message}", "DNS");
        }
    }

    private static ushort NextQueryId() => unchecked(_queryId = _queryId == ushort.MaxValue ? (ushort)1 : (ushort)(_queryId + 1));

    /// <summary>构造一个标准的 A 记录查询报文（RD=1，单问题，类 IN）。</summary>
    internal static byte[] BuildQuery(ushort queryId, string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var buffer = new List<byte>(domain.Length + 18);

        void WriteUInt16(ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value & 0xFF));
        }

        WriteUInt16(queryId);   // ID
        WriteUInt16(0x0100);    // Flags: 标准查询 + 期望递归
        WriteUInt16(1);         // QDCOUNT
        WriteUInt16(0);         // ANCOUNT
        WriteUInt16(0);         // NSCOUNT
        WriteUInt16(0);         // ARCOUNT

        foreach (string label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"域名标签长度非法：{label}", nameof(domain));
            }

            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);          // 域名结束
        WriteUInt16(1);         // QTYPE = A
        WriteUInt16(1);         // QCLASS = IN

        return buffer.ToArray();
    }

    /// <summary>
    /// 校验应答：ID 匹配、QR=1（确实是应答）、RCODE=0（服务器正常处理）。
    /// 不要求答案数量大于 0——能收到一个正确回包就已经证明往返可用，
    /// 而这里要测的正是往返耗时。
    /// </summary>
    internal static bool IsValidResponse(byte[] response, ushort expectedId)
    {
        if (response.Length < 12)
        {
            return false;
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(response);
        if (id != expectedId)
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        bool isResponse = (flags & 0x8000) != 0;
        int rcode = flags & 0x000F;

        return isResponse && rcode == 0;
    }

    private static async Task<bool> TryTcpAsync(IPAddress server, int timeoutMs, IPEndPoint? boundEndPoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = boundEndPoint is null ? new TcpClient() : new TcpClient(boundEndPoint);
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            await client.ConnectAsync(server, 53, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
