using AutoLoginNet.Hosts;
using AutoLoginNet.Network;
using AutoLoginNet.Services;
using AutoLoginNet.Windows;

namespace AutoLoginNet.App;

/// <summary>
/// 提权子命令。
/// <para>
/// 需要管理员权限的两件事——注册计划任务、修改网卡 DNS——都通过「以管理员身份重新启动自身
/// 并带子命令」完成。子命令进程只做事不发消息，退出码即结果，避免在主进程与提权进程之间
/// 做进程间通信。
/// </para>
/// </summary>
public static class ElevatedCommands
{
    public const string RegisterAutoStart = "--register-autostart";
    public const string ApplyDns = "--apply-dns";
    public const string DnsTarget = "--dns-target";

    /// <summary>界面上选定的上网网卡名称；缺省时按默认路由自动解析。</summary>
    public const string AdapterNameOption = "--adapter-name";
    public const string HostsAction = "--hosts";

    /// <summary>「设为系统首选网卡 / 恢复系统自动跃点」子命令；值为 apply 或 restore。</summary>
    public const string AdapterPriorityAction = "--adapter-priority";

    /// <summary>
    /// 覆盖 hosts 文件路径。仅供测试与排障使用：
    /// 有了它，整条命令链路可以在不触碰系统 hosts、也不需要 UAC 的情况下端到端验证。
    /// </summary>
    public const string HostsFilePath = "--hosts-file";

    /// <summary>子命令执行成功时的退出码。</summary>
    public const int SuccessExitCode = 0;

    /// <summary>子命令执行失败时的退出码。</summary>
    public const int FailureExitCode = 1;

    /// <summary>尝试解析并执行提权子命令；返回 null 表示本次启动不是提权子命令。</summary>
    public static int? TryExecute(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        if (args.Contains(RegisterAutoStart, StringComparer.OrdinalIgnoreCase))
        {
            return RunRegisterAutoStartAsync(args).GetAwaiter().GetResult();
        }

        if (args.Contains(ApplyDns, StringComparer.OrdinalIgnoreCase))
        {
            return RunApplyDnsAsync(args).GetAwaiter().GetResult();
        }

        if (args.Contains(HostsAction, StringComparer.OrdinalIgnoreCase))
        {
            return RunHostsAsync(args).GetAwaiter().GetResult();
        }

        if (args.Contains(AdapterPriorityAction, StringComparer.OrdinalIgnoreCase))
        {
            return RunAdapterPriorityAsync(args).GetAwaiter().GetResult();
        }

        return null;
    }

    private static async Task<int> RunHostsAsync(string[] args)
    {
        string action = StartupRouter.ReadOptionValue(args, HostsAction) ?? "apply";
        string? resultPath = StartupRouter.ReadOptionValue(args, HostsResultFile.ResultPathArgument);
        string? overridePath = StartupRouter.ReadOptionValue(args, HostsFilePath);

        var services = new AppServices();
        IHostsFileService hosts = string.IsNullOrWhiteSpace(overridePath)
            ? services.HostsFile
            : new WindowsHostsFileService(services.Options, services.Log, overridePath);

        HostsApplyResult result = string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase)
            ? await hosts.RestoreAsync().ConfigureAwait(false)
            : await hosts.ApplyAsync().ConfigureAwait(false);

        WriteHostsResult(resultPath, result, action);
        return result.Success ? SuccessExitCode : FailureExitCode;
    }

    private static void WriteHostsResult(string? path, HostsApplyResult result, string action)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            HostsOperationResult.WriteTo(
                path,
                new HostsOperationResult(
                    result.Success,
                    action,
                    result.Message,
                    result.BackupPath,
                    DateTimeOffset.Now));
        }
        catch
        {
            // 结果回传失败不影响实际写入结果
        }
    }

    /// <summary>
    /// 应用或恢复系统首选网卡（只改 IPv4 接口跃点）。
    /// <para>
    /// 与 DNS 子命令同一模式：读参数 → 调服务 → 把结论写回结果文件。
    /// 结果必须落盘，否则界面只能显示「已请求授权」，用户无从判断是否真的改成功。
    /// </para>
    /// </summary>
    private static async Task<int> RunAdapterPriorityAsync(string[] args)
    {
        string action = StartupRouter.ReadOptionValue(args, AdapterPriorityAction)
            ?? AdapterPriorityPlanner.ApplyAction;
        string? resultPath = StartupRouter.ReadOptionValue(args, AdapterPriorityResultFile.ResultPathArgument);
        string? adapterName = StartupRouter.ReadOptionValue(args, AdapterNameOption);

        var services = new AppServices();
        IAdapterPriorityService priority = services.AdapterPriority;

        bool restore = string.Equals(action, AdapterPriorityPlanner.RestoreAction, StringComparison.OrdinalIgnoreCase);

        AdapterPriorityOutcome outcome;
        if (restore)
        {
            outcome = await priority.RestoreAsync().ConfigureAwait(false);
        }
        else if (string.IsNullOrWhiteSpace(adapterName))
        {
            // 没有目标网卡就绝不碰系统跃点：宁可失败并说明原因
            const string reason = "没有指定上网网卡";
            services.Log.Error(reason, "网络");
            outcome = AdapterPriorityOutcome.Failure(reason);
        }
        else
        {
            outcome = await priority.ApplyPreferredAsync(adapterName).ConfigureAwait(false);
        }

        WriteAdapterPriorityResult(resultPath, outcome, restore, adapterName);

        if (outcome.Success)
        {
            services.Log.Info($"系统首选网卡（{(restore ? "恢复" : "应用")}）：{outcome.Message}", "网络");
        }
        else
        {
            services.Log.Error($"系统首选网卡（{(restore ? "恢复" : "应用")}）失败：{outcome.Message}", "网络");
        }

        return outcome.Success ? SuccessExitCode : FailureExitCode;
    }

    /// <summary>把跃点操作结论写入结果文件；失败只影响界面反馈，不影响真实结果与退出码。</summary>
    private static void WriteAdapterPriorityResult(
        string? path,
        AdapterPriorityOutcome outcome,
        bool restore,
        string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            AdapterPriorityOperationResult.WriteTo(
                path,
                new AdapterPriorityOperationResult(
                    outcome.Success,
                    restore ? AdapterPriorityPlanner.RestoreAction : AdapterPriorityPlanner.ApplyAction,
                    adapterName ?? string.Empty,
                    outcome.Message,
                    DateTimeOffset.Now));
        }
        catch
        {
            // 结果回传失败不影响实际设置结果
        }
    }

    private static async Task<int> RunRegisterAutoStartAsync(string[] args)
    {
        bool enable = StartupRouter.ReadOptionValue(args, RegisterAutoStart) is not "0";
        var services = new AppServices();

        var command = new AutoStartCommand(
            Infrastructure.ShellHelper.ExecutablePath,
            StartupRouter.AutoStartArgument);

        bool ok = await services.AutoStart.SetEnabledAsync(enable, command).ConfigureAwait(false);
        return ok ? SuccessExitCode : FailureExitCode;
    }

    private static async Task<int> RunApplyDnsAsync(string[] args)
    {
        string action = StartupRouter.ReadOptionValue(args, ApplyDns) ?? string.Empty;
        string target = StartupRouter.ReadOptionValue(args, DnsTarget) ?? "1.1.1.1";
        string primary = StartupRouter.ReadOptionValue(args, "--primary") ?? string.Empty;
        string backup = StartupRouter.ReadOptionValue(args, "--backup") ?? string.Empty;
        string? resultPath = StartupRouter.ReadOptionValue(args, DnsResultFile.ResultPathArgument);
        string? adapterName = StartupRouter.ReadOptionValue(args, AdapterNameOption);

        var services = new AppServices();
        var resolver = new WindowsNetworkAdapterResolver(services.Log);

        // 优先按界面传来的网卡名称定位：用户手动指定的网卡未必是默认路由那张
        AdapterInfo? adapter = await ResolveAdapterAsync(resolver, adapterName, target).ConfigureAwait(false);

        string adapterDisplay = adapter?.Display ?? "(未知网卡)";

        if (adapter is null)
        {
            // 指定了网卡却找不到时不再改别的网卡：换卡改 DNS 比不改更糟
            string reason = string.IsNullOrWhiteSpace(adapterName)
                ? "无法确定目标网卡，DNS 未修改"
                : $"指定的上网网卡「{adapterName}」不存在或未连接，DNS 未修改（不会改用其他网卡）";

            services.Log.Error(reason, "DNS");
            WriteResult(resultPath, false, action, adapterDisplay, reason);
            return FailureExitCode;
        }

        bool ok;
        string message;

        if (string.Equals(action, "automatic", StringComparison.OrdinalIgnoreCase))
        {
            ok = await services.DnsService.ResetToAutomaticAsync(adapter.Value).ConfigureAwait(false);
            message = ok ? "已改为自动获取(DHCP)" : "改为自动获取失败，请查看日志了解详情";
        }
        else
        {
            var dns = new DnsPair(primary, backup, IsStatic: true);
            ok = await services.DnsService.ApplyAsync(adapter.Value, dns).ConfigureAwait(false);
            message = ok ? $"已应用 {dns.AddressText}" : "应用失败（设置未生效或权限不足），请查看日志";
        }

        // 子进程没有界面，把结果写回约定文件供主进程读取
        WriteResult(resultPath, ok, action, adapterDisplay, message);
        return ok ? SuccessExitCode : FailureExitCode;
    }

    /// <summary>
    /// 定位要改 DNS 的网卡：传了名称就<b>只按名称</b>找，找不到返回 <c>null</c>（由调用方报错），
    /// 只有「自动选择」（没传名称）才回退默认路由解析。
    /// <para>
    /// 不能按名找不到就换一张卡：界面显示的是 A 网卡，改到 B 网卡上属于静默改错目标。
    /// </para>
    /// </summary>
    private static async Task<AdapterInfo?> ResolveAdapterAsync(
        WindowsNetworkAdapterResolver resolver,
        string? adapterName,
        string target)
    {
        if (!string.IsNullOrWhiteSpace(adapterName))
        {
            return resolver.ListAdapters()
                .FirstOrDefault(a => string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase));
        }

        return await resolver.ResolveAsync(target).ConfigureAwait(false);
    }

    private static void WriteResult(string? path, bool success, string action, string adapterDisplay, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            DnsOperationResult.WriteTo(
                path,
                new DnsOperationResult(success, action, adapterDisplay, message, DateTimeOffset.Now));
        }
        catch
        {
            // 结果回传失败不影响实际设置结果
        }
    }
}
