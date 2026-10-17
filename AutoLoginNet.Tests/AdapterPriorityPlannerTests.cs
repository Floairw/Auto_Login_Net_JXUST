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
    public void CreateRouteApplyPlan_PreferredRouteLowestOthersHigh()
    {
        var routes = new[]
        {
            new AdapterRouteState("WLAN", "10.48.164.237", 0),
            new AdapterRouteState("以太网 2", "10.31.126.19", 25),
            new AdapterRouteState("以太网", "10.22.128.1", 0),
        };

        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteApplyPlan(routes, "以太网 2");

        Assert.IsFalse(plan.HasError);
        Assert.IsTrue(plan.CanApply);
        Assert.AreEqual(3, plan.Changes.Length);
        Assert.AreEqual(AdapterPriorityPlanner.PreferredRouteMetric, plan.Changes.Single(c => c.AdapterName == "以太网 2").TargetMetric);
        Assert.AreEqual(25, plan.Changes.Single(c => c.AdapterName == "以太网 2").OriginalMetric, "原值要留在 changes 里，回滚和快照都靠它");
        Assert.AreEqual("10.31.126.19", plan.Changes.Single(c => c.AdapterName == "以太网 2").NextHop);
        Assert.AreEqual(
            AdapterPriorityPlanner.SecondaryRouteMetric,
            plan.Changes.Single(c => c.AdapterName == "WLAN").TargetMetric);
        Assert.AreEqual(
            AdapterPriorityPlanner.SecondaryRouteMetric,
            plan.Changes.Single(c => c.AdapterName == "以太网").TargetMetric,
            "计划器不判断连接状态，只按传入的路由计算；断开网卡由平台层过滤后再传入");
    }

    [TestMethod]
    public void CreateRouteApplyPlan_NoChangesWhenMetricsAlreadyTargets()
    {
        var routes = new[]
        {
            new AdapterRouteState("以太网 2", "10.31.126.19", AdapterPriorityPlanner.PreferredRouteMetric),
            new AdapterRouteState("WLAN", "10.48.164.237", AdapterPriorityPlanner.SecondaryRouteMetric),
        };

        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteApplyPlan(routes, "以太网 2");

        Assert.IsFalse(plan.HasError, "值已经正确不算错误");
        Assert.AreEqual(0, plan.Changes.Length, "值已经正确不该产生系统写入");
        Assert.IsFalse(plan.CanApply);
    }

    [TestMethod]
    public void CreateRouteApplyPlan_PreferredWithoutDefaultRouteReportsError()
    {
        var routes = new[] { new AdapterRouteState("WLAN", "10.48.164.237", 0) };

        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteApplyPlan(routes, "以太网 2");

        Assert.IsTrue(plan.HasError);
        Assert.AreEqual(0, plan.Changes.Length);
        StringAssert.Contains(plan.Error!, "没有默认路由");
    }

    [TestMethod]
    public void CreateRouteApplyPlan_EmptyRoutesReportsError()
    {
        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteApplyPlan(Array.Empty<AdapterRouteState>(), "以太网 2");

        Assert.IsTrue(plan.HasError);
        StringAssert.Contains(plan.Error!, "读取不到");
    }

    [TestMethod]
    public void CreateRouteApplyPlan_BlankNameReportsError()
    {
        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteApplyPlan(
            new[] { new AdapterRouteState("WLAN", "10.48.164.237", 0) },
            " ");

        Assert.IsTrue(plan.HasError);
        StringAssert.Contains(plan.Error!, "没有指定");
    }

    [TestMethod]
    public void CreateRouteRestorePlan_WritesBackRecordedMetrics()
    {
        var snapshot = new AdapterPrioritySnapshot(
            "以太网 2",
            new[] { Adapter("以太网 2", AdapterPriorityPlanner.PreferredMetric) },
            DateTimeOffset.Now,
            new[]
            {
                new AdapterRouteState("以太网 2", "10.31.126.19", 25),
                new AdapterRouteState("WLAN", "10.48.164.237", 0),
            });
        var current = new[]
        {
            new AdapterRouteState("以太网 2", "10.31.126.19", AdapterPriorityPlanner.PreferredRouteMetric),
            new AdapterRouteState("WLAN", "10.48.164.237", AdapterPriorityPlanner.SecondaryRouteMetric),
        };

        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteRestorePlan(current, snapshot);

        Assert.IsFalse(plan.HasError);
        Assert.AreEqual(2, plan.Changes.Length);
        Assert.AreEqual(
            new AdapterRouteChange("以太网 2", "10.31.126.19", AdapterPriorityPlanner.PreferredRouteMetric, 25),
            plan.Changes.Single(c => c.AdapterName == "以太网 2"));
        Assert.AreEqual(
            new AdapterRouteChange("WLAN", "10.48.164.237", AdapterPriorityPlanner.SecondaryRouteMetric, 0),
            plan.Changes.Single(c => c.AdapterName == "WLAN"));
    }

    [TestMethod]
    public void CreateRouteRestorePlan_SkipsRoutesWithChangedGatewayOrGone()
    {
        var snapshot = new AdapterPrioritySnapshot(
            "以太网 2",
            new[] { Adapter("以太网 2", 10) },
            DateTimeOffset.Now,
            new[]
            {
                new AdapterRouteState("以太网 2", "10.31.126.19", 25),
                new AdapterRouteState("旧网卡", "10.9.9.9", 0),
            });
        var current = new[]
        {
            // 同一张网卡换了网关：快照里的下一跳已不存在，不能硬写
            new AdapterRouteState("以太网 2", "10.31.126.254", 0),
        };

        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteRestorePlan(current, snapshot);

        Assert.IsFalse(plan.HasError, "路由消失/网关变化不算错误，能恢复几条是几条");
        Assert.AreEqual(0, plan.Changes.Length);
    }

    [TestMethod]
    public void CreateRouteRestorePlan_LegacySnapshotWithoutRoutesIsNoOp()
    {
        var legacy = new AdapterPrioritySnapshot(
            "以太网 2",
            new[] { Adapter("以太网 2", 25), Adapter("WLAN", 35) },
            DateTimeOffset.Now);
        var current = new[]
        {
            new AdapterRouteState("以太网 2", "10.31.126.19", 0),
            new AdapterRouteState("WLAN", "10.48.164.237", 9000),
        };

        AdapterRoutePlan plan = AdapterPriorityPlanner.CreateRouteRestorePlan(current, legacy);

        Assert.IsFalse(plan.HasError);
        Assert.AreEqual(0, plan.Changes.Length, "旧版本快照没有路由记录，只恢复接口跃点即可");
    }

    [TestMethod]
    public void CreateSnapshot_KeepsEarliestOriginalsAcrossRepeatedApply()
    {
        // 用户已经应用过一次（接口跃点 25/35 被改成 10/9000），但没恢复就再点一次：
        // 新快照必须继续记 25/35，否则「恢复」只能回到 10/9000 这个中间状态。
        var previous = new AdapterPrioritySnapshot(
            "以太网 2",
            new[] { Adapter("以太网 2", 25), Adapter("WLAN", 35) },
            DateTimeOffset.Now,
            new[]
            {
                new AdapterRouteState("以太网 2", "10.31.126.19", 25),
                new AdapterRouteState("WLAN", "10.48.164.237", 0),
            });
        var currentStates = new[] { Adapter("以太网 2", 10), Adapter("WLAN", 9000) };
        var currentRoutes = new[]
        {
            new AdapterRouteState("以太网 2", "10.31.126.19", 0),
            new AdapterRouteState("WLAN", "10.48.164.237", 9000),
        };

        AdapterPrioritySnapshot snapshot = AdapterPriorityPlanner.CreateSnapshot(
            "以太网 2", currentStates, currentRoutes, previous, DateTimeOffset.Now);

        Assert.AreEqual(25, snapshot.Adapters.Single(a => a.Name == "以太网 2").Ipv4Metric);
        Assert.AreEqual(35, snapshot.Adapters.Single(a => a.Name == "WLAN").Ipv4Metric);
        Assert.AreEqual(25, snapshot.RoutesOrEmpty.Single(r => r.AdapterName == "以太网 2").Metric);
        Assert.AreEqual(0, snapshot.RoutesOrEmpty.Single(r => r.AdapterName == "WLAN").Metric);
    }

    [TestMethod]
    public void CreateSnapshot_WithoutPreviousRecordsCurrentValues()
    {
        var states = new[] { Adapter("以太网 2", 25), Adapter("WLAN", 35) };
        var routes = new[] { new AdapterRouteState("以太网 2", "10.31.126.19", 25) };

        AdapterPrioritySnapshot snapshot = AdapterPriorityPlanner.CreateSnapshot(
            "以太网 2", states, routes, previous: null, DateTimeOffset.Now);

        Assert.AreEqual(25, snapshot.Adapters.Single(a => a.Name == "以太网 2").Ipv4Metric);
        Assert.AreEqual(35, snapshot.Adapters.Single(a => a.Name == "WLAN").Ipv4Metric);
        Assert.AreEqual(25, snapshot.RoutesOrEmpty.Single().Metric);
    }

    [TestMethod]
    public void CreateSnapshot_DropsAdaptersThatDisappeared()
    {
        var previous = new AdapterPrioritySnapshot(
            "以太网 2",
            new[] { Adapter("以太网 2", 25), Adapter("旧网卡", 60) },
            DateTimeOffset.Now);
        var states = new[] { Adapter("以太网 2", 10) };

        AdapterPrioritySnapshot snapshot = AdapterPriorityPlanner.CreateSnapshot(
            "以太网 2", states, Array.Empty<AdapterRouteState>(), previous, DateTimeOffset.Now);

        Assert.AreEqual(1, snapshot.Adapters.Length, "系统里已经没有的网卡不能进新快照，否则恢复时会报「都已不存在」");
        Assert.AreEqual(25, snapshot.Adapters[0].Ipv4Metric);
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
            appliedAt,
            new[]
            {
                new AdapterRouteState("以太网 2", "10.31.126.19", 25),
                new AdapterRouteState("WLAN", "10.48.164.237", 0),
            });

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
        Assert.AreEqual(2, loaded.RoutesOrEmpty.Length, "默认路由跃点也要进恢复记录，否则恢复不完整");
        Assert.AreEqual(25, loaded.RoutesOrEmpty.Single(route => route.AdapterName == "以太网 2").Metric);
        Assert.AreEqual("10.48.164.237", loaded.RoutesOrEmpty.Single(route => route.AdapterName == "WLAN").NextHop);

        store.Delete();
        Assert.IsFalse(store.Exists);
        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void SnapshotStore_LoadToleratesLegacyFileWithoutRoutes()
    {
        using var dir = new TempDirectory();
        string path = dir.File(AdapterPrioritySnapshotStore.FileName);
        // 旧版本（只记接口跃点）写下的文件：没有 routes 字段，必须仍能读出来只恢复接口跃点
        File.WriteAllText(path, """
        {
          "preferredName": "以太网 2",
          "adapters": [
            { "name": "以太网 2", "ipv4Metric": 25, "ipv6Metric": 30, "connected": true },
            { "name": "WLAN", "ipv4Metric": 35, "ipv6Metric": -1, "connected": true }
          ],
          "appliedAt": "2026-10-17T23:50:12+08:00"
        }
        """);

        AdapterPrioritySnapshot? loaded = new AdapterPrioritySnapshotStore(path).Load();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded!.Adapters.Length);
        Assert.IsNull(loaded.Routes, "旧文件没有 routes，应读成 null 而不是崩溃");
        Assert.AreEqual(0, loaded.RoutesOrEmpty.Length);
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
