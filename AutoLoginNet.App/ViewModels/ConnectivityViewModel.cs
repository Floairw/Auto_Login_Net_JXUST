using AutoLoginNet.Network;
using AutoLoginNet.Services;

namespace AutoLoginNet.App.ViewModels;

/// <summary>
/// 全局网络连接状态：外壳侧栏与首页共享同一个实例，
/// 保证两处展示的状态永远一致（旧实现里两处各自探测，状态经常互相矛盾）。
/// </summary>
public sealed class ConnectivityViewModel : Mvvm.ObservableObject
{
    private readonly AppServices _services;
    private NetworkSnapshot? _snapshot;
    private bool _isBusy;

    public ConnectivityViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>状态文本变化通知（供外壳订阅）。</summary>
    public event EventHandler<string>? StatusChanged;

    public NetworkSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                OnPropertiesChanged(
                    nameof(HasResult),
                    nameof(Conclusion),
                    nameof(CampusSummary),
                    nameof(ExternalSummary),
                    nameof(RequirementText),
                    nameof(IsOnline));
                StatusChanged?.Invoke(this, ShortStatus);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasResult => _snapshot is not null;

    public bool IsOnline => _snapshot?.IsOnline ?? false;

    public string Conclusion => _snapshot?.Conclusion ?? "尚未检测网络环境";

    public string CampusSummary => _snapshot is null ? "—" : _snapshot.Campus.Summary;

    public string ExternalSummary => _snapshot is null ? "—" : _snapshot.External.Summary;

    public string RequirementText => _snapshot?.Requirement switch
    {
        AuthRequirement.Required => "需要认证",
        AuthRequirement.NotRequired => "无需认证",
        AuthRequirement.Unknown => "状态未知",
        _ => "未检测",
    };

    /// <summary>侧栏用的极简状态。</summary>
    public string ShortStatus => _snapshot?.Requirement switch
    {
        AuthRequirement.Required => "待认证",
        AuthRequirement.NotRequired when IsOnline => "已联网",
        AuthRequirement.NotRequired => "非校园网",
        AuthRequirement.Unknown => "网络未知",
        _ => "正在检测网络…",
    };

    /// <summary>执行一次检测，并广播忙碌状态。</summary>
    public async Task<NetworkSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            NetworkSnapshot snapshot = await _services.NetworkDetector.DetectAsync(cancellationToken);
            Snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>登录成功后同步状态，避免立刻重复检测一次。</summary>
    public void MarkOnline()
    {
        if (_snapshot is null)
        {
            return;
        }

        Snapshot = _snapshot with
        {
            Requirement = AuthRequirement.NotRequired,
            CheckedAt = DateTimeOffset.Now,
        };
    }
}
