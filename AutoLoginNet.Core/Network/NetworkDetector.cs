using AutoLoginNet.Configuration;
using AutoLoginNet.Diagnostics;

namespace AutoLoginNet.Network;

/// <summary>
/// 网络环境探测：并行检查校园网认证服务器与外网可达性，
/// 并按「校园网可达 + 外网不可达 ⇒ 需要认证」的规则给出结论。
/// </summary>
public sealed class NetworkDetector : INetworkDetector
{
    private readonly AppOptions _options;
    private readonly INetworkProbe _probe;
    private readonly ILogService _log;

    public NetworkDetector(AppOptions options, INetworkProbe probe, ILogService log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        var startup = _options.Startup;

        for (int attempt = 1; attempt <= startup.NetworkReadyMaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsNetworkAvailable())
            {
                _log.Info("网络连接已就绪", "网络");
                return true;
            }

            _log.Debug($"网络尚未就绪，{startup.NetworkReadyRetryDelayMs} ms 后重试（{attempt}/{startup.NetworkReadyMaxRetries}）", "网络");

            try
            {
                await Task.Delay(startup.NetworkReadyRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        _log.Warn("等待网络就绪超时，将继续尝试认证", "网络");
        return false;
    }

    public async Task<NetworkSnapshot> DetectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 两项探测互不依赖，并行执行以缩短整体等待时间
            Task<ProbeResult> campusTask = _probe.ProbeHttpAsync(
                _options.Probes.CampusProbeUrl,
                _options.Probes.CampusProbeTimeoutMs,
                cancellationToken);

            Task<ProbeResult> externalTask = _probe.PingAsync(
                _options.Probes.ExternalProbeHost,
                _options.Probes.ExternalProbeTimeoutMs,
                _options.Probes.ExternalFallbackTcpPort,
                cancellationToken);

            await Task.WhenAll(campusTask, externalTask).ConfigureAwait(false);

            ProbeResult campus = await campusTask.ConfigureAwait(false);
            ProbeResult external = await externalTask.ConfigureAwait(false);
            AuthRequirement requirement = Evaluate(campus.Reachable, external.Reachable);

            var snapshot = new NetworkSnapshot(campus, external, requirement, DateTimeOffset.Now);
            _log.Info(
                $"网络检测完成：校园网 {(campus.Reachable ? "可达" : "不可达")}，外网 {(external.Reachable ? "可达" : "不可达")} ⇒ {requirement}",
                "网络");

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"网络检测失败：{ex.Message}", "网络");
            return NetworkSnapshot.Failure(ex.Message);
        }
    }

    /// <summary>由两项探测结果判定认证需求（纯函数，便于测试）。</summary>
    public static AuthRequirement Evaluate(bool campusReachable, bool externalReachable)
        => (campusReachable, externalReachable) switch
        {
            (true, false) => AuthRequirement.Required,   // 校园网可达、外网不可达 → 需要认证
            (true, true) => AuthRequirement.NotRequired, // 已认证上网
            (false, true) => AuthRequirement.NotRequired, // 不在校园网内（例如流量/家庭网络）
            (false, false) => AuthRequirement.Unknown,   // 都不可达，交由上层提示
        };

    private static bool IsNetworkAvailable()
    {
        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                return false;
            }

            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel);
        }
        catch
        {
            return false;
        }
    }
}
