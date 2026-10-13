namespace AutoLoginNet.Network;

/// <summary>活动网卡的 IPv4 信息。</summary>
/// <param name="Index">接口索引（用于 DnsClient 等按索引寻址的接口）。</param>
/// <param name="Name">网卡名称，同时作为 netsh 的接口参数（位置参数形式）。</param>
/// <param name="IPv4Address">网卡 IPv4 地址。</param>
public readonly record struct AdapterInfo(int Index, string Name, string IPv4Address)
{
    /// <summary>界面展示文本。</summary>
    public string Display => Index > 0 ? $"{Name}（{IPv4Address}）" : Name;

    /// <summary>netsh 的接口索引参数写法，仅在明确支持 idx 的场景使用。</summary>
    public string NetshSelector => $"idx={Index}";
}

/// <summary>DNS 提供商候选。</summary>
/// <param name="Name">展示名。</param>
/// <param name="Primary">首选地址。</param>
/// <param name="Backup">备选地址。</param>
public sealed record DnsProvider(string Name, string Primary, string Backup);

/// <summary>主 DNS 与备用 DNS 的组合。</summary>
/// <param name="Primary">首选 DNS。</param>
/// <param name="Backup">备选 DNS，可能为空。</param>
/// <param name="IsStatic">true 表示手动静态配置，false 表示由 DHCP 自动下发。</param>
public readonly record struct DnsPair(string Primary, string Backup, bool IsStatic = true)
{
    /// <summary>数据来源标签。</summary>
    public string SourceText => IsStatic ? "手动设置" : "自动获取(DHCP)";

    /// <summary>仅地址部分。</summary>
    public string AddressText => string.IsNullOrEmpty(Backup) ? Primary : $"{Primary} / {Backup}";

    public override string ToString() => $"{SourceText}：{AddressText}";
}

/// <summary>
/// DNS 读取与应用。
/// <para>
/// 刻意把所有写操作都设计为需要显式传入 <see cref="AdapterInfo"/>：
/// 「改哪块网卡」是本功能唯一的高风险决策，必须由调用方一次性决定并展示给用户确认，
/// 而不是在服务内部隐式重算（旧实现每次调用都重新解析网卡，存在展示与实际不一致的风险）。
/// </para>
/// </summary>
public interface IDnsService
{
    /// <summary>内置的公共 DNS 候选列表。</summary>
    IReadOnlyList<DnsProvider> Providers { get; }

    /// <summary>
    /// 读取指定网卡当前生效的 DNS。
    /// 静态配置优先；未静态配置时返回 DHCP 下发的地址（<see cref="DnsPair.IsStatic"/> 为 false）。
    /// 完全无法确定时返回 null。
    /// </summary>
    Task<DnsPair?> GetCurrentDnsAsync(AdapterInfo adapter, CancellationToken cancellationToken = default);

    /// <summary>把指定网卡 DNS 设为静态首选/备选。</summary>
    Task<bool> ApplyAsync(AdapterInfo adapter, DnsPair dns, CancellationToken cancellationToken = default);

    /// <summary>把指定网卡 DNS 改回自动获取(DHCP)。</summary>
    Task<bool> ResetToAutomaticAsync(AdapterInfo adapter, CancellationToken cancellationToken = default);
}
