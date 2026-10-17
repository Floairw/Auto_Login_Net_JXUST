using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Mvvm;
using AutoLoginNet.Hosts;
using AutoLoginNet.Network;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;

namespace AutoLoginNet.App.ViewModels;

/// <summary>上网网卡下拉项。<see cref="Name"/> 为 <c>null</c> 表示「自动选择」。</summary>
public sealed record AdapterChoice(string? Name, string Label);

/// <summary>网络工具页视图模型：DNS 优选、网速测试与网络诊断。</summary>
public sealed class NetworkToolsViewModel : ObservableObject
{
    /// <summary>等待用户在 UAC 窗口中确认时的提示。</summary>
    private const string ElevationPendingText = "已请求管理员授权，请在 UAC 窗口中确认…";

    private readonly AppServices _services;

    /// <summary>上网网卡偏好：托盘菜单与这里共用同一份设置。</summary>
    private readonly AdapterSwitcher _adapterSwitcher;

    private AdapterInfo? _adapter;
    private string _adapterText = "正在解析活动网卡…";
    private string _currentDnsText = "—";
    private string _dnsStatus = "点击「测试全部」，将自动选出延迟最低的首选与备选 DNS。";
    private string _adapterPriorityStatus = "尚未设置系统首选网卡：系统按默认跃点自动选路。";
    private DnsPair? _recommended;
    private string _pingTarget;
    private string _pingResult = "尚未测试";
    private bool _isMeasuring;
    private HostsState _hostsState = HostsState.Unknown;
    private int _hostsAlreadyMapped;
    private string _hostsStatus = "正在读取 hosts 状态…";

    public NetworkToolsViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _adapterSwitcher = new AdapterSwitcher(services);

        foreach (DnsProvider provider in services.DnsService.Providers)
        {
            DnsCandidates.Add(new DnsCandidateEntry(provider));
        }

        _pingTarget = services.Options.Tools.PingTargets.FirstOrDefault() ?? "baidu.com";

        MeasureCommand = new AsyncRelayCommand(MeasureAsync, () => !_adapter.HasValue || !IsMeasuring);
        ApplyCommand = new AsyncRelayCommand(ApplyRecommendedAsync, () => _recommended.HasValue);
        AutomaticCommand = new AsyncRelayCommand(SetAutomaticAsync, () => _adapter.HasValue);
        PingCommand = new AsyncRelayCommand(PingAsync, () => !string.IsNullOrWhiteSpace(_pingTarget));
    }

    public ObservableCollection<DnsCandidateEntry> DnsCandidates { get; } = new();

    /// <summary>可选的上网网卡（首项固定为「自动选择」）。</summary>
    public ObservableCollection<AdapterChoice> AdapterOptions { get; } = new();

    /// <summary>当前偏好的网卡名称，空字符串表示自动选择。</summary>
    public string PreferredAdapterName => _adapterSwitcher.PreferredName;

    public IReadOnlyList<string> PingTargets => _services.Options.Tools.PingTargets;

    public ICommand MeasureCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand AutomaticCommand { get; }

    public ICommand PingCommand { get; }

    public string AdapterText
    {
        get => _adapterText;
        private set => SetProperty(ref _adapterText, value);
    }

    public string CurrentDnsText
    {
        get => _currentDnsText;
        private set => SetProperty(ref _currentDnsText, value);
    }

    public string DnsStatus
    {
        get => _dnsStatus;
        private set => SetProperty(ref _dnsStatus, value);
    }

    /// <summary>系统首选网卡操作的状态说明（与 DNS 的状态分开，避免两块功能互相覆盖提示）。</summary>
    public string AdapterPriorityStatus
    {
        get => _adapterPriorityStatus;
        private set => SetProperty(ref _adapterPriorityStatus, value);
    }

    /// <summary>
    /// 是否存在可恢复的跃点快照（「恢复」按钮可用性）。
    /// <para>
    /// 刻意直接在 getter 里读服务：快照是磁盘文件，界面缓存可能与实际状态不一致，
    /// 每次读取文件系统再判断最便宜也最不会骗人。
    /// </para>
    /// </summary>
    public bool CanRestoreAdapterPriority => _services.AdapterPriority.HasSnapshot;

    public bool IsMeasuring
    {
        get => _isMeasuring;
        private set
        {
            if (SetProperty(ref _isMeasuring, value))
            {
                OnPropertiesChanged(nameof(CanMeasure), nameof(CanApply));
                (MeasureCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>是否可以开始 DNS 延迟测试（直接绑定到按钮）。</summary>
    public bool CanMeasure => !_isMeasuring;

    /// <summary>是否已选出可应用的推荐 DNS。</summary>
    public bool CanApply => _recommended.HasValue && !_isMeasuring;

    /// <summary>是否可以执行连通性测试。</summary>
    public bool CanPing => !string.IsNullOrWhiteSpace(_pingTarget) && !_isMeasuring;

    public string PingTarget
    {
        get => _pingTarget;
        set
        {
            if (SetProperty(ref _pingTarget, value))
            {
                OnPropertyChanged(nameof(CanPing));
                (PingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string PingResult
    {
        get => _pingResult;
        private set => SetProperty(ref _pingResult, value);
    }

    /// <summary>初始化：枚举上网网卡、按偏好解析当前网卡并读取其 DNS。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            RefreshAdapterOptions();
            await RefreshAdapterAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Error($"初始化网络工具页失败：{ex.Message}", "网络工具");
            AdapterText = "网卡解析失败";
        }
        finally
        {
            await RefreshHostsAsync();
        }
    }

    /// <summary>重新枚举网卡并刷新下拉项；偏好网卡不在线时也保留它，避免界面莫名跳回「自动」。</summary>
    public void RefreshAdapterOptions()
    {
        AdapterOptions.Clear();
        AdapterOptions.Add(new AdapterChoice(null, "自动选择（按默认路由）"));

        IReadOnlyList<AdapterInfo> adapters = _adapterSwitcher.ListAdapters();
        foreach (AdapterInfo adapter in adapters)
        {
            AdapterOptions.Add(new AdapterChoice(adapter.Name, adapter.Display));
        }

        string preferred = _adapterSwitcher.PreferredName;
        bool preferredPresent = adapters.Any(a => string.Equals(a.Name, preferred, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferred) && !preferredPresent)
        {
            AdapterOptions.Add(new AdapterChoice(preferred, $"{preferred}（未连接）"));
        }

        OnPropertyChanged(nameof(PreferredAdapterName));
    }

    /// <summary>切换上网网卡：保存偏好后立刻按新网卡重读 DNS。</summary>
    public async Task SelectAdapterAsync(string? name)
    {
        _adapterSwitcher.Select(name);
        await RefreshAdapterAsync();
    }

    /// <summary>按当前偏好解析网卡并刷新「当前网卡 / 当前 DNS」。</summary>
    private async Task RefreshAdapterAsync()
    {
        _adapter = await _adapterSwitcher.ResolveAsync();
        if (_adapter is null)
        {
            AdapterText = "未找到正在使用的网卡";
            CurrentDnsText = "—";
            return;
        }

        AdapterText = _adapter.Value.Display;
        await RefreshCurrentDnsAsync();

        (AutomaticCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MeasureCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>测试全部候选 DNS 的解析延迟，并给出推荐组合。</summary>
    public async Task MeasureAsync()
    {
        IsMeasuring = true;
        DnsStatus = $"正在测试 {DnsCandidates.Count * 2} 个 DNS 的解析延迟…";

        try
        {
            foreach (DnsCandidateEntry candidate in DnsCandidates)
            {
                candidate.Reset();
            }

            var tasks = new List<Task>();
            foreach (DnsCandidateEntry candidate in DnsCandidates)
            {
                tasks.Add(MeasureSlotAsync(candidate.Primary));
                tasks.Add(MeasureSlotAsync(candidate.Backup));
            }

            await Task.WhenAll(tasks);

            DnsPair? recommended = BuildRecommendation();
            _recommended = recommended;

            DnsStatus = BuildMeasureSummary(recommended);

            OnPropertyChanged(nameof(CanApply));
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            DnsStatus = $"测试失败：{ex.Message}";
            _services.Log.Error($"DNS 延迟测试失败：{ex.Message}", "DNS");
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    /// <summary>汇总本轮测试结果：可达数量、最快/最慢，以及推荐组合。</summary>
    private string BuildMeasureSummary(DnsPair? recommended)
    {
        List<DnsSlot> all = DnsCandidates
            .SelectMany(c => new[] { c.Primary, c.Backup })
            .ToList();

        List<DnsSlot> reachable = all.Where(s => s.Reachable).OrderBy(s => s.LatencyMs).ToList();

        if (reachable.Count == 0)
        {
            return $"已完成 {all.Count} 个 DNS 的测试，全部无应答。请检查网络连接后重试。";
        }

        string fastest = $"{reachable[0].Address}（{reachable[0].LatencyMs} ms）";
        string summary = reachable.Count == all.Count
            ? $"已完成 {all.Count} 个 DNS 测试，全部可达"
            : $"已完成 {all.Count} 个 DNS 测试，{reachable.Count} 个可达、{all.Count - reachable.Count} 个无应答";

        summary += $"；最快 {fastest}";

        if (reachable.Count > 1)
        {
            summary += $"，最慢 {reachable[^1].Address}（{reachable[^1].LatencyMs} ms）";
        }

        if (recommended is { } pair)
        {
            summary += $"。推荐首选 {pair.Primary}";
            if (!string.IsNullOrEmpty(pair.Backup))
            {
                summary += $"，备选 {pair.Backup}";
            }
        }

        return summary + "。";
    }

    /// <summary>把推荐的 DNS 应用到网卡；非管理员时自动请求提权并等待结果。</summary>
    public async Task ApplyRecommendedAsync()
    {
        if (_recommended is not { } dns || !_adapter.HasValue)
        {
            DnsStatus = "请先完成 DNS 延迟测试。";
            return;
        }

        if (ShellHelper.IsElevated())
        {
            bool ok = await _services.DnsService.ApplyAsync(_adapter.Value, dns);
            DnsStatus = ok ? $"已应用：{dns.AddressText}" : "应用失败，请查看日志了解详情。";
            await RefreshCurrentDnsAsync();
            return;
        }

        await RunElevatedDnsAsync(
            new[] { "--primary", dns.Primary, "--backup", dns.Backup },
            ElevationPendingText);
    }

    /// <summary>把 DNS 改回自动获取。</summary>
    public async Task SetAutomaticAsync()
    {
        if (!_adapter.HasValue)
        {
            return;
        }

        if (ShellHelper.IsElevated())
        {
            bool ok = await _services.DnsService.ResetToAutomaticAsync(_adapter.Value);
            DnsStatus = ok ? "已将 DNS 改为自动获取(DHCP)。" : "改为自动获取失败，请查看日志。";
            await RefreshCurrentDnsAsync();
            return;
        }

        await RunElevatedDnsAsync(Array.Empty<string>(), ElevationPendingText);
    }

    /// <summary>
    /// 通过提权子进程执行 DNS 设置。
    /// <para>
    /// 子进程没有界面，因此约定一个结果文件：主进程在等待期内轮询该文件，
    /// 拿到结果后先删除再回读网卡状态，从而给出真实反馈——
    /// 旧实现只显示「已请求授权」，用户无从判断是否真的改成功。
    /// </para>
    /// </summary>
    private async Task RunElevatedDnsAsync(string[] extraArguments, string pendingText)
    {
        string resultPath = DnsResultFile.CreatePath();

        var arguments = new List<string>
        {
            ElevatedCommands.ApplyDns,
            extraArguments.Length == 0 ? "automatic" : "apply",
            ElevatedCommands.DnsTarget, _services.Options.Auth.Host,
            DnsResultFile.ResultPathArgument, resultPath,
        };

        // 带着界面上选中的网卡名字过去：提权子进程按名字解析，
        // 与界面看到的必须是同一张网卡（否则「应用到 A 卡」可能落到 B 卡上）
        if (_adapter is { } adapter && !string.IsNullOrWhiteSpace(adapter.Name))
        {
            arguments.Add(ElevatedCommands.AdapterNameOption);
            arguments.Add(adapter.Name);
        }

        arguments.AddRange(extraArguments);

        DnsStatus = pendingText;

        DnsOperationResult? result = await RunElevatedAsync(
            arguments.ToArray(),
            resultPath,
            DnsOperationResult.ReadFrom);

        DnsStatus = result switch
        {
            null => "提权被取消，或已授权但未收到执行结果；请点击「刷新网卡与 DNS」确认。",
            { Success: true } => $"{result.Message}（{result.AdapterDisplay}）",
            _ => result.Message,
        };

        await RefreshCurrentDnsAsync();
    }

    /// <summary>hosts 写入/恢复：同样走提权子进程 + 结果文件。</summary>
    private async Task RunElevatedHostsAsync(bool apply)
    {
        string resultPath = HostsResultFile.CreatePath();

        var arguments = new[]
        {
            ElevatedCommands.HostsAction,
            apply ? "apply" : "restore",
            HostsResultFile.ResultPathArgument, resultPath,
        };

        HostsStatus = ElevationPendingText;

        HostsOperationResult? result = await RunElevatedAsync(
            arguments,
            resultPath,
            HostsOperationResult.ReadFrom);

        HostsStatus = result switch
        {
            null => "提权被取消，或已授权但未收到执行结果；请点击「刷新状态」确认。",
            _ => result.Message,
        };

        await RefreshHostsAsync();
    }

    /// <summary>
    /// 以管理员身份运行子命令并取回结果。
    /// <para>
    /// 子进程没有界面，因此约定一个结果文件：主进程等它退出后读取并清理。
    /// 这样界面能给出真实结论，而不是停在「已请求授权」。
    /// </para>
    /// </summary>
    private static async Task<TResult?> RunElevatedAsync<TResult>(
        string[] arguments,
        string resultPath,
        Func<string, TResult?> readResult)
        where TResult : class
    {
        Process? process = ShellHelper.RunElevated(arguments);
        if (process is null)
        {
            return null;
        }

        try
        {
            // 正常情况子进程写完结果文件就立即退出
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);
        }
        catch (OperationCanceledException)
        {
            // 超时后仍尝试读一次结果文件
        }

        TResult? result = readResult(resultPath);

        try
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
        catch
        {
            // 清理失败不影响结果展示
        }

        return result;
    }

    // ---------------- 系统首选网卡（IPv4 接口跃点 + 默认路由跃点） ----------------

    /// <summary>
    /// 把当前上网网卡设为系统首选：该网卡 IPv4 接口跃点改为 10、默认路由跃点改为 0，
    /// 其余网卡的接口跃点与其默认路由跃点都改为 9000。
    /// <para>
    /// 只改接口跃点是不够的：这台机器的默认路由带着显式跃点（WLAN 0、以太网 2 25），
    /// 路由选择按路由自身跃点走，所以必须连默认路由一起改，选路才会真的翻到首选网卡。
    /// </para>
    /// <para>
    /// 已提权时直接改；否则走提权子进程 + 结果文件——与 DNS 同样的模式，
    /// 因为改跃点同样需要管理员权限，且界面必须拿到真实结论而不是停在「已请求授权」。
    /// </para>
    /// </summary>
    public async Task SetSystemPreferredAsync()
    {
        // 以界面展示的「当前网卡」为准：偏好网卡可能未连接，解析不出就不能改
        if (!_adapter.HasValue)
        {
            AdapterPriorityStatus = "尚未解析到正在使用的网卡，无法设为系统首选。";
            return;
        }

        AdapterInfo adapter = _adapter.Value;

        if (ShellHelper.IsElevated())
        {
            AdapterPriorityStatus = "正在设置系统首选网卡…";
            AdapterPriorityOutcome outcome = await _services.AdapterPriority.ApplyPreferredAsync(adapter.Name);
            AdapterPriorityStatus = BuildAdapterPriorityText(outcome, adapter.Display);
            LogAdapterPriority("设置系统首选网卡", adapter.Name, outcome);
            OnPropertiesChanged(nameof(CanRestoreAdapterPriority));
            return;
        }

        await RunElevatedAdapterPriorityAsync(AdapterPriorityPlanner.ApplyAction, adapter.Name);
    }

    /// <summary>按应用前快照恢复原跃点；没有快照时说明原因而不是静默返回。</summary>
    public async Task RestoreAdapterPriorityAsync()
    {
        if (!_services.AdapterPriority.HasSnapshot)
        {
            AdapterPriorityStatus = "没有可恢复的跃点记录（接口跃点与默认路由跃点），系统当前按默认跃点选路。";
            OnPropertiesChanged(nameof(CanRestoreAdapterPriority));
            return;
        }

        if (ShellHelper.IsElevated())
        {
            AdapterPriorityStatus = "正在恢复系统自动跃点…";
            AdapterPriorityOutcome outcome = await _services.AdapterPriority.RestoreAsync();
            AdapterPriorityStatus = BuildAdapterPriorityText(outcome, null);
            LogAdapterPriority("恢复系统自动跃点", null, outcome);
            OnPropertiesChanged(nameof(CanRestoreAdapterPriority));
            return;
        }

        await RunElevatedAdapterPriorityAsync(AdapterPriorityPlanner.RestoreAction, null);
    }

    /// <summary>通过提权子进程应用或恢复跃点，并回读约定文件里的真实结果。</summary>
    private async Task RunElevatedAdapterPriorityAsync(string action, string? adapterName)
    {
        string resultPath = AdapterPriorityResultFile.CreatePath();

        var arguments = new List<string>
        {
            ElevatedCommands.AdapterPriorityAction,
            action,
            AdapterPriorityResultFile.ResultPathArgument,
            resultPath,
        };

        // 带上界面选中的网卡名字：子进程按名字定位，与界面看到的必须是同一张网卡
        if (!string.IsNullOrWhiteSpace(adapterName))
        {
            arguments.Add(ElevatedCommands.AdapterNameOption);
            arguments.Add(adapterName);
        }

        AdapterPriorityStatus = ElevationPendingText;

        AdapterPriorityOperationResult? result = await RunElevatedAsync(
            arguments.ToArray(),
            resultPath,
            AdapterPriorityOperationResult.ReadFrom);

        AdapterPriorityStatus = result switch
        {
            null => "提权被取消，或已授权但未收到执行结果；请点击「刷新网卡与 DNS」确认。",
            { Success: true } => BuildAdapterPriorityText(result),
            _ => result.Message,
        };

        _services.Log.Info($"系统首选网卡（{action}）结果：{AdapterPriorityStatus}", "网络");
        OnPropertiesChanged(nameof(CanRestoreAdapterPriority));
    }

    /// <summary>结果文件里的展示名可能为空（恢复时），为空就只显示结论。</summary>
    private static string BuildAdapterPriorityText(AdapterPriorityOperationResult result)
        => string.IsNullOrWhiteSpace(result.AdapterDisplay)
            ? result.Message
            : $"{result.Message}（{result.AdapterDisplay}）";

    /// <summary>成功且知道网卡名时补上展示名，让用户确认改的是哪张网卡。</summary>
    private static string BuildAdapterPriorityText(AdapterPriorityOutcome outcome, string? adapterDisplay)
        => outcome.Success && !string.IsNullOrWhiteSpace(adapterDisplay)
            ? $"{outcome.Message}（{adapterDisplay}）"
            : outcome.Message;

    private void LogAdapterPriority(string action, string? adapterName, AdapterPriorityOutcome outcome)
    {
        string target = string.IsNullOrWhiteSpace(adapterName) ? "全部网卡" : $"「{adapterName}」";
        string message = $"{action}{target}：{outcome.Message}";
        if (outcome.Success)
        {
            _services.Log.Info(message, "网络");
        }
        else
        {
            _services.Log.Error(message, "网络");
        }
    }

    /// <summary>重新读取当前网卡的 DNS 状态并刷新界面。</summary>
    public async Task RefreshCurrentDnsAsync()
    {
        if (!_adapter.HasValue)
        {
            return;
        }

        DnsPair? current = await _services.DnsService.GetCurrentDnsAsync(_adapter.Value);
        CurrentDnsText = current?.ToString() ?? "未获取到 DNS";
    }

    // ---------------- hosts 域名解析 ----------------

    /// <summary>hosts 功能是否可用（配置可关闭）。</summary>
    public bool HostsFeatureEnabled => _services.Options.Tools.EnableHostsFeature;

    /// <summary>内置的域名映射规则，用于界面预览。</summary>
    public IReadOnlyList<HostsEntry> HostsEntries => _services.HostsFile.DefaultEntries;

    /// <summary>hosts 文件路径（界面展示）。</summary>
    public string HostsFilePath => _services.HostsFile.FilePath;

    /// <summary>规则条数说明。</summary>
    public string HostsEntryCountText => $"共 {HostsEntries.Count} 条规则";

    /// <summary>当前是否已写入托管块（用于开关的初始位置）。</summary>
    public bool HostsIsOn => _hostsState == HostsState.Applied;

    /// <summary>状态说明文本。</summary>
    public string HostsStateText => _hostsState switch
    {
        HostsState.Applied => "已注入校内域名解析",
        HostsState.Outdated => "规则内容已变更，需要重新注入",
        HostsState.Unknown => "无法读取 hosts 文件",
        _ => "未注入",
    };

    public string HostsStatus
    {
        get => _hostsStatus;
        private set => SetProperty(ref _hostsStatus, value);
    }

    /// <summary>读取 hosts 当前状态。</summary>
    public async Task RefreshHostsAsync()
    {
        if (!HostsFeatureEnabled)
        {
            return;
        }

        HostsStatusInfo info = await _services.HostsFile.GetStatusInfoAsync();
        _hostsState = info.State;
        _hostsAlreadyMapped = info.AlreadyMappedCount;

        OnPropertiesChanged(
            nameof(HostsIsOn),
            nameof(HostsStateText),
            nameof(HostsAlreadyMappedNotice),
            nameof(ShowHostsAlreadyMappedNotice));

        HostsStatus = BuildHostsStatus(info);
    }

    /// <summary>
    /// 提示：这些域名在 hosts 中已有手工映射。
    /// 用户手写条目位置在托管块之前，Windows 取第一个匹配，因此优先级更高；
    /// 此提示用于避免用户误以为开关没生效。
    /// </summary>
    public string HostsAlreadyMappedNotice
        => $"检测到 hosts 中已有 {_hostsAlreadyMapped} 条同名映射（你自己维护的条目）。它们位于托管块之前，优先级更高，因此本开关开启后不会有可见变化。";

    /// <summary>是否显示「已手工映射」提示。</summary>
    public bool ShowHostsAlreadyMappedNotice
        => _hostsState == HostsState.NotApplied
            && _hostsAlreadyMapped > 0
            && _hostsAlreadyMapped == HostsEntries.Count;

    private string BuildHostsStatus(HostsStatusInfo info) => info.State switch
    {
        HostsState.Applied => $"已注入 {HostsEntries.Count} 条校内域名解析规则。",
        HostsState.Outdated => "托管区块存在但内容与当前规则不一致，建议重新注入。",
        HostsState.Unknown => "无法读取 hosts 文件，请检查权限或文件是否存在。",
        _ when ShowHostsAlreadyMappedNotice =>
            $"hosts 中已存在全部 {info.AlreadyMappedCount} 条同名映射，无需再注入；若这些条目被删除，可再用本开关恢复。",
        _ => "未注入。开启后即使改用公共 DNS，校内站点仍可正常访问。",
    };

    /// <summary>注入或移除 hosts 规则。</summary>
    public async Task SetHostsEnabledAsync(bool enable)
    {
        if (!HostsFeatureEnabled)
        {
            return;
        }

        if (ShellHelper.IsElevated())
        {
            HostsApplyResult result = enable
                ? await _services.HostsFile.ApplyAsync()
                : await _services.HostsFile.RestoreAsync();

            HostsStatus = result.Message;
            await RefreshHostsAsync();
            return;
        }

        await RunElevatedHostsAsync(enable);
    }

    /// <summary>Ping 指定目标。</summary>
    public async Task PingAsync()
    {
        PingResult = $"正在测试 {PingTarget} …";
        try
        {
            ProbeResult result = await _services.PingService.PingAsync(PingTarget);
            PingResult = result.Summary;
        }
        catch (Exception ex)
        {
            PingResult = $"测试失败：{ex.Message}";
        }
    }

    private async Task MeasureSlotAsync(DnsSlot slot)
    {
        try
        {
            // 用真实的 UDP DNS 查询计时：ICMP 会被多数公共 DNS 丢弃，不能作为可用性依据
            ProbeResult result = await _services.DnsLatencyProbe.MeasureAsync(slot.Address, 2000);
            slot.Apply(result);
        }
        catch
        {
            slot.MarkUnreachable();
        }
    }

    /// <summary>在所有可达地址中取延迟最低者为首选、次低者为备选。</summary>
    private DnsPair? BuildRecommendation()
    {
        var reachable = DnsCandidates
            .SelectMany(candidate => new[] { candidate.Primary, candidate.Backup })
            .Where(slot => slot.Reachable)
            .OrderBy(slot => slot.LatencyMs)
            .ToList();

        if (reachable.Count == 0)
        {
            return null;
        }

        reachable[0].MarkAsChosen("首选");

        string backup = string.Empty;
        if (reachable.Count > 1)
        {
            reachable[1].MarkAsChosen("备选");
            backup = reachable[1].Address;
        }

        return new DnsPair(reachable[0].Address, backup);
    }
}
