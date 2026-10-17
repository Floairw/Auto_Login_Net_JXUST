using AutoLoginNet.Diagnostics;
using AutoLoginNet.Network;
using AutoLoginNet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;

namespace AutoLoginNet.Tests;

/// <summary>
/// <see cref="WindowsAdapterPriorityService"/> 的单元测试。
/// <para>
/// 全部通过注入的假命令执行器驱动：用例里<b>绝不</b>真的执行 <c>netsh set</c>（那会改动跑测试的机器），
/// 只验证「netsh 输出解析对不对、实际发了什么命令、失败/未生效时怎么回滚、快照何时保存与删除」。
/// 真实只读的 <c>netsh show</c> 解析约定见 <see cref="ParseInterfaceTable_*"/> 与
/// <see cref="ParseRouteTable_*"/> 系列用例。
/// </para>
/// <para>
/// 为什么要测默认路由跃点：真机上只改接口跃点会出现「界面显示设置成功、系统仍走原网卡」的假成功
/// （WLAN 的默认路由跃点是 0、以太网 2 的是 25）。因此假 netsh 同时维护接口表与路由表，
/// 两者互不联动，才能模拟出「接口改了但路由没改」的真实状态。
/// </para>
/// </summary>
[TestClass]
public sealed class AdapterPriorityServiceTests
{
    private const string ShowIpv4 = "interface ipv4 show interfaces";
    private const string ShowIpv6 = "interface ipv6 show interfaces";
    private const string ShowRoute = "interface ipv4 show route";
    private const string FlushDestinationCache = "interface ipv4 delete destinationcache";
    private const string FlushDestinationCacheLog = "已清空系统目标缓存";

    // ===================== 解析器 =====================

    [TestMethod]
    public void ParseInterfaceTable_ParsesRealChineseNetshOutput()
    {
        const string output = """
            Idx Met MTU 状态 名称
            --- ---------- ---------- ------------ ---------------------------
              9          35        1500  connected     WLAN
             60          25        1500  connected     以太网 2
             13        5000        1500  connected     vEthernet (Default Switch)
            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshInterfaceRow> rows =
            WindowsAdapterPriorityService.ParseInterfaceTable(output);

        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("WLAN", rows[0].Name);
        Assert.AreEqual(35, rows[0].Metric);
        Assert.IsTrue(rows[0].Connected);
        Assert.AreEqual(9, rows[0].Index, "Idx 列必须解析出来，默认路由只带接口索引");
        Assert.AreEqual("以太网 2", rows[1].Name);
        Assert.AreEqual(25, rows[1].Metric);
        Assert.AreEqual(60, rows[1].Index);
        Assert.AreEqual("vEthernet (Default Switch)", rows[2].Name, "名称里的空格和括号必须原样保留");
        Assert.AreEqual(5000, rows[2].Metric);
        Assert.IsTrue(rows[2].Connected);
    }

    [TestMethod]
    public void ParseInterfaceTable_ParsesEnglishHeaderAndDisconnectedState()
    {
        const string output = """
            Idx Met MTU State         Name
            --- ---------- ---------- ------------- ---------------------------
              1          25        1500  connected     Ethernet
              2          35        1500  disconnected  Wi-Fi
            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshInterfaceRow> rows =
            WindowsAdapterPriorityService.ParseInterfaceTable(output);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("Ethernet", rows[0].Name);
        Assert.IsTrue(rows[0].Connected);
        Assert.AreEqual("Wi-Fi", rows[1].Name);
        Assert.IsFalse(rows[1].Connected, "disconnected 必须解析为未连接，否则会给掉线网卡写跃点");
    }

    [TestMethod]
    public void ParseInterfaceTable_SkipsHeaderSeparatorBlankAndGarbageLines()
    {
        const string output = """

            Idx Met MTU 状态 名称
            --- ---------- ---------- ------------ ---------------------------
              9          35        1500  connected     WLAN
            netsh 出错：请求的操作需要提升。
             60          25        1500  connected     以太网 2

            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshInterfaceRow> rows =
            WindowsAdapterPriorityService.ParseInterfaceTable(output);

        Assert.AreEqual(2, rows.Count, "表头、虚线、空行与错误文本都不该被当成网卡");
        Assert.AreEqual("WLAN", rows[0].Name);
        Assert.AreEqual("以太网 2", rows[1].Name);
    }

    [TestMethod]
    public void ParseInterfaceTable_EmptyOrWhitespaceReturnsEmpty()
    {
        Assert.AreEqual(0, WindowsAdapterPriorityService.ParseInterfaceTable(string.Empty).Count);
        Assert.AreEqual(0, WindowsAdapterPriorityService.ParseInterfaceTable("   \r\n\r\n").Count);
        Assert.AreEqual(0, WindowsAdapterPriorityService.ParseInterfaceTable("没有任何表格").Count);
    }

    [TestMethod]
    public void ParseInterfaceTable_TreatsChineseConnectedStateAsConnected()
    {
        const string output = """
            Idx Met MTU 状态 名称
            --- ---------- ---------- ------------ ---------------------------
              9          35        1500  已连接       WLAN
             60          25        1500  已断开       以太网 2
            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshInterfaceRow> rows =
            WindowsAdapterPriorityService.ParseInterfaceTable(output);

        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows[0].Connected);
        Assert.IsFalse(rows[1].Connected);
    }

    [TestMethod]
    public void ParseRouteTable_ParsesRealNetshOutputAndKeepsOnlyDefaultRoutes()
    {
        // 本机真实输出（截取）：默认路由 3 条，另外还有 System 路由、回环前缀、On-link 网关。
        // 只有「0.0.0.0/0 且网关是 IPv4 地址」的行才可能用 netsh set route 定位并修改。
        const string output = """
            Publish  Type      Met  Prefix                    Idx  Gateway/Interface Name
            -------  --------  ----  ------------------------  ---  ---------------------------
            No       Manual    0     0.0.0.0/0                   9  10.48.164.237
            No       Manual    0     0.0.0.0/0                   4  10.22.128.1
            No       Manual    25    0.0.0.0/0                  60  10.31.126.19
            No       System    256   10.31.126.0/24             60  以太网 2
            No       Manual    0     127.0.0.0/8                 1  On-link
            No       Manual    256   224.0.0.0/4                 1  On-link
            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshRouteRow> rows =
            WindowsAdapterPriorityService.ParseRouteTable(output);

        Assert.AreEqual(3, rows.Count, "非默认前缀与 On-link 网关都不该被当成可修改的默认路由");
        Assert.AreEqual(9, rows[0].Index);
        Assert.AreEqual("10.48.164.237", rows[0].Gateway);
        Assert.AreEqual(0, rows[0].Metric);
        Assert.AreEqual(4, rows[1].Index);
        Assert.AreEqual("10.22.128.1", rows[1].Gateway);
        Assert.AreEqual(60, rows[2].Index);
        Assert.AreEqual("10.31.126.19", rows[2].Gateway);
        Assert.AreEqual(25, rows[2].Metric, "以太网 2 的路由跃点是 25，WLAN 是 0：这正是系统仍走 WLAN 的原因");
    }


    [TestMethod]
    public void ParseRouteTable_ParsesChineseLocalizedOutput()
    {
        // 真机（中文系统）实际输出：Publish/Type 两列是中文「否 手动」，旧实现按英文 No/Yes/Age 匹配，
        // 一条都解析不出来，界面报「读取不到系统默认路由」。列语义与语言无关，parser 不能再依赖这两个词。
        const string output = """
            发布 类型 跃点数 前缀 索引 网关/接口名称
            --- ---- ------ -------- --- ----------------
            否   手动 0    0.0.0.0/0    4   10.22.128.1
            否   手动 0    0.0.0.0/0    9   10.48.164.237
            否   手动 25   0.0.0.0/0   60   10.31.126.19
            否   系统 256   10.31.126.0/24  60   以太网 2
            否   手动 0     127.0.0.0/8    1   On-link
            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshRouteRow> rows =
            WindowsAdapterPriorityService.ParseRouteTable(output);

        Assert.AreEqual(3, rows.Count, "中文系统下也必须能解析出 3 条默认路由");
        Assert.AreEqual(4, rows[0].Index);
        Assert.AreEqual("10.22.128.1", rows[0].Gateway);
        Assert.AreEqual(0, rows[0].Metric);
        Assert.AreEqual(9, rows[1].Index);
        Assert.AreEqual(0, rows[1].Metric);
        Assert.AreEqual(60, rows[2].Index);
        Assert.AreEqual("10.31.126.19", rows[2].Gateway);
        Assert.AreEqual(25, rows[2].Metric);
    }

    [TestMethod]
    public void ParseRouteTable_SkipsDefaultRouteWithOnLinkGateway()
    {
        const string output = """
            否 手动 0 0.0.0.0/0 4 On-link
            否 手动 0 0.0.0.0/0 9 10.48.164.237
            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshRouteRow> rows =
            WindowsAdapterPriorityService.ParseRouteTable(output);

        Assert.AreEqual(1, rows.Count, "网关是 On-link 的默认路由没法用 set route 的接口+下一跳定位");
        Assert.AreEqual(9, rows[0].Index);
    }

    [TestMethod]
    public void ParseRouteTable_SkipsHeaderSeparatorAndGarbageLines()
    {
        const string output = """

            Publish  Type      Met  Prefix                    Idx  Gateway/Interface Name
            -------  --------  ----  ------------------------  ---  ---------------------------
            netsh 出错：请求的操作需要提升。
            No       Manual    0     0.0.0.0/0                   9  10.48.164.237

            """;

        IReadOnlyList<WindowsAdapterPriorityService.NetshRouteRow> rows =
            WindowsAdapterPriorityService.ParseRouteTable(output);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("10.48.164.237", rows[0].Gateway);
    }

    [TestMethod]
    public void ParseRouteTable_EmptyOrWhitespaceReturnsEmpty()
    {
        Assert.AreEqual(0, WindowsAdapterPriorityService.ParseRouteTable(string.Empty).Count);
        Assert.AreEqual(0, WindowsAdapterPriorityService.ParseRouteTable("   \r\n\r\n").Count);
        Assert.AreEqual(0, WindowsAdapterPriorityService.ParseRouteTable("没有路由表").Count);
    }

    // ===================== ReadStateAsync =====================

    [TestMethod]
    public async Task ReadStateAsync_MergesIpv6MetricsAndUsesMinusOneWhenMissing()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("WLAN", 35, ipv6: 45)
            .With("以太网 2", 25, ipv6: 30)
            .With("vEthernet (Default Switch)", 5000); // 故意没有 IPv6 记录

        IReadOnlyList<AdapterMetricState> states = await fixture.Service.ReadStateAsync();

        Assert.AreEqual(3, states.Count);
        Assert.AreEqual(45, states.Single(s => s.Name == "WLAN").Ipv6Metric);
        Assert.AreEqual(30, states.Single(s => s.Name == "以太网 2").Ipv6Metric);
        Assert.AreEqual(
            WindowsAdapterPriorityService.MissingIpv6Metric,
            states.Single(s => s.Name == "vEthernet (Default Switch)").Ipv6Metric,
            "IPv6 表里没有的网卡必须用 -1 占位（0 是合法跃点值，会误报）");
        CollectionAssert.AreEqual(new[] { ShowIpv4, ShowIpv6 }, fixture.Netsh.Commands);
    }

    [TestMethod]
    public async Task ReadStateAsync_ReturnsEmptyAndLogsErrorWhenIpv4ShowFails()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("WLAN", 35);
        fixture.Netsh.Ipv4ShowFails = true;

        IReadOnlyList<AdapterMetricState> states = await fixture.Service.ReadStateAsync();

        Assert.AreEqual(0, states.Count, "IPv4 读不到就没有可信数据，必须返回空列表而不是猜");
        Assert.AreEqual(1, fixture.Netsh.Commands.Count, "IPv4 失败后不应再读 IPv6");
        Assert.IsTrue(fixture.Log.Contains("读取网卡 IPv4 跃点失败", LogLevel.Error));
    }

    [TestMethod]
    public async Task ReadStateAsync_KeepsIpv4StateWhenIpv6ShowFails()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("WLAN", 35, ipv6: 45);
        fixture.Netsh.Ipv6ShowFails = true;

        IReadOnlyList<AdapterMetricState> states = await fixture.Service.ReadStateAsync();

        Assert.AreEqual(1, states.Count, "IPv6 读取失败不应连 IPv4 数据一起丢掉");
        Assert.AreEqual(35, states[0].Ipv4Metric);
        Assert.AreEqual(
            WindowsAdapterPriorityService.MissingIpv6Metric,
            states[0].Ipv6Metric,
            "IPv6 读取失败时按缺失约定记 -1");
        Assert.IsTrue(fixture.Log.Contains("IPv6", LogLevel.Warning));
    }

    // ===================== ReadRoutesAsync =====================

    [TestMethod]
    public async Task ReadRoutesAsync_MapsIndexToAdapterNameAndSkipsLoopback()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("Loopback Pseudo-Interface 1", 75)
            .With("以太网 2", 25)
            .With("WLAN", 35);

        IReadOnlyList<AdapterRouteState> routes = await fixture.Service.ReadRoutesAsync();

        Assert.AreEqual(2, routes.Count, "回环接口的默认路由不参与上网选路，必须跳过");
        Assert.AreEqual("以太网 2", routes.Single(r => r.AdapterName == "以太网 2").AdapterName);
        Assert.AreEqual(25, routes.Single(r => r.AdapterName == "以太网 2").Metric);
        CollectionAssert.AreEqual(new[] { ShowRoute, ShowIpv4 }, fixture.Netsh.Commands);
    }

    // ===================== ApplyPreferredAsync =====================

    [TestMethod]
    public async Task ApplyPreferred_SetsMetricsSavesSnapshotAndReportsSuccess()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25, ipv6: 30)
            .With("WLAN", 35, ipv6: 45)
            .With("vEthernet (Default Switch)", 5000);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual(3, outcome.Changes.Length);
        StringAssert.Contains(outcome.Message, "以太网 2", "成功消息要带上首选网卡名");
        StringAssert.Contains(outcome.Message, "10");
        StringAssert.Contains(outcome.Message, "9000");

        CollectionAssert.Contains(fixture.Netsh.Commands, "interface ipv4 set interface 以太网 2 metric=10");
        CollectionAssert.Contains(fixture.Netsh.Commands, "interface ipv4 set interface WLAN metric=9000");
        CollectionAssert.Contains(
            fixture.Netsh.Commands,
            "interface ipv4 set interface vEthernet (Default Switch) metric=9000");

        // 带空格的接口名必须作为「一个 argv」传给 netsh，拼成多个参数会被 netsh 当成别的语法
        string[] setCall = fixture.Netsh.Invocations.Single(
            args => args.Length == 6 && args[4] == "vEthernet (Default Switch)");
        Assert.AreEqual("metric=9000", setCall[5]);

        Assert.AreEqual(10, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(9000, fixture.Netsh.MetricOf("WLAN"));
        Assert.AreEqual(9000, fixture.Netsh.MetricOf("vEthernet (Default Switch)"));

        AdapterPrioritySnapshot? snapshot = fixture.Store.Load();
        Assert.IsNotNull(snapshot, "成功应用后必须留下恢复记录，否则无法一键恢复");
        Assert.AreEqual("以太网 2", snapshot!.PreferredName);
        Assert.AreEqual(3, snapshot.Adapters.Length, "快照要记录全部网卡（含断开状态）");
        Assert.AreEqual(25, snapshot.Adapters.Single(a => a.Name == "以太网 2").Ipv4Metric);
        Assert.AreEqual(30, snapshot.Adapters.Single(a => a.Name == "以太网 2").Ipv6Metric);
        Assert.IsTrue(fixture.Service.HasSnapshot);
        Assert.IsTrue((DateTimeOffset.Now - snapshot.AppliedAt).Duration() < TimeSpan.FromMinutes(1));
        Assert.IsTrue(fixture.Log.Entries.Any(e => e.Level == LogLevel.Info && e.Category == "网络"));
    }

    [TestMethod]
    public async Task ApplyPreferred_SetsDefaultRouteMetricsAndSnapshotRecordsRouteOriginals()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35)
            // 断开的网卡也可能残留一条默认路由（例如刚拔线/禁用）：里面照样能读到，但不写它
            .With("vEthernet (Default Switch)", 5000, connected: false);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual(0, fixture.Netsh.RouteMetricOf("以太网 2"), "首选网卡的默认路由跃点必须改成 0");
        Assert.AreEqual(9000, fixture.Netsh.RouteMetricOf("WLAN"));
        Assert.AreEqual(
            5000,
            fixture.Netsh.RouteMetricOf("vEthernet (Default Switch)"),
            "断开网卡上的默认路由不写（netsh set route 在断开接口上不可靠，会整单回滚）");
        Assert.AreEqual(10, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(9000, fixture.Netsh.MetricOf("WLAN"));
        Assert.AreEqual(5000, fixture.Netsh.MetricOf("vEthernet (Default Switch)"), "断开的网卡不该改接口跃点");

        CollectionAssert.Contains(
            fixture.Netsh.Commands,
            "interface ipv4 set route 0.0.0.0/0 以太网 2 10.0.1.1 metric=0 store=active");
        StringAssert.Contains(outcome.Message, "默认路由");
        StringAssert.Contains(outcome.Message, "2 条默认路由");
        Assert.IsFalse(
            fixture.Netsh.Invocations.Any(args => args.Length == 9 && args[5] == "vEthernet (Default Switch)"),
            "断开网卡的默认路由不该生成 set route 命令");
        Assert.IsTrue(fixture.Log.Contains("跳过 1 条断开网卡的默认路由", LogLevel.Info));

        // 接口名与下一跳必须是各自独立的 argv，store=active 保证只改当前会话、不写持久化配置
        string[] routeCall = fixture.Netsh.Invocations.Single(
            args => args.Length == 9 && args[4] == "0.0.0.0/0" && args[5] == "WLAN");
        Assert.AreEqual("10.0.2.1", routeCall[6]);
        Assert.AreEqual("metric=9000", routeCall[7]);
        Assert.AreEqual("store=active", routeCall[8]);

        AdapterPrioritySnapshot snapshot = fixture.Store.Load()!;
        Assert.AreEqual(3, snapshot.RoutesOrEmpty.Length, "恢复记录必须包含全部默认路由的原始跃点");
        Assert.AreEqual(25, snapshot.RoutesOrEmpty.Single(r => r.AdapterName == "以太网 2").Metric);
        Assert.AreEqual(35, snapshot.RoutesOrEmpty.Single(r => r.AdapterName == "WLAN").Metric);
        Assert.AreEqual(5000, snapshot.RoutesOrEmpty.Single(r => r.AdapterName == "vEthernet (Default Switch)").Metric);
    }

    // ===================== 目标缓存（点完立刻生效）=====================

    [TestMethod]
    public async Task ApplyPreferred_ClearsDestinationCacheSoNewConnectionsUseItImmediately()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("以太网 2", 25).With("WLAN", 35);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        CollectionAssert.Contains(
            fixture.Netsh.Commands,
            FlushDestinationCache,
            "改完跃点要清目标缓存，否则旧缓存里的下一跳会让新连接继续走原网卡");
        Assert.IsTrue(
            fixture.Log.Contains(FlushDestinationCacheLog, LogLevel.Info),
            "清缓存成功要留一条日志，用户才知道这次为什么立刻生效");
    }

    [TestMethod]
    public async Task Restore_ClearsDestinationCacheSoNewConnectionsUseItImmediately()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25), ("WLAN", 35)));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        CollectionAssert.Contains(
            fixture.Netsh.Commands,
            FlushDestinationCache,
            "恢复原跃点后同样要清缓存，让新连接回到系统自动选路");
    }

    [TestMethod]
    public async Task ApplyPreferred_StillSucceedsWhenDestinationCacheCannotBeCleared()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("以太网 2", 25).With("WLAN", 35);
        fixture.Netsh.DestinationCacheFails = true;

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, "清缓存只是锦上添花，失败不能把已经改好的跃点算成失败");
        Assert.AreEqual(10, fixture.Netsh.MetricOf("以太网 2"));
        Assert.IsTrue(
            fixture.Log.Contains("清空系统目标缓存失败", LogLevel.Warning),
            "失败要留警告，方便排查「为什么这次还要等一会儿才生效」");
    }

    [TestMethod]
    public async Task ApplyPreferred_SkipsDefaultRoutesOfDisconnectedAdapters()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35)
            // 真机上就有一张断开的「以太网」，它的持久默认路由照样出现在路由表里
            .With("以太网", 25, connected: false);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.IsFalse(
            fixture.Netsh.Invocations.Any(args => args.Length == 9 && args[5] == "以太网"),
            "断开网卡上的默认路由不该生成 set route 命令，否则报错会触发整单回滚");
        Assert.AreEqual(25, fixture.Netsh.RouteMetricOf("以太网"), "断开网卡的默认路由保持原跃点");
        Assert.AreEqual(0, fixture.Netsh.RouteMetricOf("以太网 2"));
        Assert.AreEqual(9000, fixture.Netsh.RouteMetricOf("WLAN"));
        StringAssert.Contains(outcome.Message, "2 条默认路由");
        Assert.IsTrue(fixture.Log.Contains("跳过 1 条断开网卡的默认路由", LogLevel.Info));
        Assert.AreEqual(3, fixture.Store.Load()!.RoutesOrEmpty.Length, "快照仍记录全部路由，不丢信息");
    }

    [TestMethod]
    public async Task ApplyPreferred_WhenMetricsAlreadyTargetsDoesNotTouchSystem()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric, routeMetric: AdapterPriorityPlanner.PreferredRouteMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric, routeMetric: AdapterPriorityPlanner.SecondaryRouteMetric);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual(0, outcome.Changes.Length);
        StringAssert.Contains(outcome.Message, "无需修改");
        Assert.IsFalse(fixture.Store.Exists, "没有改动就不该产生恢复记录");
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApplyPreferred_CommandFailureRollsBackChangedAdapters()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35)
            .With("vEthernet (Default Switch)", 5000);
        fixture.Netsh.SetFails = (name, _) => name == "WLAN";

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "回滚");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"), "已改成功的网卡必须回滚到原值");
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"));
        Assert.AreEqual(5000, fixture.Netsh.MetricOf("vEthernet (Default Switch)"), "还没改的网卡不该被动过");
        CollectionAssert.Contains(fixture.Netsh.Commands, "interface ipv4 set interface 以太网 2 metric=25");
        Assert.IsFalse(fixture.Store.Exists, "回滚完整时恢复记录也要删掉");
        Assert.IsTrue(fixture.Log.Contains("设置系统首选网卡失败", LogLevel.Error));
    }

    [TestMethod]
    public async Task ApplyPreferred_VerificationMismatchRollsBackAllChanges()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35);
        // 模拟「netsh 退出码 0 但设置没生效」的假成功：首选网卡没有被真的改掉
        fixture.Netsh.SetIgnored = (name, _) => name == "以太网 2";

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "未生效");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"), "回读失败时其余已改网卡也要回滚");
        Assert.IsFalse(fixture.Store.Exists);
        Assert.IsTrue(fixture.Log.Contains("设置系统首选网卡失败", LogLevel.Error));
    }

    [TestMethod]
    public async Task ApplyPreferred_RouteCommandFailureRollsBackInterfacesAndRoutes()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35)
            .With("vEthernet (Default Switch)", 5000);
        fixture.Netsh.SetRouteFails = (name, _) => name == "WLAN";

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "回滚");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"));
        Assert.AreEqual(25, fixture.Netsh.RouteMetricOf("以太网 2"), "已改的路由必须先于接口回滚");
        Assert.AreEqual(35, fixture.Netsh.RouteMetricOf("WLAN"));
        Assert.AreEqual(5000, fixture.Netsh.RouteMetricOf("vEthernet (Default Switch)"));
        Assert.IsFalse(fixture.Store.Exists, "回滚完整才能删恢复记录");
        Assert.IsTrue(fixture.Log.Contains("设置系统首选网卡失败", LogLevel.Error));
    }

    [TestMethod]
    public async Task ApplyPreferred_RouteVerificationMismatchRollsBackAll()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35);
        // netsh 返回成功但默认路由跃点没变：只靠退出码会误报「设置成功」，必须靠回读发现
        fixture.Netsh.SetRouteIgnored = (name, _) => name == "以太网 2";

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "未生效");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"));
        Assert.AreEqual(25, fixture.Netsh.RouteMetricOf("以太网 2"));
        Assert.IsFalse(fixture.Store.Exists);
    }

    [TestMethod]
    public async Task ApplyPreferred_ReadFailureReportsFailureWithoutWriting()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.Ipv4ShowFails = true;

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("WLAN");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "读取系统网卡跃点失败");
        Assert.IsFalse(fixture.Store.Exists);
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set interface ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApplyPreferred_RouteShowFailureReportsFailureWithoutWriting()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("以太网 2", 25).With("WLAN", 35);
        fixture.Netsh.RouteShowFails = true;

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "读取不到系统默认路由");
        Assert.IsFalse(fixture.Store.Exists, "读不到路由就不该改接口跃点，否则系统会处于半改状态");
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApplyPreferred_WhenPreferredHasNoDefaultRouteFailsBeforeWriting()
    {
        using var fixture = new ServiceFixture();
        // 首选网卡有接口跃点却没有默认路由：系统不可能从它上网，必须在动系统前拒绝
        fixture.Netsh.With("以太网 2", 25, route: false).With("WLAN", 35);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "没有默认路由");
        Assert.IsFalse(fixture.Store.Exists);
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApplyPreferred_MissingAdapterReportsPlannerError()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("WLAN", 35);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 9");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "找不到");
        Assert.IsFalse(fixture.Store.Exists);
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set interface ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApplyPreferred_WhenSnapshotCannotBeSavedDoesNotModifySystem()
    {
        using var dir = new TempDirectory();
        // 把「文件路径」指向一个已存在的目录：写快照必然失败，用来验证「先存快照再改系统」的顺序
        var store = new AdapterPrioritySnapshotStore(dir.Path);
        var netsh = new FakeNetsh().With("WLAN", 35);
        var log = new RecordingLogService();
        var service = new WindowsAdapterPriorityService(log, store, netsh.RunAsync);

        AdapterPriorityOutcome outcome = await service.ApplyPreferredAsync("WLAN");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "写入恢复记录失败");
        Assert.AreEqual(35, netsh.MetricOf("WLAN"), "快照写不进去就不能改系统，否则失败后无从恢复");
        Assert.IsFalse(netsh.Commands.Any(c => c.Contains(" set ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ApplyPreferred_RollbackFailureKeepsSnapshotForRetry()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", 25)
            .With("WLAN", 35);
        fixture.Netsh.SetFails = (name, metric) => name == "WLAN" || (name == "以太网 2" && metric == 25);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "保留恢复记录");
        Assert.IsTrue(fixture.Store.Exists, "回滚失败时不能删恢复记录，否则用户再也回不到原值");
    }

    [TestMethod]
    public async Task ApplyPreferred_RepeatedApplyKeepsOriginalSnapshotValues()
    {
        using var fixture = new ServiceFixture();
        // 真机场景：用户已经应用过一次（接口 10/9000、路由 0/9000）但还没恢复，
        // 此时再点一次「设为系统首选网卡」，绝不能把当前值 10/9000 当成原始值记下来。
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);

        // 让路由仍停在「需要改」的状态（25/0），确保本次 apply 走真正的写入路径而不是 no-op
        fixture.Netsh.SetRouteMetric("以太网 2", 25);
        fixture.Netsh.SetRouteMetric("WLAN", 0);

        var previous = SnapshotWithRoutes(
            "以太网 2",
            new[] { ("以太网 2", 25), ("WLAN", 35) },
            new[]
            {
                ("以太网 2", fixture.Netsh.GatewayOf("以太网 2"), 25),
                ("WLAN", fixture.Netsh.GatewayOf("WLAN"), 0),
            });
        fixture.Store.Save(previous);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        AdapterPrioritySnapshot saved = fixture.Store.Load()!;
        Assert.AreEqual(25, saved.Adapters.Single(a => a.Name == "以太网 2").Ipv4Metric, "重复 apply 必须保留最早的原值");
        Assert.AreEqual(35, saved.Adapters.Single(a => a.Name == "WLAN").Ipv4Metric);
        Assert.AreEqual(25, saved.RoutesOrEmpty.Single(r => r.AdapterName == "以太网 2").Metric);
        Assert.AreEqual(0, saved.RoutesOrEmpty.Single(r => r.AdapterName == "WLAN").Metric);
    }

    // ===================== RestoreAsync =====================

    [TestMethod]
    public async Task Restore_RestoresOriginalMetricsAndDeletesSnapshot()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25), ("WLAN", 35)));
        Assert.IsTrue(fixture.Service.HasSnapshot);

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        CollectionAssert.Contains(fixture.Netsh.Commands, "interface ipv4 set interface 以太网 2 metric=25");
        CollectionAssert.Contains(fixture.Netsh.Commands, "interface ipv4 set interface WLAN metric=35");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"));
        Assert.IsFalse(fixture.Store.Exists, "恢复成功后必须删除记录，避免重复恢复");
        Assert.IsFalse(fixture.Service.HasSnapshot);
        Assert.IsTrue(fixture.Log.Entries.Any(e => e.Level == LogLevel.Info && e.Category == "网络"));
    }

    [TestMethod]
    public async Task Restore_RestoresDefaultRouteMetricsFromSnapshot()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);
        fixture.Netsh.SetRouteMetric("以太网 2", AdapterPriorityPlanner.PreferredRouteMetric);
        fixture.Netsh.SetRouteMetric("WLAN", AdapterPriorityPlanner.SecondaryRouteMetric);

        fixture.Store.Save(SnapshotWithRoutes(
            "以太网 2",
            new[] { ("以太网 2", 25), ("WLAN", 35) },
            new[]
            {
                ("以太网 2", fixture.Netsh.GatewayOf("以太网 2"), 25),
                ("WLAN", fixture.Netsh.GatewayOf("WLAN"), 0),
            }));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual(25, fixture.Netsh.RouteMetricOf("以太网 2"));
        Assert.AreEqual(0, fixture.Netsh.RouteMetricOf("WLAN"), "旧值 0 也必须原样写回");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"));
        StringAssert.Contains(outcome.Message, "默认路由");
        StringAssert.Contains(outcome.Message, "2 张");
        Assert.IsFalse(fixture.Store.Exists);
    }

    [TestMethod]
    public async Task Restore_SkipsDefaultRoutesOfDisconnectedAdapters()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric)
            .With("以太网", AdapterPriorityPlanner.SecondaryMetric, connected: false);
        fixture.Netsh.SetRouteMetric("以太网 2", AdapterPriorityPlanner.PreferredRouteMetric);
        fixture.Netsh.SetRouteMetric("WLAN", AdapterPriorityPlanner.SecondaryRouteMetric);
        fixture.Netsh.SetRouteMetric("以太网", AdapterPriorityPlanner.SecondaryRouteMetric);

        fixture.Store.Save(SnapshotWithRoutes(
            "以太网 2",
            new[] { ("以太网 2", 25), ("WLAN", 35), ("以太网", 25) },
            new[]
            {
                ("以太网 2", fixture.Netsh.GatewayOf("以太网 2"), 25),
                ("WLAN", fixture.Netsh.GatewayOf("WLAN"), 0),
                ("以太网", fixture.Netsh.GatewayOf("以太网"), 25),
            }));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.IsFalse(
            fixture.Netsh.Invocations.Any(args => args.Length == 9 && args[5] == "以太网"),
            "断开网卡的路由不写回（路由是 store=active，重启后由系统重建，不会留脏数据）");
        Assert.AreEqual(9000, fixture.Netsh.RouteMetricOf("以太网"), "断开网卡的路由保持当前值");
        Assert.AreEqual(25, fixture.Netsh.RouteMetricOf("以太网 2"));
        Assert.AreEqual(0, fixture.Netsh.RouteMetricOf("WLAN"), "已连接网卡的旧值必须原样写回");
        Assert.IsFalse(fixture.Store.Exists);
        Assert.IsTrue(fixture.Log.Contains("跳过 1 条断开网卡的默认路由", LogLevel.Info));
    }

    [TestMethod]
    public async Task Restore_OldSnapshotWithoutRoutesOnlyRestoresInterfaces()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);
        // 旧版本快照没有路由字段：只恢复接口跃点，不报错、也不该去写路由
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25), ("WLAN", 35)));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.AreEqual(35, fixture.Netsh.MetricOf("WLAN"));
        Assert.AreEqual(AdapterPriorityPlanner.PreferredMetric, fixture.Netsh.RouteMetricOf("以太网 2"),
            "旧快照不含路由时不应改默认路由跃点");
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set route ", StringComparison.Ordinal)));
        Assert.IsFalse(fixture.Store.Exists);
    }

    [TestMethod]
    public async Task Restore_WithoutSnapshotReportsFailureAndRunsNothing()
    {
        using var fixture = new ServiceFixture();

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "没有可恢复的记录");
        Assert.AreEqual(0, fixture.Netsh.Commands.Count, "没有记录时不该执行任何 netsh");
    }

    [TestMethod]
    public async Task Restore_PartiallyMissingAdaptersRestoresRemainingOnes()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("以太网 2", AdapterPriorityPlanner.PreferredMetric);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25), ("旧网卡", 60)));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        StringAssert.Contains(outcome.Message, "1 张", "只恢复还存在的网卡，并在消息里说明数量");
        Assert.AreEqual(25, fixture.Netsh.MetricOf("以太网 2"));
        Assert.IsFalse(fixture.Store.Exists);
    }

    [TestMethod]
    public async Task Restore_AllRecordedAdaptersMissingReportsFailureAndKeepsSnapshot()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("WLAN", 35);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25)));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "都已不存在");
        Assert.IsTrue(fixture.Store.Exists, "没恢复任何东西时记录应保留，便于用户排查");
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set interface ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Restore_WhenAlreadyOriginalJustDeletesSnapshot()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("以太网 2", 25);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25)));

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsTrue(outcome.Success, outcome.Message);
        StringAssert.Contains(outcome.Message, "无需修改");
        Assert.IsFalse(fixture.Store.Exists);
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Restore_CommandFailureRollsBackAndKeepsSnapshot()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25), ("WLAN", 35)));
        fixture.Netsh.SetFails = (name, _) => name == "WLAN";

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "恢复记录保留");
        Assert.AreEqual(AdapterPriorityPlanner.PreferredMetric, fixture.Netsh.MetricOf("以太网 2"),
            "已成功恢复的网卡要回滚回本次恢复前的值");
        Assert.IsTrue(fixture.Store.Exists, "恢复失败必须保留记录，用户才能重试");
        Assert.IsTrue(fixture.Log.Contains("恢复系统自动跃点失败", LogLevel.Error));
    }

    [TestMethod]
    public async Task Restore_VerificationMismatchRollsBackAndKeepsSnapshot()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("以太网 2", AdapterPriorityPlanner.PreferredMetric);
        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25)));
        // 模拟 netsh 退出码 0 但没生效：回读仍是指标 10，而不是记录里的 25
        fixture.Netsh.SetIgnored = (name, _) => name == "以太网 2";

        AdapterPriorityOutcome outcome = await fixture.Service.RestoreAsync();

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "未生效");
        Assert.IsTrue(fixture.Store.Exists);
    }

    // ===================== HasSnapshot =====================

    [TestMethod]
    public void HasSnapshot_ReflectsSnapshotFilePresence()
    {
        using var fixture = new ServiceFixture();
        Assert.IsFalse(fixture.Service.HasSnapshot);

        fixture.Store.Save(Snapshot("以太网 2", ("以太网 2", 25)));
        Assert.IsTrue(fixture.Service.HasSnapshot);

        fixture.Store.Delete();
        Assert.IsFalse(fixture.Service.HasSnapshot);
    }

    [TestMethod]
    public async Task ApplyPreferredAsync_NeverTouchesLoopbackInterface()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("Loopback Pseudo-Interface 1", 75).With("以太网", 25);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网");

        Assert.IsTrue(outcome.Success);
        Assert.AreEqual(75, fixture.Netsh.MetricOf("Loopback Pseudo-Interface 1"), "回环接口跃点必须保持原值");
        Assert.AreEqual(75, fixture.Netsh.RouteMetricOf("Loopback Pseudo-Interface 1"), "回环路由跃点也不能被改");
        Assert.IsFalse(
            fixture.Netsh.Commands.Any(command => command.Contains("Loopback", StringComparison.Ordinal)),
            "不应给回环接口发 set 命令");
        Assert.IsTrue(fixture.Service.HasSnapshot, "快照仍记录全部网卡，恢复时回环值没变就不会被写");
    }

    [TestMethod]
    public async Task ApplyPreferredAsync_RejectsLoopbackAsPreferred()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh.With("Loopback Pseudo-Interface 1", 75).With("以太网", 25);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("Loopback Pseudo-Interface 1");

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "回环");
        Assert.IsFalse(
            fixture.Netsh.Commands.Any(command => command.Contains(" set ", StringComparison.Ordinal)),
            "被拒绝的计划不能发出任何写命令");
        Assert.IsFalse(fixture.Service.HasSnapshot);
    }

    // ===================== 测试替身 =====================

    /// <summary>按给定网卡表生成旧式快照（不含默认路由记录）；IPv6 一律按缺失约定记 -1。</summary>
    private static AdapterPrioritySnapshot Snapshot(string preferredName, params (string Name, int Metric)[] adapters)
        => SnapshotWithRoutes(preferredName, adapters, routes: null);

    /// <summary>生成含默认路由记录的快照（新版本格式）。</summary>
    private static AdapterPrioritySnapshot SnapshotWithRoutes(
        string preferredName,
        (string Name, int Metric)[] adapters,
        (string Name, string NextHop, int Metric)[]? routes)
        => new(
            preferredName,
            adapters
                .Select(adapter => new AdapterMetricState(
                    adapter.Name,
                    adapter.Metric,
                    WindowsAdapterPriorityService.MissingIpv6Metric,
                    Connected: true))
                .ToArray(),
            DateTimeOffset.Now,
            routes?
                .Select(route => new AdapterRouteState(route.Name, route.NextHop, route.Metric))
                .ToArray());

    /// <summary>服务 + 假 netsh + 临时快照目录的组合，析构时清理临时目录。</summary>
    private sealed class ServiceFixture : IDisposable
    {
        public ServiceFixture()
        {
            Store = new AdapterPrioritySnapshotStore(Dir.File(AdapterPrioritySnapshotStore.FileName));
            Service = new WindowsAdapterPriorityService(Log, Store, Netsh.RunAsync);
        }

        public TempDirectory Dir { get; } = new();

        public FakeNetsh Netsh { get; } = new();

        public RecordingLogService Log { get; } = new();

        public AdapterPrioritySnapshotStore Store { get; }

        public WindowsAdapterPriorityService Service { get; }

        public void Dispose() => Dir.Dispose();
    }

    /// <summary>
    /// 假 netsh：维护一份内存网卡表与默认路由表，按真实 netsh 的表格格式渲染 show 输出，
    /// 并解析 set interface / set route（绝不启动真实进程）。可配置某次 set 失败或「假成功」。
    /// <para>
    /// 接口跃点与路由跃点互不联动：真机上它们是两套独立设置，只改接口不会带改默认路由，
    /// 测试必须复现这一点，否则测不出「只改接口导致系统仍走原网卡」的问题。
    /// </para>
    /// </summary>
    private sealed class FakeNetsh
    {
        private readonly List<FakeAdapter> _adapters = new();
        private readonly List<FakeRoute> _routes = new();
        private readonly Dictionary<string, int> _ipv6Metrics = new(StringComparer.OrdinalIgnoreCase);
        private int _nextIndex = 1;

        /// <summary>执行过的命令（参数以空格拼成一行，便于断言）。</summary>
        public List<string> Commands { get; } = new();

        /// <summary>执行过的命令参数原始列表，用于确认「带空格的接口名是一个 argv」。</summary>
        public List<string[]> Invocations { get; } = new();

        public bool Ipv4ShowFails { get; set; }

        public bool Ipv6ShowFails { get; set; }

        public bool RouteShowFails { get; set; }

        /// <summary>返回 true 表示清空目标缓存以非 0 退出码失败（模拟不带管理员令牌）。</summary>
        public bool DestinationCacheFails { get; set; }

        /// <summary>返回 true 表示这次 set interface 以非 0 退出码失败。</summary>
        public Func<string, int, bool>? SetFails { get; set; }

        /// <summary>返回 true 表示这次 set interface 退出码为 0 但设置不生效（用于验证回读校验）。</summary>
        public Func<string, int, bool>? SetIgnored { get; set; }

        /// <summary>返回 true 表示这次 set route 以非 0 退出码失败。</summary>
        public Func<string, int, bool>? SetRouteFails { get; set; }

        /// <summary>返回 true 表示这次 set route 退出码为 0 但设置不生效。</summary>
        public Func<string, int, bool>? SetRouteIgnored { get; set; }

        /// <summary>
        /// 加入一张网卡。<paramref name="route"/> 为 true 时自动附带一条指向它的默认路由
        /// （网关用 10.0.N.1 模拟，跃点默认等于接口跃点，可用 <paramref name="routeMetric"/> 单独指定）。
        /// </summary>
        public FakeNetsh With(
            string name,
            int metric,
            bool connected = true,
            int ipv6 = WindowsAdapterPriorityService.MissingIpv6Metric,
            bool route = true,
            int? routeMetric = null)
        {
            int index = _nextIndex++;
            _adapters.Add(new FakeAdapter(name, metric, connected, index));
            if (ipv6 != WindowsAdapterPriorityService.MissingIpv6Metric)
            {
                _ipv6Metrics[name] = ipv6;
            }

            if (route)
            {
                _routes.Add(new FakeRoute(
                    WindowsAdapterPriorityService.DefaultRoutePrefix,
                    index,
                    $"10.0.{index}.1",
                    routeMetric ?? metric));
            }

            return this;
        }

        public int MetricOf(string name)
            => _adapters.First(adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase)).Metric;

        /// <summary>该网卡当前默认路由的跃点。</summary>
        public int RouteMetricOf(string name)
            => DefaultRouteOf(name).Metric;

        /// <summary>该网卡默认路由的下一跳网关。</summary>
        public string GatewayOf(string name)
            => DefaultRouteOf(name).Gateway;

        /// <summary>测试准备阶段直接改路由跃点（模拟真机上历史遗留的 25 / 0 等值）。</summary>
        public void SetRouteMetric(string name, int metric)
        {
            int index = IndexOf(name);
            int routeIndex = _routes.FindIndex(route =>
                route.Index == index
                && string.Equals(route.Prefix, WindowsAdapterPriorityService.DefaultRoutePrefix, StringComparison.Ordinal));
            Assert.IsTrue(routeIndex >= 0, $"假 netsh 里没有「{name}」的默认路由");
            _routes[routeIndex] = _routes[routeIndex] with { Metric = metric };
        }

        public Task<ProcessOutcome> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(fileName, "netsh.exe", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"被测服务只应执行 netsh.exe，实际为 {fileName}");
            }

            string[] args = arguments.ToArray();
            Invocations.Add(args);
            string command = string.Join(" ", args);
            Commands.Add(command);

            if (command == ShowIpv4)
            {
                return Task.FromResult(Ipv4ShowFails
                    ? new ProcessOutcome(1, string.Empty, "请求的操作需要提升。")
                    : new ProcessOutcome(0, RenderTable(_adapters.Select(ToRow)), string.Empty));
            }

            if (command == ShowIpv6)
            {
                if (Ipv6ShowFails)
                {
                    return Task.FromResult(new ProcessOutcome(1, string.Empty, "IPv6 组件未安装。"));
                }

                IEnumerable<(int Index, string Name, int Metric, bool Connected)> rows = _adapters
                    .Where(adapter => _ipv6Metrics.ContainsKey(adapter.Name))
                    .Select(adapter => (
                        adapter.Index,
                        adapter.Name,
                        Metric: _ipv6Metrics[adapter.Name],
                        adapter.Connected));
                return Task.FromResult(new ProcessOutcome(0, RenderTable(rows), string.Empty));
            }

            if (command == ShowRoute)
            {
                return Task.FromResult(RouteShowFails
                    ? new ProcessOutcome(1, string.Empty, "请求的操作需要提升。")
                    : new ProcessOutcome(0, RenderRouteTable(), string.Empty));
            }

            if (command == FlushDestinationCache)
            {
                return Task.FromResult(DestinationCacheFails
                    ? new ProcessOutcome(1, string.Empty, "请求的操作需要提升(作为管理员运行)。")
                    : new ProcessOutcome(0, string.Empty, string.Empty));
            }

            if (args.Length == 6
                && args[0] == "interface"
                && args[1] == "ipv4"
                && args[2] == "set"
                && args[3] == "interface"
                && args[5].StartsWith("metric=", StringComparison.Ordinal))
            {
                string name = args[4];
                int target = int.Parse(args[5]["metric=".Length..], CultureInfo.InvariantCulture);

                if (SetFails?.Invoke(name, target) == true)
                {
                    return Task.FromResult(new ProcessOutcome(1, string.Empty, "拒绝访问。"));
                }

                if (SetIgnored?.Invoke(name, target) == true)
                {
                    // 命令成功但系统值不变：真实环境里偶发（组策略/驱动拒绝），必须靠回读发现
                    return Task.FromResult(new ProcessOutcome(0, string.Empty, string.Empty));
                }

                int index = _adapters.FindIndex(
                    adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return Task.FromResult(new ProcessOutcome(1, string.Empty, "找不到接口。"));
                }

                _adapters[index] = _adapters[index] with { Metric = target };
                return Task.FromResult(new ProcessOutcome(0, string.Empty, string.Empty));
            }

            // 改默认路由：interface ipv4 set route 0.0.0.0/0 <接口名> <下一跳> metric=<n> store=active
            if (args.Length == 9
                && args[0] == "interface"
                && args[1] == "ipv4"
                && args[2] == "set"
                && args[3] == "route"
                && args[4] == WindowsAdapterPriorityService.DefaultRoutePrefix
                && args[7].StartsWith("metric=", StringComparison.Ordinal)
                && args[8] == "store=active")
            {
                string name = args[5];
                string nextHop = args[6];
                int target = int.Parse(args[7]["metric=".Length..], CultureInfo.InvariantCulture);

                if (SetRouteFails?.Invoke(name, target) == true)
                {
                    return Task.FromResult(new ProcessOutcome(1, string.Empty, "拒绝访问。"));
                }

                if (SetRouteIgnored?.Invoke(name, target) == true)
                {
                    return Task.FromResult(new ProcessOutcome(0, string.Empty, string.Empty));
                }

                int index = _adapters.FindIndex(
                    adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return Task.FromResult(new ProcessOutcome(1, string.Empty, "找不到接口。"));
                }

                int routeIndex = _routes.FindIndex(route =>
                    route.Index == _adapters[index].Index
                    && string.Equals(route.Prefix, WindowsAdapterPriorityService.DefaultRoutePrefix, StringComparison.Ordinal)
                    && string.Equals(route.Gateway, nextHop, StringComparison.OrdinalIgnoreCase));
                if (routeIndex < 0)
                {
                    return Task.FromResult(new ProcessOutcome(1, string.Empty, "找不到路由。"));
                }

                _routes[routeIndex] = _routes[routeIndex] with { Metric = target };
                return Task.FromResult(new ProcessOutcome(0, string.Empty, string.Empty));
            }

            return Task.FromResult(new ProcessOutcome(87, string.Empty, "参数错误。"));
        }

        private int IndexOf(string name)
            => _adapters.First(adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase)).Index;

        private FakeRoute DefaultRouteOf(string name)
        {
            int index = IndexOf(name);
            return _routes.First(route =>
                route.Index == index
                && string.Equals(route.Prefix, WindowsAdapterPriorityService.DefaultRoutePrefix, StringComparison.Ordinal));
        }

        private static (int Index, string Name, int Metric, bool Connected) ToRow(FakeAdapter adapter)
            => (adapter.Index, adapter.Name, adapter.Metric, adapter.Connected);

        /// <summary>按真实 netsh 的列宽渲染接口表（表头同样是本地化中文 + 虚线分隔行）。</summary>
        private static string RenderTable(IEnumerable<(int Index, string Name, int Metric, bool Connected)> rows)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("Idx Met MTU 状态 名称");
            builder.AppendLine("--- ---------- ---------- ------------ ---------------------------");
            foreach ((int index, string name, int metric, bool connected) in rows)
            {
                string state = connected ? "connected" : "disconnected";
                builder.AppendLine($"{index,3} {metric,11} {1500,10}  {state,-12} {name}");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 按真实 netsh 的列宽渲染路由表（Publish Type Met Prefix Idx Gateway/Interface Name）。
        /// 故意用中文系统的本地化表头与「否 手动」两列，因为真机就是中文系统，
        /// 早期实现只认英文 No/Manual 导致真机一条路由都读不到。
        /// </summary>
        private string RenderRouteTable()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("发布 类型 跃点数 前缀 索引 网关/接口名称");
            builder.AppendLine("---- ---- ----- -------- --- ----------------");
            foreach (FakeRoute route in _routes)
            {
                builder.AppendLine($"否 手动 {route.Metric} {route.Prefix} {route.Index} {route.Gateway}");
            }

            return builder.ToString();
        }

        private sealed record FakeAdapter(string Name, int Metric, bool Connected, int Index);

        private sealed record FakeRoute(string Prefix, int Index, string Gateway, int Metric);
    }
}
