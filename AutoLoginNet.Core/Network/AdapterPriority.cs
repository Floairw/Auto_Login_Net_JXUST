namespace AutoLoginNet.Network;

/// <summary>
/// 一张网卡的接口跃点（metric）快照。
/// <para>
/// 跃点是 Windows 选路的权重：数值越小越优先。把选定网卡调小、把其余网卡调大，
/// 系统就会真正从这张网卡上网——这是「切换上网网卡」在系统层面的落地方式，
/// 比禁用其它网卡温和：不禁用、不重启网卡，随时可恢复。
/// </para>
/// </summary>
/// <param name="Name">接口名称（同时也是 netsh 设置跃点时使用的接口名）。</param>
/// <param name="Ipv4Metric">IPv4 接口跃点。</param>
/// <param name="Ipv6Metric">IPv6 接口跃点（本功能只改 IPv4，此值仅用于快照与展示）。</param>
/// <param name="Connected">当前是否已连接（断开时改跃点没有意义）。</param>
public sealed record AdapterMetricState(string Name, int Ipv4Metric, int Ipv6Metric, bool Connected);

/// <summary>计划中的单条跃点修改。原值保留下来，应用失败时用于回滚、成功时写入恢复快照。</summary>
/// <param name="Name">接口名称。</param>
/// <param name="Ipv4Original">修改前的 IPv4 跃点。</param>
/// <param name="Ipv4Target">修改后的 IPv4 跃点。</param>
public sealed record AdapterMetricChange(string Name, int Ipv4Original, int Ipv4Target)
{
    /// <summary>目标值与当前值相同，无需执行命令。</summary>
    public bool IsNoOp => Ipv4Original == Ipv4Target;

    /// <summary>界面展示文本。</summary>
    public string Text => $"{Name}：{Ipv4Original} → {Ipv4Target}";
}

/// <summary>
/// 一张网卡的 IPv4 默认路由（0.0.0.0/0）状态。
/// <para>
/// 只改接口跃点在真实机器上可能不生效：Windows 里默认路由可以带自己的显式跃点
/// （实测「WLAN」默认路由跃点 0、以太网 2 为 25），此时路由选择先比路由跃点，
/// 接口跃点被改小也不一定抢得过。因此「切换上网网卡」要同时改默认路由跃点。
/// </para>
/// </summary>
/// <param name="AdapterName">该默认路由所属接口名称。</param>
/// <param name="NextHop">下一跳网关地址。</param>
/// <param name="Metric">默认路由自身的跃点（netsh show route 的 Met 列）。</param>
public sealed record AdapterRouteState(string AdapterName, string NextHop, int Metric);

/// <summary>计划中的单条默认路由跃点修改；原值用于回滚与恢复快照。</summary>
/// <param name="AdapterName">接口名称。</param>
/// <param name="NextHop">下一跳网关地址（定位并修改这条路由要用它）。</param>
/// <param name="OriginalMetric">修改前的路由跃点。</param>
/// <param name="TargetMetric">修改后的路由跃点。</param>
public sealed record AdapterRouteChange(string AdapterName, string NextHop, int OriginalMetric, int TargetMetric)
{
    /// <summary>目标值与当前值相同，无需执行命令。</summary>
    public bool IsNoOp => OriginalMetric == TargetMetric;

    /// <summary>界面展示文本。</summary>
    public string Text => $"{AdapterName} 默认路由：{OriginalMetric} → {TargetMetric}";
}

/// <summary>
/// 「应用优先网卡」前的原始跃点快照，持久化到数据目录，供「恢复系统自动跃点」一键回滚。
/// 记录全部网卡（含断开状态的），这样恢复时即使某张网卡掉线过也能回到原值。
/// </summary>
/// <param name="PreferredName">当时选定的首选网卡名称。</param>
/// <param name="Adapters">快照时刻各网卡的跃点。</param>
/// <param name="AppliedAt">应用时间。</param>
/// <param name="Routes">
/// 快照时刻各网卡 IPv4 默认路由（0.0.0.0/0）的跃点。
/// 只改接口跃点并不足以切换上网网卡：已有默认路由带显式跃点（实测 WLAN 路由跃点 0、以太网 25），
/// 路由选择优先看路由自身的跃点，所以还要把默认路由跃点一起改并记录原值。
/// 旧版本记录里没有这一项（为 null），恢复时跳过路由部分即可。
/// </param>
public sealed record AdapterPrioritySnapshot(
    string PreferredName,
    AdapterMetricState[] Adapters,
    DateTimeOffset AppliedAt,
    AdapterRouteState[]? Routes = null)
{
    /// <summary>快照为空（没有记录任何网卡）。</summary>
    public bool IsEmpty => Adapters.Length == 0;

    /// <summary>默认路由记录（旧版本快照没有该字段，统一按空数组处理）。</summary>
    public AdapterRouteState[] RoutesOrEmpty => Routes ?? Array.Empty<AdapterRouteState>();
}

/// <summary>一次优先网卡操作（应用或恢复）的结论。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Message">结果说明（失败时包含原因）。</param>
/// <param name="Changes">实际执行或被计划执行的修改。</param>
public sealed record AdapterPriorityOutcome(bool Success, string Message, AdapterMetricChange[] Changes)
{
    /// <summary>构造失败结论。</summary>
    public static AdapterPriorityOutcome Failure(string message)
        => new(false, message, Array.Empty<AdapterMetricChange>());

    /// <summary>构造成功结论。</summary>
    public static AdapterPriorityOutcome Done(string message, params AdapterMetricChange[] changes)
        => new(true, message, changes);
}

/// <summary>
/// 跃点修改计划。纯数据、无副作用，便于把「改哪张网卡、改成多少」单独测试，
/// 而不必真的去动系统网络设置。
/// </summary>
/// <param name="PreferredName">首选网卡名称。</param>
/// <param name="Changes">需要执行的修改（只含真正变化的网卡）。</param>
/// <param name="Error">计划不可执行的原因；为 null 表示可执行。</param>
public readonly record struct AdapterPriorityPlan(
    string PreferredName,
    AdapterMetricChange[] Changes,
    string? Error)
{
    /// <summary>计划是否可执行且确实有改动。</summary>
    public bool CanApply => Error is null && Changes.Length > 0;

    /// <summary>计划是否带有错误原因。</summary>
    public bool HasError => Error is not null;
}

/// <summary>
/// 默认路由跃点修改计划。与 <see cref="AdapterPriorityPlan"/> 分开：接口跃点与
/// 默认路由跃点是两套独立设置，任一失败都要能单独回滚。
/// </summary>
/// <param name="Changes">需要执行的修改（只含真正变化的路由）。</param>
/// <param name="Error">计划不可执行的原因；为 null 表示可执行。</param>
public readonly record struct AdapterRoutePlan(AdapterRouteChange[] Changes, string? Error)
{
    /// <summary>计划是否可执行且确实有改动。</summary>
    public bool CanApply => Error is null && Changes.Length > 0;

    /// <summary>计划是否带有错误原因。</summary>
    public bool HasError => Error is not null;
}

/// <summary>
/// 「切换系统优先网卡」的纯逻辑：把选定网卡调成最小跃点、其余已连接网卡调大；
/// 以及从快照恢复原跃点。
/// <para>
/// 只改 IPv4 接口跃点：校园网认证、DNS 与绝大多数校园服务都走 IPv4，
/// 动 IPv6 跃点收益不明却可能影响双栈网络，因此刻意保持不动。
/// </para>
/// </summary>
public static class AdapterPriorityPlanner
{
    /// <summary>首选网卡的跃点。</summary>
    public const int PreferredMetric = 10;

    /// <summary>其余已连接网卡的跃点：比常见默认值（25/35/65/5000）都大，确保首选网卡胜出。</summary>
    public const int SecondaryMetric = 9000;

    /// <summary>
    /// 首选网卡默认路由的跃点。取 0（Windows 允许的最小路由跃点）：
    /// 默认路由跃点越低越优先，而其它网卡会被抬到 <see cref="SecondaryRouteMetric"/>，
    /// 因此不会再出现并列；即使系统按「路由跃点 + 接口跃点」比较，也仍然最小。
    /// </summary>
    public const int PreferredRouteMetric = 0;

    /// <summary>其余网卡默认路由的跃点：足够大，确保首选网卡默认路由胜出。</summary>
    public const int SecondaryRouteMetric = 9000;

    /// <summary>提权子命令与界面共用的动作标识：应用。</summary>
    public const string ApplyAction = "apply";

    /// <summary>提权子命令与界面共用的动作标识：恢复。</summary>
    public const string RestoreAction = "restore";

    /// <summary>
    /// 生成「把 <paramref name="preferredName"/> 设为首选网卡」的计划：
    /// 首选网卡到 <see cref="PreferredMetric"/>，其余<b>已连接</b>网卡到 <see cref="SecondaryMetric"/>；
    /// 已断开网卡不动。已经等于目标值的网卡不产生命令，避免无谓的系统写入。
    /// </summary>
    public static AdapterPriorityPlan CreateApplyPlan(IReadOnlyList<AdapterMetricState> states, string preferredName)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (string.IsNullOrWhiteSpace(preferredName))
        {
            return new AdapterPriorityPlan(string.Empty, Array.Empty<AdapterMetricChange>(), "没有指定上网网卡");
        }

        AdapterMetricState? preferred = states.FirstOrDefault(
            state => string.Equals(state.Name, preferredName, StringComparison.OrdinalIgnoreCase));

        if (preferred is null)
        {
            return new AdapterPriorityPlan(preferredName, Array.Empty<AdapterMetricChange>(), $"系统里找不到网卡「{preferredName}」");
        }

        if (!preferred.Connected)
        {
            return new AdapterPriorityPlan(preferredName, Array.Empty<AdapterMetricChange>(), $"网卡「{preferred.Name}」当前未连接，无法设为系统首选");
        }

        var changes = new List<AdapterMetricChange>();
        foreach (AdapterMetricState state in states)
        {
            bool isPreferred = string.Equals(state.Name, preferredName, StringComparison.OrdinalIgnoreCase);
            if (!isPreferred && !state.Connected)
            {
                continue;
            }

            int target = isPreferred ? PreferredMetric : SecondaryMetric;
            if (state.Ipv4Metric != target)
            {
                changes.Add(new AdapterMetricChange(state.Name, state.Ipv4Metric, target));
            }
        }

        return new AdapterPriorityPlan(preferred.Name, changes.ToArray(), null);
    }

    /// <summary>
    /// 生成「恢复快照里的原跃点」的计划。
    /// 快照里的网卡若已不存在则跳过；只把当前值与记录值不同的网卡写回原值。
    /// </summary>
    public static AdapterPriorityPlan CreateRestorePlan(IReadOnlyList<AdapterMetricState> states, AdapterPrioritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsEmpty)
        {
            return new AdapterPriorityPlan(snapshot.PreferredName, Array.Empty<AdapterMetricChange>(), "恢复记录为空，无法恢复");
        }

        var changes = new List<AdapterMetricChange>();
        int matched = 0;

        foreach (AdapterMetricState recorded in snapshot.Adapters)
        {
            AdapterMetricState? current = states.FirstOrDefault(
                state => string.Equals(state.Name, recorded.Name, StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                continue;
            }

            matched++;
            if (current.Ipv4Metric != recorded.Ipv4Metric)
            {
                changes.Add(new AdapterMetricChange(current.Name, current.Ipv4Metric, recorded.Ipv4Metric));
            }
        }

        if (matched == 0)
        {
            return new AdapterPriorityPlan(
                snapshot.PreferredName,
                Array.Empty<AdapterMetricChange>(),
                "记录里的网卡都已不存在，无法恢复（可手动在「网络连接」里重置跃点）");
        }

        return new AdapterPriorityPlan(snapshot.PreferredName, changes.ToArray(), null);
    }

    /// <summary>
    /// 生成「默认路由跃点」修改计划：首选网卡的路由调到 <see cref="PreferredRouteMetric"/>，
    /// 其余网卡的路由调到 <see cref="SecondaryRouteMetric"/>。
    /// <para>
    /// 这里<b>不判断网卡是否已连接</b>，只按传入的路由列表计算：是否连上由平台层决定。
    /// 实际实现只传入「当前已连接」网卡的路由——断开接口上执行 <c>set route</c> 不可靠，
    /// 而断开网卡不写也不会抢路（选路按「路由跃点 + 接口跃点」，它仍明显更大）。
    /// </para>
    /// <para>
    /// 首选网卡没有默认路由时直接报错：没有网关就无法上网，此时报「已设为首选」属于假成功。
    /// </para>
    /// </summary>
    public static AdapterRoutePlan CreateRouteApplyPlan(IReadOnlyList<AdapterRouteState> routes, string preferredName)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (string.IsNullOrWhiteSpace(preferredName))
        {
            return new AdapterRoutePlan(Array.Empty<AdapterRouteChange>(), "没有指定上网网卡");
        }

        if (routes.Count == 0)
        {
            return new AdapterRoutePlan(Array.Empty<AdapterRouteChange>(), "读取不到系统默认路由，无法设置系统首选网卡");
        }

        if (!routes.Any(route => NameEquals(route.AdapterName, preferredName)))
        {
            return new AdapterRoutePlan(
                Array.Empty<AdapterRouteChange>(),
                $"网卡「{preferredName}」没有默认路由（没有可用网关），系统无法从它上网");
        }

        var changes = new List<AdapterRouteChange>(routes.Count);
        foreach (AdapterRouteState route in routes)
        {
            int target = NameEquals(route.AdapterName, preferredName)
                ? PreferredRouteMetric
                : SecondaryRouteMetric;

            if (route.Metric != target)
            {
                changes.Add(new AdapterRouteChange(route.AdapterName, route.NextHop, route.Metric, target));
            }
        }

        return new AdapterRoutePlan(changes.ToArray(), null);
    }

    /// <summary>
    /// 生成「恢复快照里的默认路由跃点」的计划。按「网卡名称 + 下一跳」匹配当前路由：
    /// 快照后网卡换网段、路由消失的情况都跳过，不强行按名称写回一条不存在的路由。
    /// 旧版本快照没有路由记录时返回空计划（只恢复接口跃点），不报错。
    /// </summary>
    public static AdapterRoutePlan CreateRouteRestorePlan(
        IReadOnlyList<AdapterRouteState> routes,
        AdapterPrioritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(snapshot);

        AdapterRouteState[] recorded = snapshot.RoutesOrEmpty;
        if (recorded.Length == 0)
        {
            return new AdapterRoutePlan(Array.Empty<AdapterRouteChange>(), null);
        }

        var changes = new List<AdapterRouteChange>(recorded.Length);
        foreach (AdapterRouteState original in recorded)
        {
            AdapterRouteState? current = routes.FirstOrDefault(route =>
                NameEquals(route.AdapterName, original.AdapterName)
                && string.Equals(route.NextHop, original.NextHop, StringComparison.OrdinalIgnoreCase));

            if (current is null || current.Metric == original.Metric)
            {
                continue;
            }

            changes.Add(new AdapterRouteChange(current.AdapterName, current.NextHop, current.Metric, original.Metric));
        }

        return new AdapterRoutePlan(changes.ToArray(), null);
    }

    /// <summary>
    /// 生成要落盘的恢复快照。
    /// <para>
    /// 关键点：如果已经有旧快照（说明用户之前设过、还没恢复），必须<b>保留旧快照里的原值</b>，
    /// 不能用「当前值」当原值——否则重复点「设为系统首选网卡」会把已经改过的值记成原始值，
    /// 之后「恢复」就回不到真正的出厂状态（真机验证时就踩到这个坑）。
    /// </para>
    /// <para>
    /// 旧快照里存在、但当前系统里已经消失的网卡/路由会被丢弃（无法恢复）；
    /// 新出现的网卡/路由按当前值记录。
    /// </para>
    /// </summary>
    public static AdapterPrioritySnapshot CreateSnapshot(
        string preferredName,
        IReadOnlyList<AdapterMetricState> states,
        IReadOnlyList<AdapterRouteState> routes,
        AdapterPrioritySnapshot? previous,
        DateTimeOffset appliedAt)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(routes);

        var adapters = new AdapterMetricState[states.Count];
        for (int i = 0; i < states.Count; i++)
        {
            AdapterMetricState state = states[i];
            AdapterMetricState? before = previous?.Adapters.FirstOrDefault(
                recorded => NameEquals(recorded.Name, state.Name));

            adapters[i] = before is null
                ? state
                : new AdapterMetricState(state.Name, before.Ipv4Metric, before.Ipv6Metric, state.Connected);
        }

        var routeStates = new AdapterRouteState[routes.Count];
        for (int i = 0; i < routes.Count; i++)
        {
            AdapterRouteState route = routes[i];
            AdapterRouteState? before = previous?.RoutesOrEmpty.FirstOrDefault(recorded =>
                NameEquals(recorded.AdapterName, route.AdapterName)
                && string.Equals(recorded.NextHop, route.NextHop, StringComparison.OrdinalIgnoreCase));

            routeStates[i] = before is null ? route : new AdapterRouteState(route.AdapterName, route.NextHop, before.Metric);
        }

        return new AdapterPrioritySnapshot(preferredName, adapters, appliedAt, routeStates);
    }

    private static bool NameEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
