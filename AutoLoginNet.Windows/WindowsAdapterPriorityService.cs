using AutoLoginNet.Diagnostics;
using AutoLoginNet.Windows;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AutoLoginNet.Network;

/// <summary>
/// 基于 <c>netsh</c> 的接口跃点（metric）与 IPv4 默认路由跃点读写实现。
/// <para>
/// 为什么必须同时改<b>两套</b>跃点（真机实测证据）：
/// 只改接口跃点时，以太网 2 的接口跃点已经变成 10、WLAN 变成 9000，但
/// <c>Find-NetRoute 8.8.8.8</c> / <c>Test-NetConnection</c> 仍然走 WLAN——
/// <c>Get-NetRoute 0.0.0.0/0</c> 显示 WLAN 的默认路由跃点是 0、以太网 2 的是 25，
/// Windows 选路先比默认路由自身的跃点，再比接口跃点。所以必须用
/// <c>netsh interface ipv4 set interface</c> 改接口跃点的同时，用
/// <c>netsh interface ipv4 set route</c> 改默认路由（0.0.0.0/0）自身的跃点，
/// 否则界面上「设置成功」但系统依旧走原来的网卡，属于最危险的假成功。
/// </para>
/// <para>
/// 其余取舍：
/// 1) 网卡与下一跳都只作为<b>单独一个 argv</b> 传入（名称可含空格、括号），由 .NET 负责加引号，不手工拼命令行；
/// 2) 写入后立即回读并逐项比对（接口一套、路由一套），只有真正生效才报告成功；
/// 3) 路由跃点用 <c>store=active</c> 写入：只改当前运行配置、不写持久化注册表，重启后由 DHCP 重新生成默认路由，
///    不会在系统里留下难清理的改动，与「可随时恢复」的定位一致；
/// 4) 只改 IPv4：校园网认证、DNS 与绝大多数校园服务都走 IPv4，动 IPv6 收益不明却可能影响双栈。
/// </para>
/// </summary>
public sealed partial class WindowsAdapterPriorityService : IAdapterPriorityService
{
    /// <summary>
    /// 网卡没有出现在 <c>netsh interface ipv6 show interfaces</c> 输出里（该网卡未启用 IPv6）时，
    /// <see cref="AdapterMetricState.Ipv6Metric"/> 使用 -1 作占位。
    /// <para>
    /// 约定用 -1 而不是 0：0 在 Windows 里是合法的「自动跃点」值，用 0 会把「没有 IPv6」
    /// 伪装成「IPv6 跃点为 0」；-1 不可能是真实跃点，一眼能分辨。本功能只写 IPv4，
    /// 该值只进快照与展示，不会回写系统。
    /// </para>
    /// </summary>
    internal const int MissingIpv6Metric = -1;

    /// <summary>
    /// IPv4 默认路由的前缀。只有它代表「从这张网卡出去上网」；
    /// 同网段、组播、回环等其它前缀都是内部路由，不在本功能职责内。
    /// </summary>
    internal const string DefaultRoutePrefix = "0.0.0.0/0";

    /// <summary>netsh 可执行文件名（Windows 自带，位于 System32，无需完整路径）。</summary>
    private const string NetshFileName = "netsh.exe";

    /// <summary>单条 netsh 命令超时：改跃点很快，10 秒足够，超时即视为失败以便回滚。</summary>
    private const int NetshTimeoutMs = 10000;

    /// <summary>日志分类，与界面「网络」页一致。</summary>
    private const string LogCategory = "网络";

    /// <summary>
    /// 清空系统「目标缓存」（destination cache）的参数。Windows 会为每条用过的目标记住
    /// 下一跳与出接口；只改跃点不清缓存时，缓存里的旧条目仍按旧网卡选路，
    /// 表现就是「跃点改了，新连接还是从旧网卡出去」，要等缓存自己过期才恢复。
    /// </summary>
    private static readonly string[] FlushDestinationCacheArguments =
    {
        "interface", "ipv4", "delete", "destinationcache",
    };

    private readonly ILogService _log;
    private readonly AdapterPrioritySnapshotStore _store;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>> _runAsync;

    /// <summary>生产用构造：走真实 <see cref="ProcessRunner"/> 执行 netsh。</summary>
    public WindowsAdapterPriorityService(ILogService log, AdapterPrioritySnapshotStore store)
        : this(log, store, null)
    {
    }

    /// <summary>
    /// 测试用构造：注入命令执行器，单元测试就能完整验证「发什么命令、失败怎么回滚」，
    /// 而<b>不必真的执行 netsh set</b>（那会改动运行测试的机器的网络设置）。
    /// </summary>
    internal WindowsAdapterPriorityService(
        ILogService log,
        AdapterPrioritySnapshotStore store,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>>? runAsync)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runAsync = runAsync ?? DefaultRunAsync;
    }

    /// <inheritdoc />
    public bool HasSnapshot => _store.Exists;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdapterMetricState>> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        // IPv4 是唯一会被写入的协议，读不到它就没有继续的意义：返回空列表让上层报错，不抛异常。
        ProcessOutcome ipv4 = await _runAsync(
            NetshFileName,
            new[] { "interface", "ipv4", "show", "interfaces" },
            cancellationToken).ConfigureAwait(false);

        if (!ipv4.Succeeded)
        {
            _log.Error($"读取网卡 IPv4 跃点失败：{FirstLine(ipv4.StdErr)}", LogCategory);
            return Array.Empty<AdapterMetricState>();
        }

        IReadOnlyList<NetshInterfaceRow> ipv4Rows = ParseInterfaceTable(ipv4.StdOut);

        // IPv6 只用于快照与展示：读不到不算整体失败（很多网卡本来就关闭了 IPv6），
        // 但要把缺失的网卡记成 -1，避免把「没有这条记录」误当成「跃点为 0」。
        var ipv6Metrics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ProcessOutcome ipv6 = await _runAsync(
            NetshFileName,
            new[] { "interface", "ipv6", "show", "interfaces" },
            cancellationToken).ConfigureAwait(false);

        if (ipv6.Succeeded)
        {
            foreach (NetshInterfaceRow row in ParseInterfaceTable(ipv6.StdOut))
            {
                ipv6Metrics[row.Name] = row.Metric;
            }
        }
        else
        {
            _log.Warn($"读取网卡 IPv6 跃点失败（不影响 IPv4 设置）：{FirstLine(ipv6.StdErr)}", LogCategory);
        }

        var states = new AdapterMetricState[ipv4Rows.Count];
        for (int i = 0; i < ipv4Rows.Count; i++)
        {
            NetshInterfaceRow row = ipv4Rows[i];
            states[i] = new AdapterMetricState(
                row.Name,
                row.Metric,
                ipv6Metrics.TryGetValue(row.Name, out int ipv6Metric) ? ipv6Metric : MissingIpv6Metric,
                row.Connected);
        }

        return states;
    }

    /// <summary>
    /// 读取系统当前的 IPv4 默认路由（0.0.0.0/0）。
    /// <para>
    /// <c>netsh interface ipv4 show route</c> 的 Idx 列是接口索引，没有接口名，
    /// 因此还要再读一次 <c>show interfaces</c> 把 Idx 映射成名称（后续 set route 需要名称）。
    /// 这里只做只读查询，不改 <see cref="ReadStateAsync"/> 的对外语义。
    /// </para>
    /// <para>
    /// 过滤规则：只保留默认前缀；网关列必须是 IPv4 地址（On-link、接口名之类无法用 set route 定位）；
    /// 回环接口的路由不参与上网选路，直接跳过。
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<AdapterRouteState>> ReadRoutesAsync(CancellationToken cancellationToken = default)
    {
        ProcessOutcome routesShow = await _runAsync(
            NetshFileName,
            new[] { "interface", "ipv4", "show", "route" },
            cancellationToken).ConfigureAwait(false);

        if (!routesShow.Succeeded)
        {
            _log.Error($"读取系统 IPv4 默认路由失败：{FirstLine(routesShow.StdErr)}", LogCategory);
            return Array.Empty<AdapterRouteState>();
        }

        IReadOnlyList<NetshRouteRow> rows = ParseRouteTable(routesShow.StdOut);
        if (rows.Count == 0)
        {
            _log.Warn("没有从 netsh 输出里解析到 IPv4 默认路由（0.0.0.0/0）", LogCategory);
            return Array.Empty<AdapterRouteState>();
        }

        ProcessOutcome interfaces = await _runAsync(
            NetshFileName,
            new[] { "interface", "ipv4", "show", "interfaces" },
            cancellationToken).ConfigureAwait(false);

        if (!interfaces.Succeeded)
        {
            _log.Error($"读取网卡接口表失败，无法确定默认路由属于哪张网卡：{FirstLine(interfaces.StdErr)}", LogCategory);
            return Array.Empty<AdapterRouteState>();
        }

        var names = new Dictionary<int, string>();
        foreach (NetshInterfaceRow row in ParseInterfaceTable(interfaces.StdOut))
        {
            names[row.Index] = row.Name;
        }

        var result = new List<AdapterRouteState>(rows.Count);
        foreach (NetshRouteRow row in rows)
        {
            if (!names.TryGetValue(row.Index, out string? name))
            {
                _log.Warn($"默认路由（网关 {row.Gateway}）的接口 Idx {row.Index} 不在接口表里，已跳过", LogCategory);
                continue;
            }

            if (IsLoopbackAdapter(name))
            {
                continue;
            }

            result.Add(new AdapterRouteState(name, row.Gateway, row.Metric));
        }

        if (result.Count == 0)
        {
            _log.Warn("解析到默认路由，但没有可用的上网接口（网关不是 IPv4 或属于回环接口）", LogCategory);
        }

        return result;
    }

    /// <summary>
    /// 只保留「当前已连接」网卡的默认路由。
    /// <para>
    /// 原因（真机验证）：断开网卡上的持久默认路由同样出现在 netsh show route 里，
    /// 但在断开接口上执行 netsh set route 并不可靠，可能直接报错；一旦报错按现有流程会整单回滚，
    /// 用户点了「设为系统首选网卡」等于没效果。断开网卡不写也不会抢路：
    /// 选路按「路由跃点 + 接口跃点」，它两者的和仍明显大于首选网卡。
    /// </para>
    /// <para>
    /// 快照仍记录全部路由（含断开网卡的），不丢信息；只是不往系统里写。
    /// </para>
    /// </summary>
    private IReadOnlyList<AdapterRouteState> FilterConnectedRoutes(
        IReadOnlyList<AdapterRouteState> routes,
        IReadOnlyList<AdapterMetricState> states)
    {
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AdapterMetricState state in states)
        {
            if (state.Connected)
            {
                connected.Add(state.Name);
            }
        }

        var writable = new List<AdapterRouteState>(routes.Count);
        int skipped = 0;
        foreach (AdapterRouteState route in routes)
        {
            if (connected.Contains(route.AdapterName))
            {
                writable.Add(route);
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            _log.Info($"跳过 {skipped} 条断开网卡的默认路由（只在已连接网卡上改路由跃点）", LogCategory);
        }

        return writable;
    }

    /// <inheritdoc />
    public async Task<AdapterPriorityOutcome> ApplyPreferredAsync(
        string preferredName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preferredName))
        {
            return FailApply("没有指定上网网卡");
        }

        IReadOnlyList<AdapterMetricState> states = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (states.Count == 0)
        {
            return FailApply($"读取系统网卡跃点失败，无法把「{preferredName}」设为系统首选（可尝试以管理员身份运行）");
        }

        // 回环接口永远不参与上网选路：实测 netsh 里它同样标记为 connected，
        // 若照着「其余已连接网卡」去调它，既没有意义，也可能影响本机回环路由。
        if (IsLoopbackAdapter(preferredName))
        {
            return FailApply($"「{preferredName}」是回环接口，不能作为上网网卡");
        }

        // 路由必须一起读：只改接口跃点不足以保证系统真的从首选网卡上网（见类摘要的真机证据）。
        IReadOnlyList<AdapterRouteState> routes = await ReadRoutesAsync(cancellationToken).ConfigureAwait(false);

        // 计划只看「可调整」的网卡，但快照仍记全部网卡：恢复时按记录比对，回环值没变就不会被写
        AdapterMetricState[] editable = states.Where(state => !IsLoopbackAdapter(state.Name)).ToArray();
        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(editable, preferredName);
        if (plan.HasError)
        {
            return FailApply(plan.Error!);
        }

        // 路由只改「当前已连接」网卡的：断开网卡上的持久默认路由不写，否则 set route 报错会整单回滚。
        IReadOnlyList<AdapterRouteState> writableRoutes = FilterConnectedRoutes(routes, states);
        AdapterRoutePlan routePlan = AdapterPriorityPlanner.CreateRouteApplyPlan(writableRoutes, preferredName);
        if (routePlan.HasError)
        {
            return FailApply(routePlan.Error!);
        }

        if (!plan.CanApply && !routePlan.CanApply)
        {
            // 两套跃点都已经正确就不写系统：既避免无谓写入，也避免把「本来就对」误报成一次修改。
            string message =
                $"「{plan.PreferredName}」的接口跃点与默认路由跃点都已是目标值" +
                $"（接口 {AdapterPriorityPlanner.PreferredMetric}、默认路由 {AdapterPriorityPlanner.PreferredRouteMetric}），无需修改";
            _log.Info(message, LogCategory);
            return AdapterPriorityOutcome.Done(message);
        }

        // 先落盘恢复记录，再动系统：万一进程在改到一半时崩溃/被杀，用户仍能一键恢复。
        // 用 CreateSnapshot 合并旧记录：用户可能已经 apply 过还没恢复，重复点击必须保留最早的原值，
        // 否则恢复只能回到「上一次改后的值」，回不到真正的原值。
        AdapterPrioritySnapshot? previous = _store.Load();
        AdapterPrioritySnapshot snapshot = AdapterPriorityPlanner.CreateSnapshot(
            plan.PreferredName,
            states.ToArray(),
            routes.ToArray(),
            previous,
            DateTimeOffset.Now);

        if (!TrySaveSnapshot(snapshot, out string? saveError))
        {
            return FailApply($"写入恢复记录失败，已取消修改：{saveError}");
        }

        var appliedInterfaces = new List<AdapterMetricChange>(plan.Changes.Length);
        var appliedRoutes = new List<AdapterRouteChange>(routePlan.Changes.Length);
        try
        {
            // 先接口、后路由：接口命令失败时系统还没被路由改动，需要回滚的面更小。
            foreach (AdapterMetricChange change in plan.Changes)
            {
                if (!await TrySetMetricAsync(change.Name, change.Ipv4Target, cancellationToken).ConfigureAwait(false))
                {
                    return await FailApplyWithRollbackAsync(
                        $"设置「{change.Name}」接口跃点为 {change.Ipv4Target} 失败",
                        appliedInterfaces,
                        appliedRoutes).ConfigureAwait(false);
                }

                appliedInterfaces.Add(change);
            }

            string? mismatch = await FindMismatchAsync(appliedInterfaces, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                return await FailApplyWithRollbackAsync(mismatch, appliedInterfaces, appliedRoutes).ConfigureAwait(false);
            }

            foreach (AdapterRouteChange change in routePlan.Changes)
            {
                if (!await TrySetRouteMetricAsync(change, cancellationToken).ConfigureAwait(false))
                {
                    return await FailApplyWithRollbackAsync(
                        $"设置「{change.AdapterName}」默认路由（网关 {change.NextHop}）跃点为 {change.TargetMetric} 失败",
                        appliedInterfaces,
                        appliedRoutes).ConfigureAwait(false);
                }

                appliedRoutes.Add(change);
            }

            string? routeMismatch = await FindRouteMismatchAsync(appliedRoutes, cancellationToken).ConfigureAwait(false);
            if (routeMismatch is not null)
            {
                return await FailApplyWithRollbackAsync(routeMismatch, appliedInterfaces, appliedRoutes).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消也先把已改的项改回去，别把系统留在「一半改过」的状态；
            // 回滚本身不能再被取消，否则可能永久改坏用户网络。
            await RollbackAsync(appliedInterfaces, appliedRoutes).ConfigureAwait(false);
            throw;
        }

        string done =
            $"已将「{plan.PreferredName}」设为系统首选网卡：接口跃点 {AdapterPriorityPlanner.PreferredMetric}、" +
            $"默认路由跃点 {AdapterPriorityPlanner.PreferredRouteMetric}；其余网卡接口跃点 {AdapterPriorityPlanner.SecondaryMetric}、" +
            $"默认路由跃点 {AdapterPriorityPlanner.SecondaryRouteMetric}，" +
            $"共调整 {plan.Changes.Length} 张网卡、{routePlan.Changes.Length} 条默认路由（可随时恢复）";
        _log.Info(done, LogCategory);
        await TryFlushDestinationCacheAsync(cancellationToken).ConfigureAwait(false);
        return AdapterPriorityOutcome.Done(done, plan.Changes);
    }

    /// <inheritdoc />
    public async Task<AdapterPriorityOutcome> RestoreAsync(CancellationToken cancellationToken = default)
    {
        AdapterPrioritySnapshot? snapshot = _store.Load();
        if (snapshot is null)
        {
            string message = "没有可恢复的记录，可能从未设置过系统首选网卡，或记录已被清除";
            _log.Warn(message, LogCategory);
            return AdapterPriorityOutcome.Failure(message);
        }

        IReadOnlyList<AdapterMetricState> states = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (states.Count == 0)
        {
            string message = "读取系统网卡跃点失败，无法恢复原跃点（可尝试以管理员身份运行）";
            _log.Error(message, LogCategory);
            return AdapterPriorityOutcome.Failure(message);
        }

        IReadOnlyList<AdapterRouteState> routes = await ReadRoutesAsync(cancellationToken).ConfigureAwait(false);

        // 新版本记录里含默认路由：读不到路由就无法恢复它们，此时必须报错，
        // 否则会「恢复成功并删掉记录」，而路由仍停在 0/9000，用户永远回不到原状态。
        if (snapshot.RoutesOrEmpty.Length > 0 && routes.Count == 0)
        {
            string message = "读取系统默认路由失败，无法恢复原默认路由跃点（可尝试以管理员身份运行）";
            _log.Error(message, LogCategory);
            return AdapterPriorityOutcome.Failure(message);
        }

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(states, snapshot);
        if (plan.HasError)
        {
            _log.Error(plan.Error!, LogCategory);
            return AdapterPriorityOutcome.Failure(plan.Error!);
        }

        // 旧版本快照没有路由记录（RoutesOrEmpty 为空）时得到空计划：只恢复接口跃点，不报错。
        // 恢复同样只写「当前已连接」网卡的路由：断开网卡写路由不可靠；路由是 store=active，
        // 重启后由系统/DHCP 重新生成，不会留下脏数据。
        IReadOnlyList<AdapterRouteState> writableRoutes = FilterConnectedRoutes(routes, states);
        AdapterRoutePlan routePlan = AdapterPriorityPlanner.CreateRouteRestorePlan(writableRoutes, snapshot);

        if (plan.Changes.Length == 0 && routePlan.Changes.Length == 0)
        {
            // 记录里的值已经都对上了，说明用户或系统已经手动恢复过；清掉记录即可，不再写系统。
            _store.Delete();
            string message = "各网卡的接口跃点与默认路由跃点已与恢复记录一致，无需修改（已清除恢复记录）";
            _log.Info(message, LogCategory);
            return AdapterPriorityOutcome.Done(message);
        }

        var appliedInterfaces = new List<AdapterMetricChange>(plan.Changes.Length);
        var appliedRoutes = new List<AdapterRouteChange>(routePlan.Changes.Length);
        try
        {
            foreach (AdapterMetricChange change in plan.Changes)
            {
                if (!await TrySetMetricAsync(change.Name, change.Ipv4Target, cancellationToken).ConfigureAwait(false))
                {
                    return await FailRestoreWithRollbackAsync(
                        $"恢复「{change.Name}」接口跃点为 {change.Ipv4Target} 失败",
                        appliedInterfaces,
                        appliedRoutes).ConfigureAwait(false);
                }

                appliedInterfaces.Add(change);
            }

            string? mismatch = await FindMismatchAsync(appliedInterfaces, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                return await FailRestoreWithRollbackAsync(mismatch, appliedInterfaces, appliedRoutes).ConfigureAwait(false);
            }

            foreach (AdapterRouteChange change in routePlan.Changes)
            {
                if (!await TrySetRouteMetricAsync(change, cancellationToken).ConfigureAwait(false))
                {
                    return await FailRestoreWithRollbackAsync(
                        $"恢复「{change.AdapterName}」默认路由（网关 {change.NextHop}）跃点为 {change.TargetMetric} 失败",
                        appliedInterfaces,
                        appliedRoutes).ConfigureAwait(false);
                }

                appliedRoutes.Add(change);
            }

            string? routeMismatch = await FindRouteMismatchAsync(appliedRoutes, cancellationToken).ConfigureAwait(false);
            if (routeMismatch is not null)
            {
                return await FailRestoreWithRollbackAsync(routeMismatch, appliedInterfaces, appliedRoutes).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(appliedInterfaces, appliedRoutes).ConfigureAwait(false);
            throw;
        }

        // 只有全部恢复并回读通过才删除记录：失败时保留，用户重试或手动处理都还有依据。
        _store.Delete();
        string routeText = routePlan.Changes.Length == 0
            ? string.Empty
            : $"、{routePlan.Changes.Length} 条默认路由跃点";
        string done =
            $"已恢复 {plan.Changes.Length} 张网卡的原始 IPv4 跃点{routeText}" +
            $"（首选网卡「{snapshot.PreferredName}」），系统恢复自动选路";
        _log.Info(done, LogCategory);
        await TryFlushDestinationCacheAsync(cancellationToken).ConfigureAwait(false);
        return AdapterPriorityOutcome.Done(done, plan.Changes);
    }

    /// <summary>
    /// 解析 <c>netsh interface ipv4|ipv6 show interfaces</c> 的表格。
    /// <para>
    /// 输出是「本地化表头 + 全虚线分隔行 + 数据行」，中文系统形如：
    /// <code>
    /// Idx Met MTU 状态 名称
    /// --- ---------- ---------- ------------ ---------------------------
    ///   9          35        1500  connected     WLAN
    ///  60          25        1500  connected     以太网 2
    /// </code>
    /// 表头行以字母开头、分隔行以 '-' 开头，都不会被「行首数字」规则匹配到；
    /// 名称列可能含空格和括号，所以只按前四列定位，其余整段都是名称。
    /// Idx 也要解析出来：默认路由只带接口索引，必须靠它映射到接口名称。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<NetshInterfaceRow> ParseInterfaceTable(string output)
    {
        var rows = new List<NetshInterfaceRow>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return rows;
        }

        foreach (string line in output.Split('\n'))
        {
            Match match = InterfaceRowRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int metric))
            {
                continue;
            }

            string state = match.Groups[4].Value;
            string name = match.Groups[5].Value.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            rows.Add(new NetshInterfaceRow(name, metric, IsConnectedState(state), index));
        }

        return rows;
    }

    /// <summary>
    /// 解析 <c>netsh interface ipv4 show route</c> 的表格，只返回 IPv4 默认路由（0.0.0.0/0）。
    /// <para>
    /// 列布局固定为 Publish Type Met Prefix Idx Gateway/Interface Name，但 **Publish 与 Type
    /// 两列是本地化文本**：英文系统是 <c>No Manual</c>，中文系统是 <c>否 手动</c>（真机就是中文系统，
    /// 早期版本按英文 No/Yes/Age 匹配，导致中文系统一条路由都解析不出来，功能直接失败）。
    /// 因此这里不依赖 Publish/Type 的取值，改为「按空白切词 + 定位前缀列 0.0.0.0/0」，
    /// 前缀前一个词是 Met、后两个词是 Idx 与网关，列语义与语言无关。
    /// </para>
    /// <para>
    /// 网关不是 IPv4 地址的行（On-link，或网关列写接口名）无法用
    /// <c>netsh set route</c> 的「接口 + 下一跳」定位，直接跳过；其它前缀都是内部路由，不碰。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<NetshRouteRow> ParseRouteTable(string output)
    {
        var rows = new List<NetshRouteRow>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return rows;
        }

        foreach (string line in output.Split('\n'))
        {
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int prefixIndex = Array.FindIndex(
                tokens,
                token => string.Equals(token, DefaultRoutePrefix, StringComparison.Ordinal));

            // 前缀列前面必须有 Met，后面必须有 Idx 与网关；表头/虚线行里没有 0.0.0.0/0，天然被过滤。
            if (prefixIndex < 1 || prefixIndex + 2 >= tokens.Length)
            {
                continue;
            }

            // 只关心默认路由；其它前缀改了会破坏本机网络（这里已由 prefixIndex 定位保证）。
            if (!int.TryParse(tokens[prefixIndex - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int metric))
            {
                continue;
            }

            if (!int.TryParse(tokens[prefixIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            // 网关列只取第一个词；形如「On-link」「在链路上」不是 IPv4 地址，无法用 set route 定位。
            string gateway = tokens[prefixIndex + 2];
            if (!IPAddress.TryParse(gateway, out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            rows.Add(new NetshRouteRow(index, gateway, metric));
        }

        return rows;
    }

    /// <summary>
    /// 解析出的一行接口信息。IPv4 表给出 IPv4 跃点，IPv6 表给出 IPv6 跃点；
    /// 两张表共用同一套列布局，因此共用这个解析器。
    /// <paramref name="Index"/> 是 Idx 列，用来把默认路由的接口索引映射成名称。
    /// </summary>
    internal readonly record struct NetshInterfaceRow(string Name, int Metric, bool Connected, int Index);

    /// <summary>解析出的一行 IPv4 默认路由：接口索引 + 下一跳网关 + 路由自身跃点。</summary>
    internal readonly record struct NetshRouteRow(int Index, string Gateway, int Metric);

    /// <summary>
    /// 数据行：Idx、Met、MTU 三列数字 + 状态（单个词）+ 名称（可含空格）。
    /// 用「行首三列必须是数字」这一硬约束天然过滤掉表头与虚线分隔行。
    /// </summary>
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\S+)\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceRowRegex();

    /// <summary>
    /// 状态列判定：英文 connected / 中文 已连接 视为已连接，其余（disconnected、已断开…）视为断开。
    /// 断开的网卡不参与改跃点，所以只有「明确连上」才返回 true，宁可少改也不误改。
    /// </summary>
    /// <summary>
    /// 是否回环接口。Windows 上显示为 <c>Loopback Pseudo-Interface 1</c>，中文系统也可能出现「回环」字样；
    /// 本地化名称不保证统一，因此英文关键字与中文关键字都按「包含」匹配。
    /// </summary>
    private static bool IsLoopbackAdapter(string name)
        => !string.IsNullOrEmpty(name)
            && (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Pseudo-Interface", StringComparison.OrdinalIgnoreCase)
                || name.Contains("回环", StringComparison.Ordinal));

    private static bool IsConnectedState(string state)
        => state.Equals("connected", StringComparison.OrdinalIgnoreCase)
            || state.Equals("已连接", StringComparison.Ordinal);

    /// <summary>执行一条 <c>netsh interface ipv4 set interface &lt;name&gt; metric=&lt;n&gt;</c>。</summary>
    private async Task<bool> TrySetMetricAsync(string adapterName, int metric, CancellationToken cancellationToken)
    {
        ProcessOutcome outcome = await _runAsync(
            NetshFileName,
            new[] { "interface", "ipv4", "set", "interface", adapterName, $"metric={metric}" },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _log.Warn(
                $"netsh 设置「{adapterName}」跃点为 {metric} 失败：{FirstLine(outcome.StdErr)}",
                LogCategory);
        }

        return outcome.Succeeded;
    }

    /// <summary>
    /// 执行一条改默认路由跃点的命令：
    /// <c>netsh interface ipv4 set route 0.0.0.0/0 &lt;接口名&gt; &lt;下一跳&gt; metric=&lt;n&gt; store=active</c>。
    /// <para>
    /// 接口名、下一跳各自作为一个 argv，含空格/括号的名字由 .NET 负责加引号。
    /// <c>store=active</c> 只改当前运行配置、不写注册表持久化，重启即由 DHCP 重新生成，便于彻底还原。
    /// </para>
    /// </summary>
    internal async Task<bool> TrySetRouteMetricAsync(AdapterRouteChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return await TrySetRouteMetricAsync(
            change.AdapterName,
            change.NextHop,
            change.TargetMetric,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrySetRouteMetricAsync(
        string adapterName,
        string nextHop,
        int metric,
        CancellationToken cancellationToken)
    {
        ProcessOutcome outcome = await _runAsync(
            NetshFileName,
            new[]
            {
                "interface",
                "ipv4",
                "set",
                "route",
                DefaultRoutePrefix,
                adapterName,
                nextHop,
                $"metric={metric}",
                "store=active",
            },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _log.Warn(
                $"netsh 设置「{adapterName}」默认路由（网关 {nextHop}）跃点为 {metric} 失败：{FirstLine(outcome.StdErr)}",
                LogCategory);
        }

        return outcome.Succeeded;
    }

    /// <summary>
    /// 回读校验：每张计划改过的网卡都必须真的等于目标值。
    /// 只校验「改过的」网卡，不校验无关网卡，避免把别人的改动算到自己头上。
    /// </summary>
    /// <returns>第一处不一致的说明；全部一致返回 null。</returns>
    private async Task<string?> FindMismatchAsync(
        IReadOnlyList<AdapterMetricChange> applied,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdapterMetricState> states = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (states.Count == 0)
        {
            return "设置后无法回读网卡跃点，无法确认是否生效";
        }

        foreach (AdapterMetricChange change in applied)
        {
            AdapterMetricState? actual = states.FirstOrDefault(
                state => string.Equals(state.Name, change.Name, StringComparison.OrdinalIgnoreCase));

            if (actual is null)
            {
                return $"回读时找不到网卡「{change.Name}」，无法确认是否生效";
            }

            if (actual.Ipv4Metric != change.Ipv4Target)
            {
                return $"「{change.Name}」跃点未生效：期望 {change.Ipv4Target}，实际 {actual.Ipv4Metric}";
            }
        }

        return null;
    }

    /// <summary>
    /// 默认路由的回读校验：按「接口名 + 下一跳」重新读取默认路由，每条改过的路由都必须等于目标值。
    /// 只靠 netsh 退出码不够——组策略/驱动可能接受命令却不生效（假成功），必须回读确认。
    /// </summary>
    /// <returns>第一处不一致的说明；全部一致返回 null。</returns>
    private async Task<string?> FindRouteMismatchAsync(
        IReadOnlyList<AdapterRouteChange> applied,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdapterRouteState> routes = await ReadRoutesAsync(cancellationToken).ConfigureAwait(false);
        if (routes.Count == 0)
        {
            return "设置后无法回读默认路由，无法确认是否生效";
        }

        foreach (AdapterRouteChange change in applied)
        {
            AdapterRouteState? actual = routes.FirstOrDefault(route =>
                string.Equals(route.AdapterName, change.AdapterName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(route.NextHop, change.NextHop, StringComparison.OrdinalIgnoreCase));

            if (actual is null)
            {
                return $"回读时找不到「{change.AdapterName}」的默认路由（网关 {change.NextHop}），无法确认是否生效";
            }

            if (actual.Metric != change.TargetMetric)
            {
                return $"「{change.AdapterName}」默认路由跃点未生效：期望 {change.TargetMetric}，实际 {actual.Metric}";
            }
        }

        return null;
    }

    /// <summary>
    /// 回滚已执行的修改：先倒序回滚默认路由、再倒序回滚接口跃点（后改的先回，像撤销栈；
    /// 接口与路由是两套独立设置，路由是最后改的，所以先撤路由）。
    /// 回滚用 <see cref="CancellationToken.None"/>：即使外部已取消也必须尽力改回去。
    /// </summary>
    /// <returns>全部回滚命令都成功时为 true。</returns>
    private async Task<bool> RollbackAsync(
        IReadOnlyList<AdapterMetricChange> appliedInterfaces,
        IReadOnlyList<AdapterRouteChange> appliedRoutes)
    {
        bool allSucceeded = true;

        for (int i = appliedRoutes.Count - 1; i >= 0; i--)
        {
            AdapterRouteChange change = appliedRoutes[i];
            if (!await TrySetRouteMetricAsync(change.AdapterName, change.NextHop, change.OriginalMetric, CancellationToken.None)
                .ConfigureAwait(false))
            {
                allSucceeded = false;
            }
        }

        for (int i = appliedInterfaces.Count - 1; i >= 0; i--)
        {
            AdapterMetricChange change = appliedInterfaces[i];
            if (!await TrySetMetricAsync(change.Name, change.Ipv4Original, CancellationToken.None).ConfigureAwait(false))
            {
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    /// <summary>应用失败后的收尾：尽力回滚接口与路由；回滚完整才删除恢复记录，否则保留供用户重试。</summary>
    private async Task<AdapterPriorityOutcome> FailApplyWithRollbackAsync(
        string reason,
        IReadOnlyList<AdapterMetricChange> appliedInterfaces,
        IReadOnlyList<AdapterRouteChange> appliedRoutes)
    {
        bool rolledBack = await RollbackAsync(appliedInterfaces, appliedRoutes).ConfigureAwait(false);
        if (rolledBack)
        {
            _store.Delete();
        }

        string message = rolledBack
            ? $"设置系统首选网卡失败：{reason}；已回滚到修改前的跃点"
            : $"设置系统首选网卡失败：{reason}；部分网卡或默认路由回滚失败，已保留恢复记录，可用「恢复系统自动跃点」重试";
        _log.Error(message, LogCategory);
        return AdapterPriorityOutcome.Failure(message);
    }

    /// <summary>恢复失败后的收尾：回滚本次已恢复的接口与路由，但<b>保留</b>快照供重试。</summary>
    private async Task<AdapterPriorityOutcome> FailRestoreWithRollbackAsync(
        string reason,
        IReadOnlyList<AdapterMetricChange> appliedInterfaces,
        IReadOnlyList<AdapterRouteChange> appliedRoutes)
    {
        bool rolledBack = await RollbackAsync(appliedInterfaces, appliedRoutes).ConfigureAwait(false);
        string message = rolledBack
            ? $"恢复系统自动跃点失败：{reason}；已回滚本次已恢复的网卡与默认路由，恢复记录保留，可重试"
            : $"恢复系统自动跃点失败：{reason}；部分网卡或默认路由回滚失败，恢复记录保留，请重试";
        _log.Error(message, LogCategory);
        return AdapterPriorityOutcome.Failure(message);
    }

    /// <summary>记录失败并返回失败结论（应用前就失败的路径，不需要回滚）。</summary>
    private AdapterPriorityOutcome FailApply(string message)
    {
        _log.Error(message, LogCategory);
        return AdapterPriorityOutcome.Failure(message);
    }

    /// <summary>
    /// 保存恢复记录。写盘失败就不动系统——宁可不改，也不能出现「改了却没有恢复依据」。
    /// </summary>
    private bool TrySaveSnapshot(AdapterPrioritySnapshot snapshot, out string? error)
    {
        try
        {
            _store.Save(snapshot);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 清空系统目标缓存，让新连接立刻按刚改好的跃点选路。
    /// <para>
    /// 这一步是「尽力而为」：失败只记警告，不影响「跃点已改好」的结论——
    /// 缓存条目会随时间过期，最坏情况只是新连接晚一点才走新网卡。
    /// 已经建立的连接不会迁移，需要重启对应软件才会换路径。
    /// </para>
    /// </summary>
    private async Task TryFlushDestinationCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProcessOutcome outcome = await _runAsync(NetshFileName, FlushDestinationCacheArguments, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                _log.Info("已清空系统目标缓存，新连接会立即按新跃点选路（已建立的连接不受影响，需重启对应软件）", LogCategory);
            }
            else
            {
                _log.Warn($"清空系统目标缓存失败（不影响跃点设置）：{FirstLine(outcome.StdErr)}", LogCategory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"清空系统目标缓存失败（不影响跃点设置）：{ex.Message}", LogCategory);
        }
    }

    /// <summary>取错误输出的第一行做日志，避免把多行堆栈塞进一条日志。</summary>
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

    /// <summary>生产默认执行器：真实调用 netsh，超时 10 秒，输出按控制台 OEM 代码页解码。</summary>
    private static Task<ProcessOutcome> DefaultRunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(fileName, arguments, NetshTimeoutMs, cancellationToken);
}
