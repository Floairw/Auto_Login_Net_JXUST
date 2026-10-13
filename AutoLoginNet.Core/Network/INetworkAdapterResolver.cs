namespace AutoLoginNet.Network;

/// <summary>
/// 把「为某个目标地址选网卡」从 DNS 服务中分离出来。
/// 独立抽象的动机：选错网卡会直接切断用户网络，因此该决策必须可被替换、可被测试。
/// </summary>
public interface INetworkAdapterResolver
{
    /// <summary>解析到达 <paramref name="target"/> 所使用的网卡；target 为 null 时使用默认路由。</summary>
    Task<AdapterInfo?> ResolveAsync(string? target = null, CancellationToken cancellationToken = default);
}
