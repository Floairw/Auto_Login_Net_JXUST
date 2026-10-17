using AutoLoginNet.Network;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>
/// 「当前上网网卡」的选择与解析，网络工具页与托盘右键菜单共用。
/// <para>
/// 只保存网卡<b>名称</b>（不保存索引）：网卡重新插拔或驱动重装后索引会变，名称通常不变。
/// 指定的网卡不存在或已断开时自动回退到系统路由选择，避免用户换网卡后功能直接失效。
/// </para>
/// </summary>
public sealed class AdapterSwitcher
{
    private readonly AppServices _services;

    public AdapterSwitcher(AppServices services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>手动指定的网卡名称；空字符串表示自动（按路由表选择）。</summary>
    public string PreferredName => _services.UiSettings.Current.NetworkAdapterName ?? string.Empty;

    /// <summary>当前可选的候选网卡（已连接、有 IPv4 地址）。</summary>
    public IReadOnlyList<AdapterInfo> ListAdapters() => _services.AdapterResolver.ListAdapters();

    /// <summary>保存选择；<paramref name="name"/> 为 null 或空表示改回自动选择。</summary>
    public void Select(string? name)
    {
        string normalized = (name ?? string.Empty).Trim();
        if (string.Equals(normalized, PreferredName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _services.UiSettings.Current.NetworkAdapterName = normalized;
        _services.UiSettings.Save();
        _services.Log.Info(
            normalized.Length == 0 ? "上网网卡改为自动选择" : $"上网网卡改为「{normalized}」",
            "网络");
    }

    /// <summary>解析当前上网网卡：优先用指定网卡，找不到时回退到路由表。</summary>
    public async Task<AdapterInfo?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        string preferred = PreferredName;
        if (preferred.Length > 0)
        {
            foreach (AdapterInfo adapter in ListAdapters())
            {
                if (string.Equals(adapter.Name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return adapter;
                }
            }

            _services.Log.Info($"指定的上网网卡「{preferred}」不存在或未连接，回退为自动选择", "网络");
        }

        return await _services.AdapterResolver
            .ResolveAsync(_services.Options.Auth.Host, cancellationToken)
            .ConfigureAwait(false);
    }
}
