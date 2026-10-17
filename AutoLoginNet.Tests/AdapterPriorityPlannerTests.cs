using AutoLoginNet.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.RegularExpressions;

namespace AutoLoginNet.Tests;

/// <summary>
/// 「切换系统优先网卡」的纯逻辑测试：计划怎么算、快照怎么存、结果文件协议怎么定。
/// 这些用例不碰系统网络设置（不执行 netsh），可以随便跑。
/// </summary>
[TestClass]
public sealed class AdapterPriorityPlannerTests
{
    private static AdapterMetricState Adapter(string name, int ipv4, bool connected = true, int ipv6 = -1)
        => new(name, ipv4, ipv6, connected);

    [TestMethod]
    public void CreateApplyPlan_PreferredGetsLowestMetricOthersGetHigh()
    {
        var states = new[]
        {
            Adapter("WLAN", 35),
            Adapter("以太网 2", 25),
            Adapter("vEthernet (Default Switch)", 5000),
        };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(states, "以太网 2");

        Assert.IsFalse(plan.HasError);
        Assert.IsTrue(plan.CanApply);
        Assert.AreEqual(3, plan.Changes.Length);
        // 顺序跟随 states，不额外排序：按名称断言，避免把「首选网卡排第一」当成契约
        Assert.AreEqual(AdapterPriorityPlanner.PreferredMetric, plan.Changes.Single(c => c.Name == "以太网 2").Ipv4Target);
        Assert.AreEqual(AdapterPriorityPlanner.SecondaryMetric, plan.Changes.Single(c => c.Name == "WLAN").Ipv4Target);
        Assert.AreEqual(AdapterPriorityPlanner.SecondaryMetric, plan.Changes.Single(c => c.Name == "vEthernet (Default Switch)").Ipv4Target);
        Assert.AreEqual(25, plan.Changes.Single(c => c.Name == "以太网 2").Ipv4Original, "原值要留在 changes 里，回滚和快照都靠它");
    }

    [TestMethod]
    public void CreateApplyPlan_MatchesPreferredNameCaseInsensitively()
    {
        var states = new[] { Adapter("Ethernet", 25), Adapter("WLAN", 35) };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(states, "ethernet");

        Assert.IsFalse(plan.HasError);
        Assert.AreEqual("Ethernet", plan.PreferredName, "计划里应保留系统里的原始名称，后续 netsh 命令要用它");
        Assert.AreEqual(AdapterPriorityPlanner.PreferredMetric, plan.Changes.Single(c => c.Name == "Ethernet").Ipv4Target);
    }

    [TestMethod]
    public void CreateApplyPlan_LeavesDisconnectedAdaptersUntouched()
    {
        var states = new[]
        {
            Adapter("以太网", 25),
            Adapter("WLAN", 35, connected: false),
        };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(states, "以太网");

        Assert.IsFalse(plan.HasError);
        Assert.AreEqual(1, plan.Changes.Length, "断开状态的网卡不该被写跃点");
        Assert.AreEqual("以太网", plan.Changes[0].Name);
    }

    [TestMethod]
    public void CreateApplyPlan_MissingPreferredAdapterReportsError()
    {
        var states = new[] { Adapter("WLAN", 35) };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(states, "以太网 9");

        Assert.IsTrue(plan.HasError);
        Assert.IsFalse(plan.CanApply);
        Assert.AreEqual(0, plan.Changes.Length);
        StringAssert.Contains(plan.Error!, "找不到");
    }

    [TestMethod]
    public void CreateApplyPlan_DisconnectedPreferredAdapterReportsError()
    {
        var states = new[] { Adapter("WLAN", 35, connected: false) };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(states, "WLAN");

        Assert.IsTrue(plan.HasError);
        StringAssert.Contains(plan.Error!, "未连接");
    }

    [TestMethod]
    public void CreateApplyPlan_BlankNameReportsError()
    {
        var plan = AdapterPriorityPlanner.CreateApplyPlan(new[] { Adapter("WLAN", 35) }, "  ");

        Assert.IsTrue(plan.HasError);
        StringAssert.Contains(plan.Error!, "没有指定");
    }

    [TestMethod]
    public void CreateApplyPlan_NoChangesWhenMetricsAlreadyTargets()
    {
        var states = new[]
        {
            Adapter("以太网", AdapterPriorityPlanner.PreferredMetric),
            Adapter("WLAN", AdapterPriorityPlanner.SecondaryMetric),
        };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(states, "以太网");

        Assert.IsFalse(plan.HasError, "值已经正确不算错误");
        Assert.AreEqual(0, plan.Changes.Length, "值已经正确不该产生系统写入");
        Assert.IsFalse(plan.CanApply);
    }

    [TestMethod]
    public void CreateRestorePlan_WritesBackRecordedValues()
    {
        var snapshot = new AdapterPrioritySnapshot(
            "以太网",
            new[] { Adapter("以太网", 25), Adapter("WLAN", 35, ipv6: 40) },
            DateTimeOffset.Now);
        var current = new[]
        {
            Adapter("以太网", AdapterPriorityPlanner.PreferredMetric),
            Adapter("WLAN", AdapterPriorityPlanner.SecondaryMetric),
        };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(current, snapshot);

        Assert.IsFalse(plan.HasError);
        CollectionAssert.AreEqual(
            new[] { new AdapterMetricChange("以太网", 10, 25), new AdapterMetricChange("WLAN", 9000, 35) },
            plan.Changes);
    }

    [TestMethod]
    public void CreateRestorePlan_SkipsAdaptersThatDisappeared()
    {
        var snapshot = new AdapterPrioritySnapshot(
            "以太网",
            new[] { Adapter("以太网", 25), Adapter("旧网卡", 60) },
            DateTimeOffset.Now);
        var current = new[] { Adapter("以太网", AdapterPriorityPlanner.PreferredMetric) };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(current, snapshot);

        Assert.IsFalse(plan.HasError, "部分网卡不存在不算错误，能恢复几张是几张");
        Assert.AreEqual(1, plan.Changes.Length);
        Assert.AreEqual("以太网", plan.Changes[0].Name);
    }

    [TestMethod]
    public void CreateRestorePlan_ReportsErrorWhenNoRecordedAdapterExists()
    {
        var snapshot = new AdapterPrioritySnapshot("以太网", new[] { Adapter("旧网卡", 60) }, DateTimeOffset.Now);

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(new[] { Adapter("WLAN", 35) }, snapshot);

        Assert.IsTrue(plan.HasError);
        StringAssert.Contains(plan.Error!, "都已不存在");
    }

    [TestMethod]
    public void CreateRestorePlan_EmptySnapshotReportsError()
    {
        var snapshot = new AdapterPrioritySnapshot("以太网", Array.Empty<AdapterMetricState>(), DateTimeOffset.Now);

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(new[] { Adapter("WLAN", 35) }, snapshot);

        Assert.IsTrue(plan.HasError);
    }

    [TestMethod]
    public void CreateRestorePlan_NoChangesWhenAlreadyOriginal()
    {
        var snapshot = new AdapterPrioritySnapshot("以太网", new[] { Adapter("以太网", 25) }, DateTimeOffset.Now);
        var current = new[] { Adapter("以太网", 25) };

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(current, snapshot);

        Assert.IsFalse(plan.HasError);
        Assert.AreEqual(0, plan.Changes.Length);
    }

    [TestMethod]
    public void SnapshotStore_SaveLoadDeleteRoundTrips()
    {
        using var dir = new TempDirectory();
        var store = new AdapterPrioritySnapshotStore(dir.File(AdapterPrioritySnapshotStore.FileName));
        var appliedAt = new DateTimeOffset(2026, 10, 17, 23, 50, 12, TimeSpan.FromHours(8));
        var snapshot = new AdapterPrioritySnapshot(
            "以太网 2",
            new[] { Adapter("以太网 2", 25, ipv6: 30), Adapter("WLAN", 35) },
            appliedAt);

        Assert.IsFalse(store.Exists);
        store.Save(snapshot);
        Assert.IsTrue(store.Exists);

        AdapterPrioritySnapshot? loaded = store.Load();
        Assert.IsNotNull(loaded);
        Assert.AreEqual("以太网 2", loaded!.PreferredName);
        Assert.AreEqual(appliedAt, loaded.AppliedAt, "恢复记录的时间戳应原样往返");
        Assert.AreEqual(2, loaded.Adapters.Length);
        Assert.AreEqual(25, loaded.Adapters.Single(a => a.Name == "以太网 2").Ipv4Metric);
        Assert.AreEqual(30, loaded.Adapters.Single(a => a.Name == "以太网 2").Ipv6Metric);

        store.Delete();
        Assert.IsFalse(store.Exists);
        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void SnapshotStore_LoadReturnsNullWhenFileMissing()
    {
        using var dir = new TempDirectory();
        var store = new AdapterPrioritySnapshotStore(dir.File(AdapterPrioritySnapshotStore.FileName));

        Assert.IsNull(store.Load());
        store.Delete(); // 删除不存在的文件也不该抛
    }

    [TestMethod]
    public void OutcomeFactories_KeepSuccessAndChanges()
    {
        var change = new AdapterMetricChange("以太网", 25, 10);

        AdapterPriorityOutcome done = AdapterPriorityOutcome.Done("已设置", change);
        AdapterPriorityOutcome failure = AdapterPriorityOutcome.Failure("失败原因");

        Assert.IsTrue(done.Success);
        Assert.AreEqual("已设置", done.Message);
        Assert.AreEqual(1, done.Changes.Length);
        Assert.IsFalse(failure.Success);
        Assert.AreEqual(0, failure.Changes.Length);
    }

    [TestMethod]
    public void OperationResult_RoundTripsThroughFileWithOffset()
    {
        using var dir = new TempDirectory();
        string path = dir.File("adapter-priority-result.json");
        var completedAt = new DateTimeOffset(2026, 10, 17, 23, 51, 3, TimeSpan.FromHours(8));

        AdapterPriorityOperationResult.WriteTo(
            path,
            new AdapterPriorityOperationResult(true, AdapterPriorityPlanner.ApplyAction, "以太网 2", "已设为系统首选网卡", completedAt));

        AdapterPriorityOperationResult? loaded = AdapterPriorityOperationResult.ReadFrom(path);

        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded!.Success);
        Assert.AreEqual(AdapterPriorityPlanner.ApplyAction, loaded.Action);
        Assert.AreEqual("以太网 2", loaded.AdapterDisplay);
        Assert.AreEqual(completedAt, loaded.CompletedAt);
        Assert.AreEqual(completedAt.Offset, loaded.CompletedAt.Offset);
        Assert.IsTrue(File.ReadAllText(path).Contains("\"adapterDisplay\"", StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(path).Contains("\"AdapterDisplay\"", StringComparison.Ordinal), "字段名要 camelCase，否则跨版本读不到");
    }

    [TestMethod]
    public void OperationResult_PathArgumentAndNamingConvention()
    {
        Assert.AreEqual("--adapter-priority-result", AdapterPriorityResultFile.ResultPathArgument);

        string path = AdapterPriorityResultFile.CreatePath();

        Assert.AreEqual(AppPaths.DataDirectory, Path.GetDirectoryName(path));
        Assert.IsTrue(
            Regex.IsMatch(Path.GetFileName(path), "^adapter-priority-result-[0-9a-f]{32}\\.json$"),
            $"结果文件名应为 adapter-priority-result-{{32 位十六进制}}.json，实际为 {Path.GetFileName(path)}");
    }
}
