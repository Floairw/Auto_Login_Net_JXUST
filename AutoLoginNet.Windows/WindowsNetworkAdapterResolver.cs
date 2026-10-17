using AutoLoginNet.Diagnostics;
using AutoLoginNet.Windows;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace AutoLoginNet.Network;

/// <summary>
/// 解析「当前真正用于上网」的网卡。
/// <para>
/// 关键点：优先使用 Windows 路由表为<b>具体目标地址</b>选网卡（等价于 Find-NetRoute）。
/// 仅看默认网关会误选 Hyper-V / WSL / VPN 等虚拟网卡——在这些网卡上改 DNS 不仅无效，
/// 还可能直接切断网络。
/// </para>
/// </summary>
public sealed class WindowsNetworkAdapterResolver : INetworkAdapterResolver
{
    private readonly ILogService _log;

    public WindowsNetworkAdapterResolver(ILogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<AdapterInfo?> ResolveAsync(string? target = null, CancellationToken cancellationToken = default)
    {
        string destination = string.IsNullOrWhiteSpace(target) ? "1.1.1.1" : target.Trim();

        AdapterInfo? resolved = await ResolveByRoutingAsync(destination, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            return resolved;
        }

        AdapterInfo? fallback = ResolveByGateway();
        if (fallback is not null)
        {
            _log.Debug($"按默认网关回退选择网卡：{fallback.Value.Display}", "网络");
        }

        return fallback;
    }

    private async Task<AdapterInfo?> ResolveByRoutingAsync(string destination, CancellationToken cancellationToken)
    {
        // 强制以 UTF-8 输出，避免网卡中文名（如「以太网」「WLAN」）在重定向时变成乱码
        string command =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$r = Find-NetRoute -RemoteIPAddress '{destination}' | Select-Object -First 1; " +
            "if ($r) { [pscustomobject]@{ ip = $r.IPAddress; idx = $r.InterfaceIndex; alias = $r.InterfaceAlias } | ConvertTo-Json -Compress }";

        string? json = await ProcessRunner.RunPowerShellAsync(command, 15000, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("ip", out var ipElement) || ipElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string ip = ipElement.GetString() ?? string.Empty;
            if (!IsIPv4(ip))
            {
                return null;
            }

            int index = root.TryGetProperty("idx", out var indexElement) && indexElement.TryGetInt32(out int parsedIndex)
                ? parsedIndex
                : -1;

            string alias = root.TryGetProperty("alias", out var aliasElement) && aliasElement.ValueKind == JsonValueKind.String
                ? aliasElement.GetString() ?? string.Empty
                : string.Empty;

            return new AdapterInfo(index, alias, ip);
        }
        catch (Exception ex)
        {
            _log.Debug($"解析路由网卡失败：{ex.Message}", "网络");
            return null;
        }
    }

    public IReadOnlyList<AdapterInfo> ListAdapters()
    {
        var adapters = new List<AdapterInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                AdapterInfo? adapter = Describe(nic);
                if (adapter is not null)
                {
                    adapters.Add(adapter.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"枚举网卡失败：{ex.Message}", "网络");
        }

        return adapters;
    }

    /// <summary>回退方案：取第一个已连接、拥有 IPv4 网关与非回环地址的网卡。</summary>
    private static AdapterInfo? ResolveByGateway()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties properties = nic.GetIPProperties();

                bool hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && !gateway.Address.Equals(System.Net.IPAddress.Any));

                if (!hasGateway)
                {
                    continue;
                }

                AdapterInfo? adapter = Describe(nic);
                if (adapter is not null)
                {
                    return adapter;
                }
            }
        }
        catch
        {
            // 忽略枚举失败
        }

        return null;
    }

    /// <summary>把网卡描述成候选上网网卡；不符合条件（已断开、回环、隧道、无 IPv4）时返回 null。</summary>
    private static AdapterInfo? Describe(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up
            || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return null;
        }

        IPInterfaceProperties properties = nic.GetIPProperties();

        var unicast = properties.UnicastAddresses.FirstOrDefault(address =>
            address.Address.AddressFamily == AddressFamily.InterNetwork
            && !System.Net.IPAddress.IsLoopback(address.Address));

        if (unicast is null)
        {
            return null;
        }

        int index = properties.GetIPv4Properties()?.Index ?? -1;
        return new AdapterInfo(index, nic.Name, unicast.Address.ToString());
    }

    private static bool IsIPv4(string value)
        => System.Net.IPAddress.TryParse(value, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork;
}
