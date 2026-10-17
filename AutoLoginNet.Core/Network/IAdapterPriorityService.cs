namespace AutoLoginNet.Network;

/// <summary>
/// 「把某张网卡设为系统首选」的读写接口。
/// <para>
/// 与 DNS 服务同样的设计原则：写操作必须显式指定网卡，且应用前要回读确认、
/// 失败要尽量回滚——改跃点会直接影响系统选路，假成功比失败更危险。
/// </para>
/// </summary>
public interface IAdapterPriorityService
{
    /// <summary>
    /// 读取全部网卡当前的 IPv4/IPv6 接口跃点。
    /// 读取失败（例如 netsh 不可用）时返回空列表并记录日志，不抛异常。
    /// </summary>
    Task<IReadOnlyList<AdapterMetricState>> ReadStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把 <paramref name="preferredName"/> 设为系统首选网卡：
    /// 该网卡跃点改为 <see cref="AdapterPriorityPlanner.PreferredMetric"/>，
    /// 其余已连接网卡改为 <see cref="AdapterPriorityPlanner.SecondaryMetric"/>，
    /// 并把修改前的原值写入恢复快照。
    /// </summary>
    Task<AdapterPriorityOutcome> ApplyPreferredAsync(string preferredName, CancellationToken cancellationToken = default);

    /// <summary>按最近一次快照把各网卡的跃点恢复成原值，成功后删除快照。</summary>
    Task<AdapterPriorityOutcome> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>是否存在可恢复的快照（界面「恢复」按钮的可用性）。</summary>
    bool HasSnapshot { get; }
}
