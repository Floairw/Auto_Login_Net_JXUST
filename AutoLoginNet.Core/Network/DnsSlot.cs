using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoLoginNet.Network;

/// <summary>
/// 一个 DNS 提供商的测量结果（首选 + 备选）。
/// <para>
/// 放在 Core 而非界面层：它描述的是「测量结果」这一领域概念，
/// 界面只是恰好用它做数据绑定。这样它也能被单元测试直接覆盖。
/// </para>
/// </summary>
public sealed class DnsCandidateEntry
{
    public DnsCandidateEntry(DnsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        Name = provider.Name;
        Primary = new DnsSlot(provider.Primary);
        Backup = new DnsSlot(provider.Backup);
    }

    /// <summary>提供商展示名。</summary>
    public string Name { get; }

    public DnsSlot Primary { get; }

    public DnsSlot Backup { get; }

    /// <summary>清空上一次的测量与选中状态。</summary>
    public void Reset()
    {
        Primary.Reset();
        Backup.Reset();
    }
}

/// <summary>单个 DNS 地址的测量状态与结果。</summary>
public sealed class DnsSlot : INotifyPropertyChanged
{
    private long _latencyMs = -1;
    private bool _reachable;
    private bool _isChosen;
    private string _rank = string.Empty;
    private string _method = string.Empty;

    public DnsSlot(string address)
    {
        Address = address ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>DNS 服务器地址。</summary>
    public string Address { get; }

    /// <summary>解析耗时（毫秒）；无应答时为 -1。</summary>
    public long LatencyMs
    {
        get => _latencyMs;
        private set
        {
            if (SetField(ref _latencyMs, value))
            {
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    /// <summary>界面展示的耗时文本；无应答显示为 —，避免误显示成 0 ms。</summary>
    public string LatencyText => _reachable ? $"{_latencyMs} ms" : "—";

    /// <summary>是否收到有效应答。</summary>
    public bool Reachable
    {
        get => _reachable;
        private set => SetField(ref _reachable, value);
    }

    /// <summary>是否被选为推荐的首选/备选。</summary>
    public bool IsChosen
    {
        get => _isChosen;
        private set => SetField(ref _isChosen, value);
    }

    /// <summary>选中角色：首选 / 备选。</summary>
    public string Rank
    {
        get => _rank;
        private set => SetField(ref _rank, value);
    }

    /// <summary>实际生效的探测方式：DNS / TCP。</summary>
    public string Method
    {
        get => _method;
        private set => SetField(ref _method, value);
    }

    /// <summary>记录一次测量结果。</summary>
    public void Apply(ProbeResult result)
    {
        Reachable = result.Reachable;
        LatencyMs = result.Reachable ? (long)result.Elapsed.TotalMilliseconds : -1;
        Method = result.AttemptedMethod;
    }

    /// <summary>标记为无应答。</summary>
    public void MarkUnreachable()
    {
        Reachable = false;
        LatencyMs = -1;
        Method = string.Empty;
    }

    /// <summary>标记为推荐的某个角色。</summary>
    public void MarkAsChosen(string rank)
    {
        IsChosen = true;
        Rank = rank;
    }

    /// <summary>清空测量与选中状态，用于开始新一轮测试。</summary>
    public void Reset()
    {
        Reachable = false;
        LatencyMs = -1;
        IsChosen = false;
        Rank = string.Empty;
        Method = string.Empty;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
