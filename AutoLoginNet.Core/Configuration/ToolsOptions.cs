using AutoLoginNet.Hosts;

namespace AutoLoginNet.Configuration;

/// <summary>工具页的外链配置。</summary>
public sealed class ToolsOptions
{
    /// <summary>校园内网测速页地址（JXUST 校内测速站点）。</summary>
    public string CampusSpeedTestUrl { get; set; } = "http://speedtest.jxust.edu.cn";

    /// <summary>外网测速页地址。</summary>
    public string InternetSpeedTestUrl { get; set; } = "http://wangsuceshi.bmcx.com";

    /// <summary>网络诊断页预置的探测目标。</summary>
    public List<string> PingTargets { get; set; } = new()
    {
        "10.17.8.18",
        "baidu.com",
        "223.5.5.5",
        "www.jxust.edu.cn",
    };

    /// <summary>是否启用 hosts 域名解析注入功能。</summary>
    public bool EnableHostsFeature { get; set; } = true;

    /// <summary>
    /// 注入到 hosts 的校园网域名映射。
    /// 保留这些静态解析可以避免「改了公共 DNS 之后校内站点解析不到」的问题。
    /// </summary>
    public List<HostsEntry> HostsEntries { get; set; } = CreateDefaultHostsEntries();

    /// <summary>内置的 JXUST 域名映射表。</summary>
    public static List<HostsEntry> CreateDefaultHostsEntries() => new()
    {
        new HostsEntry("219.229.232.50", "jxust.edu.cn", "官网"),
        new HostsEntry("2001:250:6c0b:6010::10:50", "jxust.edu.cn"),
        new HostsEntry("10.16.10.70", "proxy.jxust.edu.cn"),
        new HostsEntry("10.16.10.4", "info.jxust.edu.cn", "资源列表"),
        new HostsEntry("10.16.10.70", "ehall.jxust.edu.cn", "办事大厅"),
        new HostsEntry("10.16.10.70", "authserver.jxust.edu.cn", "系统认证"),
        new HostsEntry("10.16.17.37", "jw.jxust.edu.cn", "教务系统"),
        new HostsEntry("172.20.2.59", "ecard.jxust.edu.cn", "校园卡"),
        new HostsEntry("10.16.30.100", "uam.jxust.edu.cn"),
        new HostsEntry("10.16.30.100", "dcs.jxust.edu.cn", "数据门户"),
        new HostsEntry("10.16.30.100", "tsa.jxust.edu.cn", "数据中心"),
        new HostsEntry("10.16.30.100", "dfm.jxust.edu.cn", "智能填报系统"),
        new HostsEntry("10.16.16.171", "yjsgl.jxust.edu.cn", "研究生系统"),
        new HostsEntry("10.16.16.130", "dxyqyy.jxust.edu.cn", "仪器预约"),
        new HostsEntry("10.16.2.100", "speedtest.jxust.edu.cn", "校内测速"),
        new HostsEntry("10.16.2.101", "speedtest-sj.jxust.edu.cn", "校内测速"),
    };
}
