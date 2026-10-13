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
    public const string HostsAction = "--hosts";

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

        var services = new AppServices();
        var resolver = new WindowsNetworkAdapterResolver(services.Log);
        AdapterInfo? adapter = await resolver.ResolveAsync(target).ConfigureAwait(false);

        string adapterDisplay = adapter?.Display ?? "(未知网卡)";

        if (adapter is null)
        {
            const string reason = "无法确定目标网卡，DNS 未修改";
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
