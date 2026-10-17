using AutoLoginNet.Diagnostics;
using AutoLoginNet.Windows;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoLoginNet.Network;

/// <summary>
/// 基于 <c>netsh</c> 的接口跃点（metric）读写实现。
/// <para>
/// 与 DNS 服务同样的两个关键取舍：
/// 1) 网卡只用 <b>名称</b> 定位：<c>netsh interface ipv4 set interface</c> 接受位置参数形式的
///    接口名（含空格、括号），名称单独作为一个 argv 传入由 .NET 负责加引号，不手工拼命令行；
/// 2) 写入后立即回读并逐张比对，只有真正生效才报告成功——跃点没改却报成功会让用户
///    误以为系统已经切成那张网卡上网，属于比失败更危险的「假成功」。
/// </para>
/// <para>
/// 只改 IPv4 接口跃点（与 Core 的 <see cref="AdapterPriorityPlanner"/> 保持一致）：
/// 校园网认证、DNS 都走 IPv4，动 IPv6 收益不明却可能影响双栈。
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

    /// <summary>netsh 可执行文件名（Windows 自带，位于 System32，无需完整路径）。</summary>
    private const string NetshFileName = "netsh.exe";

    /// <summary>单条 netsh 命令超时：改跃点很快，10 秒足够，超时即视为失败以便回滚。</summary>
    private const int NetshTimeoutMs = 10000;

    /// <summary>日志分类，与界面「网络」页一致。</summary>
    private const string LogCategory = "网络";

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

    /// <inheritdoc />
    public async Task<AdapterPriorityOutcome> ApplyPreferredAsync(
        string preferredName,
        CancellationToken cancellationToken = default)
    {
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

        // 计划只看「可调整」的网卡，但快照仍记全部网卡：恢复时按记录比对，回环值没变就不会被写
        AdapterMetricState[] editable = states.Where(state => !IsLoopbackAdapter(state.Name)).ToArray();
        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateApplyPlan(editable, preferredName);
        if (plan.HasError)
        {
            return FailApply(plan.Error!);
        }

        if (!plan.CanApply)
        {
            // 值已经正确就不写系统：既避免无谓写入，也避免把「本来就对」误报成一次修改。
            string message = $"「{plan.PreferredName}」的 IPv4 跃点已是目标值 {AdapterPriorityPlanner.PreferredMetric}，无需修改";
            _log.Info(message, LogCategory);
            return AdapterPriorityOutcome.Done(message);
        }

        // 先落盘恢复记录，再动系统：万一进程在改到一半时崩溃/被杀，用户仍能一键恢复。
        if (!TrySaveSnapshot(new AdapterPrioritySnapshot(plan.PreferredName, states.ToArray(), DateTimeOffset.Now), out string? saveError))
        {
            return FailApply($"写入恢复记录失败，已取消修改：{saveError}");
        }

        var applied = new List<AdapterMetricChange>(plan.Changes.Length);
        try
        {
            foreach (AdapterMetricChange change in plan.Changes)
            {
                if (!await TrySetMetricAsync(change.Name, change.Ipv4Target, cancellationToken).ConfigureAwait(false))
                {
                    return await FailApplyWithRollbackAsync(
                        $"设置「{change.Name}」跃点为 {change.Ipv4Target} 失败",
                        applied).ConfigureAwait(false);
                }

                applied.Add(change);
            }

            string? mismatch = await FindMismatchAsync(applied, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                return await FailApplyWithRollbackAsync(mismatch, applied).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消也先把已改的网卡改回去，别把系统留在「一半网卡改过」的状态；
            // 回滚本身不能再被取消，否则可能永久改坏用户网络。
            await RollbackAsync(applied).ConfigureAwait(false);
            throw;
        }

        string done =
            $"已将「{plan.PreferredName}」设为系统首选网卡：该网卡 IPv4 跃点 {AdapterPriorityPlanner.PreferredMetric}，" +
            $"其余已连接网卡跃点 {AdapterPriorityPlanner.SecondaryMetric}，共调整 {plan.Changes.Length} 张网卡（可随时恢复）";
        _log.Info(done, LogCategory);
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

        AdapterPriorityPlan plan = AdapterPriorityPlanner.CreateRestorePlan(states, snapshot);
        if (plan.HasError)
        {
            _log.Error(plan.Error!, LogCategory);
            return AdapterPriorityOutcome.Failure(plan.Error!);
        }

        if (plan.Changes.Length == 0)
        {
            // 记录里的值已经都对上了，说明用户或系统已经手动恢复过；清掉记录即可，不再写系统。
            _store.Delete();
            string message = "各网卡跃点已与恢复记录一致，无需修改（已清除恢复记录）";
            _log.Info(message, LogCategory);
            return AdapterPriorityOutcome.Done(message);
        }

        var applied = new List<AdapterMetricChange>(plan.Changes.Length);
        try
        {
            foreach (AdapterMetricChange change in plan.Changes)
            {
                if (!await TrySetMetricAsync(change.Name, change.Ipv4Target, cancellationToken).ConfigureAwait(false))
                {
                    return await FailRestoreWithRollbackAsync(
                        $"恢复「{change.Name}」跃点为 {change.Ipv4Target} 失败",
                        applied).ConfigureAwait(false);
                }

                applied.Add(change);
            }

            string? mismatch = await FindMismatchAsync(applied, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                return await FailRestoreWithRollbackAsync(mismatch, applied).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(applied).ConfigureAwait(false);
            throw;
        }

        // 只有全部恢复并回读通过才删除记录：失败时保留，用户重试或手动处理都还有依据。
        _store.Delete();
        string done =
            $"已恢复 {plan.Changes.Length} 张网卡的原始 IPv4 跃点（首选网卡「{snapshot.PreferredName}」），系统恢复自动选路";
        _log.Info(done, LogCategory);
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

            rows.Add(new NetshInterfaceRow(name, metric, IsConnectedState(state)));
        }

        return rows;
    }

    /// <summary>
    /// 解析出的一行接口信息。IPv4 表给出 IPv4 跃点，IPv6 表给出 IPv6 跃点；
    /// 两张表共用同一套列布局，因此共用这个解析器。
    /// </summary>
    internal readonly record struct NetshInterfaceRow(string Name, int Metric, bool Connected);

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
        => name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pseudo-Interface", StringComparison.OrdinalIgnoreCase)
            || name.Contains("回环", StringComparison.Ordinal);

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
    /// 回滚已执行的修改：倒序写回原值（后改的先回，像撤销栈）。
    /// 回滚用 <see cref="CancellationToken.None"/>：即使外部已取消也必须尽力改回去。
    /// </summary>
    /// <returns>全部回滚命令都成功时为 true。</returns>
    private async Task<bool> RollbackAsync(IReadOnlyList<AdapterMetricChange> applied)
    {
        bool allSucceeded = true;
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            AdapterMetricChange change = applied[i];
            if (!await TrySetMetricAsync(change.Name, change.Ipv4Original, CancellationToken.None).ConfigureAwait(false))
            {
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    /// <summary>应用失败后的收尾：尽力回滚；回滚完整才删除恢复记录，否则保留供用户重试。</summary>
    private async Task<AdapterPriorityOutcome> FailApplyWithRollbackAsync(
        string reason,
        IReadOnlyList<AdapterMetricChange> applied)
    {
        bool rolledBack = await RollbackAsync(applied).ConfigureAwait(false);
        if (rolledBack)
        {
            _store.Delete();
        }

        string message = rolledBack
            ? $"设置系统首选网卡失败：{reason}；已回滚到修改前的跃点"
            : $"设置系统首选网卡失败：{reason}；部分网卡回滚失败，已保留恢复记录，可用「恢复系统自动跃点」重试";
        _log.Error(message, LogCategory);
        return AdapterPriorityOutcome.Failure(message);
    }

    /// <summary>恢复失败后的收尾：回滚本次已恢复的网卡，但<b>保留</b>快照供重试。</summary>
    private async Task<AdapterPriorityOutcome> FailRestoreWithRollbackAsync(
        string reason,
        IReadOnlyList<AdapterMetricChange> applied)
    {
        bool rolledBack = await RollbackAsync(applied).ConfigureAwait(false);
        string message = rolledBack
            ? $"恢复系统自动跃点失败：{reason}；已回滚本次已恢复的网卡，恢复记录保留，可重试"
            : $"恢复系统自动跃点失败：{reason}；部分网卡回滚失败，恢复记录保留，请重试";
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
