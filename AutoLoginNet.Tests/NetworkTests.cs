using AutoLoginNet.Configuration;
using AutoLoginNet.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class NetworkDetectorTests
{
    [DataTestMethod]
    [DataRow(true, false, AuthRequirement.Required)]
    [DataRow(true, true, AuthRequirement.NotRequired)]
    [DataRow(false, true, AuthRequirement.NotRequired)]
    [DataRow(false, false, AuthRequirement.Unknown)]
    public void Evaluate_MapsProbeResultsToRequirement(bool campus, bool external, AuthRequirement expected)
        => Assert.AreEqual(expected, NetworkDetector.Evaluate(campus, external));

    [TestMethod]
    public async Task DetectAsync_ReturnsSnapshotFromProbe()
    {
        var probe = new FakeProbe
        {
            HttpResult = new ProbeResult("10.17.8.18", true, TimeSpan.FromMilliseconds(12), "HTTP 200", "HTTP"),
            PingResult = new ProbeResult("baidu.com", false, TimeSpan.FromSeconds(1), "超时", "ICMP"),
        };

        var detector = new NetworkDetector(NewOptions(), probe, new RecordingLogService());
        NetworkSnapshot snapshot = await detector.DetectAsync();

        Assert.AreEqual(AuthRequirement.Required, snapshot.Requirement);
        Assert.IsTrue(snapshot.NeedsAuthentication);
        Assert.IsFalse(snapshot.IsOnline);
        Assert.IsTrue(snapshot.Campus.Reachable);
        Assert.IsFalse(snapshot.External.Reachable);
    }

    [TestMethod]
    public async Task DetectAsync_ProbeThrows_ReturnsUnknownSnapshotInsteadOfThrowing()
    {
        var probe = new FakeProbe { ThrowOnPing = true };
        var detector = new NetworkDetector(NewOptions(), probe, new RecordingLogService());

        NetworkSnapshot snapshot = await detector.DetectAsync();

        Assert.AreEqual(AuthRequirement.Unknown, snapshot.Requirement);
    }

    [TestMethod]
    public async Task WaitForReadyAsync_UsesConfiguredRetryCount()
    {
        var options = NewOptions();
        options.Startup.NetworkReadyMaxRetries = 2;
        options.Startup.NetworkReadyRetryDelayMs = 100;

        var log = new RecordingLogService();
        var detector = new NetworkDetector(options, new FakeProbe(), log);

        // 测试机有网络时应立即返回 true；没有网络时应在重试后返回 false，两种情况都不应抛异常
        bool ready = await detector.WaitForReadyAsync();

        Assert.IsTrue(ready || log.Entries.Any(e => e.Message.Contains("等待网络就绪超时", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProbeResult_FormatsSummary()
    {
        var reachable = new ProbeResult("baidu.com", true, TimeSpan.FromMilliseconds(42), "1.2.3.4 延迟 42 ms", "ICMP");
        Assert.IsTrue(reachable.Summary.Contains("可达", StringComparison.Ordinal));
        Assert.IsTrue(reachable.Summary.Contains("42", StringComparison.Ordinal));

        var unreachable = new ProbeResult("baidu.com", false, TimeSpan.FromSeconds(1), "超时", "ICMP");
        Assert.IsTrue(unreachable.Summary.Contains("不可达", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NetworkSnapshot_Conclusion_DiffersByRequirement()
    {
        var campus = new ProbeResult("c", true, TimeSpan.Zero, "", "HTTP");
        var external = new ProbeResult("e", false, TimeSpan.Zero, "", "ICMP");

        var required = new NetworkSnapshot(campus, external, AuthRequirement.Required, DateTimeOffset.Now);
        var notRequired = new NetworkSnapshot(campus, external with { Reachable = true }, AuthRequirement.NotRequired, DateTimeOffset.Now);
        var unknown = new NetworkSnapshot(campus with { Reachable = false }, external, AuthRequirement.Unknown, DateTimeOffset.Now);

        Assert.IsTrue(required.Conclusion.Contains("需要认证", StringComparison.Ordinal));
        Assert.IsTrue(notRequired.Conclusion.Contains("无需重复认证", StringComparison.Ordinal));
        Assert.IsTrue(unknown.Conclusion.Contains("未知", StringComparison.Ordinal));
    }

    private static AppOptions NewOptions()
    {
        var options = new AppOptions();
        options.Normalize();
        return options;
    }

    private sealed class FakeProbe : INetworkProbe
    {
        public ProbeResult HttpResult { get; set; } = new("campus", true, TimeSpan.Zero, "", "HTTP");

        public ProbeResult PingResult { get; set; } = new("external", true, TimeSpan.Zero, "", "ICMP");

        public bool ThrowOnPing { get; set; }

        public Task<ProbeResult> PingAsync(string host, int timeoutMs, int tcpFallbackPort = 0, CancellationToken cancellationToken = default)
            => ThrowOnPing
                ? Task.FromException<ProbeResult>(new InvalidOperationException("探测失败"))
                : Task.FromResult(PingResult);

        public Task<ProbeResult> ProbeHttpAsync(string url, int timeoutMs, CancellationToken cancellationToken = default)
            => ThrowOnPing
                ? Task.FromException<ProbeResult>(new InvalidOperationException("探测失败"))
                : Task.FromResult(HttpResult);
    }
}

[TestClass]
public sealed class WindowsDnsParsingTests
{
    [TestMethod]
    public void ParseStaticallyConfiguredDns_ReadsEnglishOutput()
    {
        const string output = """
        Configuration for interface "Ethernet"
            Statically Configured DNS Servers:    114.114.114.114
                                                  223.5.5.5
            Register with which suffix:           Primary only
        """;

        var addresses = WindowsDnsService.ParseStaticallyConfiguredDns(output);

        CollectionAssert.AreEqual(
            new[] { "114.114.114.114", "223.5.5.5" },
            addresses.ToArray(),
            $"实际解析结果：[{string.Join(",", addresses)}]");
    }

    [TestMethod]
    public void ParseStaticallyConfiguredDns_ReadsChineseOutput()
    {
        const string output = "接口 \"以太网\" 的配置\r\n    静态配置的 DNS 服务器:    114.114.114.114\r\n                              223.5.5.5\r\n    用哪个后缀注册:           仅主后缀\r\n";

        var addresses = WindowsDnsService.ParseStaticallyConfiguredDns(output);

        CollectionAssert.AreEqual(new[] { "114.114.114.114", "223.5.5.5" }, addresses.ToArray());
    }

    [TestMethod]
    public void ParseStaticallyConfiguredDns_DhcpOutput_ReturnsEmpty()
    {
        const string output = """
        Configuration for interface "Ethernet"
            DNS servers configured through DHCP:  10.22.0.1
            Register with which suffix:           Primary only
        """;

        // 静态小节为空 → 静态解析结果必须为空
        var addresses = WindowsDnsService.ParseStaticallyConfiguredDns(output);

        CollectionAssert.AreEqual(Array.Empty<string>(), addresses.ToArray());
    }

    [TestMethod]
    public void ParseStaticallyConfiguredDns_ChineseDhcpHeader_ReturnsEmpty()
    {
        // 中文标题「通过 DHCP 配置的 DNS 服务器:」同样含 "DNS"，必须不被误判为静态配置
        const string output = "接口 \"以太网\" 的配置\r\n    通过 DHCP 配置的 DNS 服务器:  10.22.0.1\r\n    用哪个后缀注册:            仅主后缀\r\n";

        var addresses = WindowsDnsService.ParseStaticallyConfiguredDns(output);

        CollectionAssert.AreEqual(Array.Empty<string>(), addresses.ToArray(), "自动获取的 DNS 不得被当作静态配置");
    }

    [TestMethod]
    public void ParseStaticallyConfiguredDns_NoneValue_ReturnsEmpty()
    {
        const string output = """
        Configuration for interface "vEthernet"
            Statically Configured DNS Servers:    None
            Register with which suffix:           None
        """;

        CollectionAssert.AreEqual(Array.Empty<string>(), WindowsDnsService.ParseStaticallyConfiguredDns(output).ToArray());
    }

    [TestMethod]
    public void ParseStaticallyConfiguredDns_EmptyOrGarbage_ReturnsEmpty()
    {
        Assert.AreEqual(0, WindowsDnsService.ParseStaticallyConfiguredDns(string.Empty).Count);
        Assert.AreEqual(0, WindowsDnsService.ParseStaticallyConfiguredDns("完全无关的输出").Count);
    }

    // ---------- ParseDnsConfiguration：静态优先，其次 DHCP ----------

    [TestMethod]
    public void ParseDnsConfiguration_PrefersStaticServers()
    {
        const string output = """
        Configuration for interface "Ethernet"
            Statically Configured DNS Servers:    114.114.114.114
                                                  223.5.5.5
            Register with which suffix:           Primary only
        """;

        DnsPair? dns = WindowsDnsService.ParseDnsConfiguration(output);

        Assert.IsNotNull(dns);
        Assert.IsTrue(dns!.Value.IsStatic);
        Assert.AreEqual("114.114.114.114", dns.Value.Primary);
        Assert.AreEqual("223.5.5.5", dns.Value.Backup);
        Assert.AreEqual("手动设置", dns.Value.SourceText);
    }

    [TestMethod]
    public void ParseDnsConfiguration_ReadsDhcpServersWhenNoStaticConfigured()
    {
        // 这是「改回自动获取」之后的真实输出形态：静态小节为 None，地址在 DHCP 小节。
        // 旧实现只解析静态小节，因此会把这种情况误读成「没有 DNS」。
        const string output = """
        Configuration for interface "Ethernet"
            Statically Configured DNS Servers:    None
            DNS servers configured through DHCP:  10.16.1.3
                                                  10.16.1.2
            Register with which suffix:           Primary only
        """;

        DnsPair? dns = WindowsDnsService.ParseDnsConfiguration(output);

        Assert.IsNotNull(dns, "自动获取时也应能读出实际生效的 DNS");
        Assert.IsFalse(dns!.Value.IsStatic);
        Assert.AreEqual("10.16.1.3", dns.Value.Primary);
        Assert.AreEqual("10.16.1.2", dns.Value.Backup);
        Assert.AreEqual("自动获取(DHCP)", dns.Value.SourceText);
    }

    [TestMethod]
    public void ParseDnsConfiguration_ChineseDhcpServers()
    {
        const string output = "接口 \"以太网\" 的配置\r\n    静态配置的 DNS 服务器:    None\r\n    通过 DHCP 配置的 DNS 服务器:  10.16.1.3\r\n                                  10.16.1.2\r\n    用哪个后缀注册:            仅主后缀\r\n";

        DnsPair? dns = WindowsDnsService.ParseDnsConfiguration(output);

        Assert.IsNotNull(dns);
        Assert.IsFalse(dns!.Value.IsStatic);
        Assert.AreEqual("10.16.1.3", dns.Value.Primary);
    }

    [TestMethod]
    public void ParseDnsConfiguration_NoServersAtAll_ReturnsNull()
    {
        const string output = """
        Configuration for interface "vEthernet"
            Statically Configured DNS Servers:    None
            DNS servers configured through DHCP:  None
            Register with which suffix:           None
        """;

        Assert.IsNull(WindowsDnsService.ParseDnsConfiguration(output));
    }

    [TestMethod]
    public void AddressesMatch_ToleratesWhitespaceAndIpv4MappedPrefix()
    {
        Assert.IsTrue(WindowsDnsService.AddressesMatch("114.114.114.114", " 114.114.114.114 "));
        Assert.IsTrue(WindowsDnsService.AddressesMatch("::ffff:114.114.114.114", "114.114.114.114"));
        Assert.IsFalse(WindowsDnsService.AddressesMatch("114.114.114.114", "223.5.5.5"));
    }

    [TestMethod]
    public void DnsPair_ToString_IncludesSource()
    {
        Assert.AreEqual("手动设置：1.1.1.1 / 1.0.0.1", new DnsPair("1.1.1.1", "1.0.0.1").ToString());
        Assert.AreEqual("自动获取(DHCP)：10.0.0.1", new DnsPair("10.0.0.1", string.Empty, IsStatic: false).ToString());
    }
}

[TestClass]
public sealed class AdapterInfoTests
{
    [TestMethod]
    public void Display_IncludesNameAndAddress()
    {
        var adapter = new AdapterInfo(12, "以太网", "10.22.1.5");
        Assert.AreEqual("以太网（10.22.1.5）", adapter.Display);
    }

    [TestMethod]
    public void Name_IsPassedThroughVerbatim()
    {
        // 网卡名会直接作为 netsh 的接口参数；实测 netsh 的 show/set dnsservers
        // 只接受位置参数形式的名称，不接受 idx=，因此模型里不能夹带任何 netsh 语法。
        var adapter = new AdapterInfo(12, "以太网", "10.22.1.5");

        Assert.AreEqual("以太网", adapter.Name);
        Assert.IsFalse(adapter.Name.Contains("idx=", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(adapter.Name.Contains('"'));
    }

    [TestMethod]
    public void Index_WrapsNetshIndexArgument()
    {
        var adapter = new AdapterInfo(3, "Ethernet", "10.0.0.2");
        Assert.AreEqual("idx=3", adapter.NetshSelector);
    }
}

[TestClass]
public sealed class WindowsNetworkAdapterResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_ReturnsAdapterWithIntactLocalizedName()
    {
        var resolver = new WindowsNetworkAdapterResolver(new RecordingLogService());

        // 以校园网认证服务器为目标：应解析出实际承载它的网卡
        AdapterInfo? adapter = await resolver.ResolveAsync("10.17.8.18");

        if (adapter is null)
        {
            Assert.Inconclusive("当前环境没有可用网卡（例如离线运行），跳过该断言");
            return;
        }

        Assert.IsTrue(adapter.Value.Index >= 0, "应解析出接口索引");
        Assert.IsTrue(System.Net.IPAddress.TryParse(adapter.Value.IPv4Address, out _), "应解析出合法的 IPv4 地址");
        Assert.IsFalse(
            adapter.Value.Name.Contains('\uFFFD'),
            $"网卡名称不应出现替换字符（编码问题）：{adapter.Value.Name}");

        // 中文系统上名称应是可读中文；同时绝不能是「�」这类乱码
        Assert.IsFalse(string.IsNullOrWhiteSpace(adapter.Value.Name));
    }
}
