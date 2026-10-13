using System.Text.RegularExpressions;
using AutoLoginNet.Diagnostics;
using AutoLoginNet.Windows;

namespace AutoLoginNet.Network;

/// <summary>
/// 基于 <c>netsh</c> 的 DNS 读写实现。
/// <para>
/// 两个关键取舍：
/// 1) 网卡只用 <b>名称</b> 定位。实测 <c>netsh interface ipv4 show|set dnsservers</c>
///    <b>不接受</b> <c>idx=</c> 参数（会直接报 “'idx' is not a valid argument”），
///    只接受位置参数形式的接口名。名称单独作为 argv 传参即可，由 .NET 负责加引号，
///    因此不需要也不应该手工拼接引号。
/// 2) 写入后立即回读并比对，只有真正生效才报告成功——避免「命令退出码为 0 但设置未生效」
///    这种最容易误导用户的假成功。
/// </para>
/// </summary>
public sealed partial class WindowsDnsService : IDnsService
{
    private readonly INetworkAdapterResolver _resolver;
    private readonly string _campusTarget;
    private readonly ILogService _log;

    public WindowsDnsService(INetworkAdapterResolver resolver, string campusTarget, ILogService log)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _campusTarget = string.IsNullOrWhiteSpace(campusTarget) ? "1.1.1.1" : campusTarget;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<DnsProvider> Providers { get; } = new[]
    {
        new DnsProvider("114 DNS", "114.114.114.114", "114.114.115.115"),
        new DnsProvider("阿里 DNS", "223.5.5.5", "223.6.6.6"),
        new DnsProvider("腾讯 DNSPod", "119.29.29.29", "119.28.28.28"),
        new DnsProvider("Google DNS", "8.8.8.8", "8.8.4.4"),
        new DnsProvider("Cloudflare", "1.1.1.1", "1.0.0.1"),
    };

    public Task<AdapterInfo?> GetActiveAdapterAsync(CancellationToken cancellationToken = default)
        => _resolver.ResolveAsync(_campusTarget, cancellationToken);

    public async Task<DnsPair?> GetCurrentDnsAsync(AdapterInfo adapter, CancellationToken cancellationToken = default)
    {
        var outcome = await ProcessRunner.RunAsync(
            "netsh.exe",
            new[] { "interface", "ipv4", "show", "dnsservers", adapter.Name },
            8000,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _log.Warn($"读取 DNS 失败（{adapter.Display}）：{FirstLine(outcome.StdErr)}", "DNS");
            return null;
        }

        return ParseDnsConfiguration(outcome.StdOut);
    }

    public async Task<bool> ApplyAsync(
        AdapterInfo adapter,
        DnsPair dns,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dns.Primary))
        {
            return false;
        }

        if (await TryApplyWithNetshAsync(adapter, dns, cancellationToken).ConfigureAwait(false)
            || await TryApplyWithPowerShellAsync(adapter, dns, cancellationToken).ConfigureAwait(false))
        {
            // 回读确认，避免「命令成功但设置未生效」的假成功
            DnsPair? applied = await GetCurrentDnsAsync(adapter, cancellationToken).ConfigureAwait(false);
            if (applied is not null && AddressesMatch(applied.Value.Primary, dns.Primary))
            {
                _log.Info($"已将 {adapter.Display} 的 DNS 设置为 {applied.Value.AddressText}", "DNS");
                return true;
            }

            _log.Error(
                $"DNS 设置未生效（{adapter.Display}）：期望首选 {dns.Primary}，实际 {applied?.AddressText ?? "无"}",
                "DNS");
            return false;
        }

        _log.Error($"设置 DNS 失败（{adapter.Display}）：netsh 与 PowerShell 两条路径均未成功", "DNS");
        return false;
    }

    public async Task<bool> ResetToAutomaticAsync(AdapterInfo adapter, CancellationToken cancellationToken = default)
    {
        var netsh = await ProcessRunner.RunAsync(
            "netsh.exe",
            new[] { "interface", "ipv4", "set", "dnsservers", adapter.Name, "source=dhcp" },
            10000,
            cancellationToken).ConfigureAwait(false);

        bool ok = netsh.Succeeded;

        if (!ok)
        {
            _log.Warn(
                $"netsh 改为自动获取失败（{adapter.Display}）：{FirstLine(netsh.StdErr)}，改用 PowerShell 重试",
                "DNS");
            ok = await ResetWithPowerShellAsync(adapter, cancellationToken).ConfigureAwait(false);
        }

        if (ok)
        {
            _log.Info($"已将 {adapter.Display} 的 DNS 改为自动获取", "DNS");
        }
        else
        {
            _log.Error($"改为自动获取失败（{adapter.Display}）", "DNS");
        }

        return ok;
    }

    /// <summary>主路径：netsh（使用接口名作为位置参数）。</summary>
    private async Task<bool> TryApplyWithNetshAsync(
        AdapterInfo adapter,
        DnsPair dns,
        CancellationToken cancellationToken)
    {
        var setPrimary = await ProcessRunner.RunAsync(
            "netsh.exe",
            new[]
            {
                "interface", "ipv4", "set", "dnsservers",
                adapter.Name, "source=static", $"address={dns.Primary}", "register=PRIMARY", "validate=no",
            },
            10000,
            cancellationToken).ConfigureAwait(false);

        if (!setPrimary.Succeeded)
        {
            _log.Warn(
                $"netsh 设置首选 DNS 失败（{adapter.Display}）：{FirstLine(setPrimary.StdErr)}，改用 PowerShell 重试",
                "DNS");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dns.Backup))
        {
            var addBackup = await ProcessRunner.RunAsync(
                "netsh.exe",
                new[]
                {
                    "interface", "ipv4", "add", "dnsservers",
                    adapter.Name, $"address={dns.Backup}", "index=2", "validate=no",
                },
                10000,
                cancellationToken).ConfigureAwait(false);

            if (!addBackup.Succeeded)
            {
                // 首选已生效，备选失败只记录不视为整体失败
                _log.Warn($"添加备选 DNS 失败（{adapter.Display}）：{FirstLine(addBackup.StdErr)}", "DNS");
            }
        }

        return true;
    }

    /// <summary>
    /// 备用路径：DnsClient 模块的 cmdlet，按接口<b>索引</b>寻址，
    /// 与 netsh 完全独立——两者同时失效的概率极低。
    /// </summary>
    private async Task<bool> TryApplyWithPowerShellAsync(
        AdapterInfo adapter,
        DnsPair dns,
        CancellationToken cancellationToken)
    {
        if (adapter.Index <= 0)
        {
            return false;
        }

        string servers = string.IsNullOrWhiteSpace(dns.Backup)
            ? $"'{dns.Primary}'"
            : $"'{dns.Primary}','{dns.Backup}'";

        string command =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "$ErrorActionPreference='Stop'; " +
            $"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ServerAddresses @({servers}); " +
            "'ok'";

        return await RunPowerShellDnsCommandAsync(command, "Set-DnsClientServerAddress", adapter, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ResetWithPowerShellAsync(AdapterInfo adapter, CancellationToken cancellationToken)
    {
        if (adapter.Index <= 0)
        {
            return false;
        }

        string command =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "$ErrorActionPreference='Stop'; " +
            $"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ResetServerAddresses; " +
            "'ok'";

        return await RunPowerShellDnsCommandAsync(command, "ResetServerAddresses", adapter, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> RunPowerShellDnsCommandAsync(
        string command,
        string operation,
        AdapterInfo adapter,
        CancellationToken cancellationToken)
    {
        string? output = await ProcessRunner
            .RunPowerShellAsync(command, 20000, cancellationToken)
            .ConfigureAwait(false);

        if (output is null)
        {
            _log.Warn($"PowerShell {operation} 执行失败（{adapter.Display}）", "DNS");
            return false;
        }

        return output.Contains("ok", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>比较两个 DNS 地址是否指向同一台服务器（容忍 IPv6 映射前缀等写法差异）。</summary>
    internal static bool AddressesMatch(string left, string right)
    {
        static string Normalize(string value)
        {
            string text = value.Trim();
            const string mappedPrefix = "::ffff:";
            if (text.StartsWith(mappedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[mappedPrefix.Length..];
            }

            return text;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析 netsh 的 DNS 配置输出：静态配置优先，其次为 DHCP 下发。
    /// <para>
    /// 必须同时处理两种小节，否则「手动改回自动获取」之后会把 DHCP 下发的地址
    /// 误读成「没有 DNS」。首地址与标题同行，其余为缩进续行。
    /// </para>
    /// </summary>
    internal static DnsPair? ParseDnsConfiguration(string output)
    {
        List<string> staticAddresses = ExtractSection(output, IsStaticDnsHeader);
        if (staticAddresses.Count > 0)
        {
            return new DnsPair(staticAddresses[0], staticAddresses.Count > 1 ? staticAddresses[1] : string.Empty, IsStatic: true);
        }

        List<string> dhcpAddresses = ExtractSection(output, IsDhcpDnsHeader);
        if (dhcpAddresses.Count > 0)
        {
            return new DnsPair(dhcpAddresses[0], dhcpAddresses.Count > 1 ? dhcpAddresses[1] : string.Empty, IsStatic: false);
        }

        return null;
    }

    /// <summary>仅保留静态配置的解析入口，供单元测试与兼容调用使用。</summary>
    internal static List<string> ParseStaticallyConfiguredDns(string output)
        => ExtractSection(output, IsStaticDnsHeader);

    private static List<string> ExtractSection(string output, Func<string, bool> isHeader)
    {
        var addresses = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return addresses;
        }

        string[] lines = output.Split('\n');

        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (isHeader(lines[i]))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
        {
            return addresses;
        }

        // 从标题行开始收集：首地址与标题同行，后续为缩进续行；遇到不含地址的行即结束
        for (int i = headerIndex; i < lines.Length; i++)
        {
            var matches = IPv4Regex().Matches(lines[i]);
            if (matches.Count == 0)
            {
                break;
            }

            foreach (Match match in matches)
            {
                addresses.Add(match.Value);
            }
        }

        return addresses.Distinct().ToList();
    }

    /// <summary>判断是否为「已静态配置的 DNS 服务器」小节标题。</summary>
    private static bool IsStaticDnsHeader(string line)
        => line.Contains("Statically Configured DNS Servers", StringComparison.OrdinalIgnoreCase)
            || line.Contains("静态配置的 DNS 服务器", StringComparison.Ordinal);

    /// <summary>判断是否为「通过 DHCP 配置的 DNS 服务器」小节标题。</summary>
    private static bool IsDhcpDnsHeader(string line)
        => (line.Contains("DNS", StringComparison.OrdinalIgnoreCase)
                && line.Contains("DHCP", StringComparison.OrdinalIgnoreCase))
            || line.Contains("通过 DHCP 配置的 DNS 服务器", StringComparison.Ordinal);

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(无输出)";
        }

        string trimmed = text.Trim();
        int newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].Trim();
    }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Regex();
}
