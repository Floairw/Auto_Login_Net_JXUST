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
/// 「应用优先网卡」前的原始跃点快照，持久化到数据目录，供「恢复系统自动跃点」一键回滚。
/// 记录全部网卡（含断开状态的），这样恢复时即使某张网卡掉线过也能回到原值。
/// </summary>
/// <param name="PreferredName">当时选定的首选网卡名称。</param>
/// <param name="Adapters">快照时刻各网卡的跃点。</param>
/// <param name="AppliedAt">应用时间。</param>
public sealed record AdapterPrioritySnapshot(string PreferredName, AdapterMetricState[] Adapters, DateTimeOffset AppliedAt)
{
    /// <summary>快照为空（没有记录任何网卡）。</summary>
    public bool IsEmpty => Adapters.Length == 0;
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
}
