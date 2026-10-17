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
/// 真实只读的 <c>netsh show</c> 解析约定见 <see cref="ParseInterfaceTable_*"/> 系列用例。
/// </para>
/// </summary>
[TestClass]
public sealed class AdapterPriorityServiceTests
{
    private const string ShowIpv4 = "interface ipv4 show interfaces";
    private const string ShowIpv6 = "interface ipv6 show interfaces";

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
        Assert.AreEqual("以太网 2", rows[1].Name);
        Assert.AreEqual(25, rows[1].Metric);
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
    public async Task ApplyPreferred_WhenMetricsAlreadyTargetsDoesNotTouchSystem()
    {
        using var fixture = new ServiceFixture();
        fixture.Netsh
            .With("以太网 2", AdapterPriorityPlanner.PreferredMetric)
            .With("WLAN", AdapterPriorityPlanner.SecondaryMetric);

        AdapterPriorityOutcome outcome = await fixture.Service.ApplyPreferredAsync("以太网 2");

        Assert.IsTrue(outcome.Success, outcome.Message);
        Assert.AreEqual(0, outcome.Changes.Length);
        StringAssert.Contains(outcome.Message, "无需修改");
        Assert.IsFalse(fixture.Store.Exists, "没有改动就不该产生恢复记录");
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set interface ", StringComparison.Ordinal)));
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
        Assert.IsFalse(netsh.Commands.Any(c => c.Contains(" set interface ", StringComparison.Ordinal)));
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
        Assert.IsFalse(fixture.Netsh.Commands.Any(c => c.Contains(" set interface ", StringComparison.Ordinal)));
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

    /// <summary>按给定网卡表生成快照；IPv6 一律按缺失约定记 -1（本功能只写 IPv4）。</summary>
    private static AdapterPrioritySnapshot Snapshot(string preferredName, params (string Name, int Metric)[] adapters)
        => new(
            preferredName,
            adapters
                .Select(adapter => new AdapterMetricState(
                    adapter.Name,
                    adapter.Metric,
                    WindowsAdapterPriorityService.MissingIpv6Metric,
                    Connected: true))
                .ToArray(),
            DateTimeOffset.Now);

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
    /// 假 netsh：维护一份内存网卡表，按真实 netsh 的表格格式渲染 show 输出，
    /// 并解析 set interface（绝不启动真实进程）。可配置某次 set 失败或「假成功」。
    /// </summary>
    private sealed class FakeNetsh
    {
        private readonly List<FakeAdapter> _adapters = new();
        private readonly Dictionary<string, int> _ipv6Metrics = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>执行过的命令（参数以空格拼成一行，便于断言）。</summary>
        public List<string> Commands { get; } = new();

        /// <summary>执行过的命令参数原始列表，用于确认「带空格的接口名是一个 argv」。</summary>
        public List<string[]> Invocations { get; } = new();

        public bool Ipv4ShowFails { get; set; }

        public bool Ipv6ShowFails { get; set; }

        /// <summary>返回 true 表示这次 set 以非 0 退出码失败。</summary>
        public Func<string, int, bool>? SetFails { get; set; }

        /// <summary>返回 true 表示这次 set 退出码为 0 但设置不生效（用于验证回读校验）。</summary>
        public Func<string, int, bool>? SetIgnored { get; set; }

        public FakeNetsh With(
            string name,
            int metric,
            bool connected = true,
            int ipv6 = WindowsAdapterPriorityService.MissingIpv6Metric)
        {
            _adapters.Add(new FakeAdapter(name, metric, connected));
            if (ipv6 != WindowsAdapterPriorityService.MissingIpv6Metric)
            {
                _ipv6Metrics[name] = ipv6;
            }

            return this;
        }

        public int MetricOf(string name)
            => _adapters.First(adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase)).Metric;

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

                IEnumerable<(string Name, int Metric, bool Connected)> rows = _adapters
                    .Where(adapter => _ipv6Metrics.ContainsKey(adapter.Name))
                    .Select(adapter => (
                        Name: adapter.Name,
                        Metric: _ipv6Metrics[adapter.Name],
                        Connected: adapter.Connected));
                return Task.FromResult(new ProcessOutcome(0, RenderTable(rows), string.Empty));
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

            return Task.FromResult(new ProcessOutcome(87, string.Empty, "参数错误。"));
        }

        private static (string Name, int Metric, bool Connected) ToRow(FakeAdapter adapter)
            => (adapter.Name, adapter.Metric, adapter.Connected);

        /// <summary>按真实 netsh 的列宽渲染表格（表头同样是本地化中文 + 虚线分隔行）。</summary>
        private static string RenderTable(IEnumerable<(string Name, int Metric, bool Connected)> rows)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("Idx Met MTU 状态 名称");
            builder.AppendLine("--- ---------- ---------- ------------ ---------------------------");
            int index = 1;
            foreach ((string name, int metric, bool connected) in rows)
            {
                string state = connected ? "connected" : "disconnected";
                builder.AppendLine($"{index,3} {metric,11} {1500,10}  {state,-12} {name}");
                index++;
            }

            return builder.ToString();
        }

        private sealed record FakeAdapter(string Name, int Metric, bool Connected);
    }
}
