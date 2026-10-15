using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Mvvm;
using AutoLoginNet.Diagnostics;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AutoLoginNet.App.ViewModels;

/// <summary>日志页过滤器。</summary>
public sealed class LogFilter : ObservableObject
{
    private string _keyword = string.Empty;
    private LogLevel? _minimumLevel;

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    /// <summary>最低级别；null 表示不过滤。</summary>
    public LogLevel? MinimumLevel
    {
        get => _minimumLevel;
        set => SetProperty(ref _minimumLevel, value);
    }

    public bool Matches(LogEntry entry)
    {
        if (_minimumLevel is { } minimum && entry.Level < minimum)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_keyword))
        {
            return true;
        }

        string keyword = _keyword.Trim();
        return entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 日志页视图模型。
/// <para>
/// 性能设计：日志写入来自任意线程且可能非常密集，因此
/// 1) 追加走「快照 + 尾部增量」，绝大多数情况是 O(1)，只有丢弃旧记录时才重建；
/// 2) 界面刷新在同一批消息内只做一次（<see cref="DispatcherQueue.TryEnqueue"/> 合并），
///    不会出现「一条日志一次集合变更事件」导致的卡顿；
/// 3) 状态栏文本按需刷新，避免每条日志都做字符串拼接。
/// </para>
/// </summary>
public sealed class LogsViewModel : ObservableObject
{
    private const int MaxBuffered = 4000;
    private const int MaxVisible = 2000;
    private const int StatusRefreshInterval = 25;

    private readonly AppServices _services;
    private readonly List<LogEntry> _all = new();
    private readonly object _sync = new();

    private IReadOnlyList<LogEntry> _snapshot = Array.Empty<LogEntry>();
    private int _renderedCount;
    private bool _pendingStructuralChange;
    private bool _flushQueued;
    private int _appendsSinceStatusUpdate;

    private string _statusText = "—";
    private bool _autoScroll = true;
    private bool _isPinnedToBottom = true;

    public LogsViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Dispatcher = services.Dispatcher;

        Filter = new LogFilter();
        Filter.PropertyChanged += (_, _) => Rebuild();

        RefreshCommand = new RelayCommand(Refresh);
        ClearCommand = new AsyncRelayCommand(ClearAsync);
        OpenFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(AppPaths.DataDirectory));

        _services.Log.EntryWritten += OnEntryWritten;
    }

    /// <summary>一批新记录已推送到界面后触发，供界面执行自动滚动。</summary>
    public event EventHandler? Flushed;

    /// <summary>界面线程队列，用于合并刷新。</summary>
    public DispatcherQueue Dispatcher { get; }

    /// <summary>
    /// 供界面绑定的日志集合。只在刷新时整体替换引用，因此不需要
    /// ObservableCollection 的逐项通知（那正是旧实现卡顿的来源）。
    /// </summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogFilter Filter { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public string LogFilePath => _services.Log.LogFilePath;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>
    /// 由界面告知当前是否停留在列表底部。
    /// 离开底部时暂停自动滚动，避免用户查看历史记录时被不断拽回底部。
    /// </summary>
    public void SetPinnedToBottom(bool pinned)
    {
        _isPinnedToBottom = pinned;
        OnPropertyChanged(nameof(ShouldAutoScroll));
    }

    /// <summary>是否应当自动滚动到底部。</summary>
    public bool ShouldAutoScroll => _autoScroll && _isPinnedToBottom;

    /// <summary>
    /// 从磁盘加载历史日志（含上次运行留下的记录），并与内存中的实时记录合并。
    /// <para>
    /// 只取内存缓冲会漏掉本次运行之前写入的历史，只取磁盘又看不到尚未落盘的最新几条，
    /// 因此两边都要读：以磁盘尾部为底座，再补上内存里磁盘尚未包含的记录。
    /// </para>
    /// </summary>
    public void Refresh()
    {
        IReadOnlyList<LogEntry> fromDisk = _services.Log.ReadFromDisk(MaxVisible);
        IReadOnlyList<LogEntry> buffered = _services.Log.GetRecent();

        IReadOnlyList<LogEntry> loaded = Merge(fromDisk, buffered);

        lock (_sync)
        {
            _all.Clear();
            _all.AddRange(loaded);
        }

        Rebuild();
    }

    /// <summary>
    /// 合并磁盘记录与内存记录。
    /// 去重键为「时间戳 + 级别 + 分类 + 正文」——同一条日志在磁盘与内存中是完全相同的文本。
    /// </summary>
    private static IReadOnlyList<LogEntry> Merge(
        IReadOnlyList<LogEntry> fromDisk,
        IReadOnlyList<LogEntry> buffered)
    {
        if (fromDisk.Count == 0)
        {
            return buffered;
        }

        if (buffered.Count == 0)
        {
            return fromDisk;
        }

        var merged = new List<LogEntry>(fromDisk.Count + buffered.Count);
        var seen = new HashSet<LogEntry>();

        foreach (LogEntry entry in fromDisk)
        {
            merged.Add(entry);
            seen.Add(entry);
        }

        foreach (LogEntry entry in buffered)
        {
            if (seen.Add(entry))
            {
                merged.Add(entry);
            }
        }

        return merged;
    }

    /// <summary>实时追加一条日志（可能在任意线程调用）。</summary>
    public void Append(LogEntry entry)
    {
        lock (_sync)
        {
            _all.Add(entry);

            // 超出缓冲上限时丢弃最旧的一批，并标记需要重建
            if (_all.Count > MaxBuffered)
            {
                _all.RemoveRange(0, _all.Count - MaxBuffered);
                _pendingStructuralChange = true;
            }
        }

        QueueFlush();
    }

    /// <summary>清空内存与磁盘日志（确认流程由调用方负责）。</summary>
    public async Task ClearAsync()
    {
        bool confirmed = await ShellHelper.ConfirmAsync(
            App.MainWindow?.Content?.XamlRoot,
            "清空日志",
            "将删除内存缓存与磁盘上的全部日志文件，确定继续吗？",
            "清空");

        if (!confirmed)
        {
            return;
        }

        _services.Log.Clear();

        lock (_sync)
        {
            _all.Clear();
        }

        Entries.Clear();
        _snapshot = Array.Empty<LogEntry>();
        _renderedCount = 0;
        UpdateStatus();
    }

    private void OnEntryWritten(object? sender, LogEntry entry) => Append(entry);

    /// <summary>把刷新请求合并到一次界面调用，密集日志下也不会频繁刷新。</summary>
    private void QueueFlush()
    {
        if (_flushQueued)
        {
            return;
        }

        _flushQueued = true;

        if (!Dispatcher.TryEnqueue(() =>
            {
                _flushQueued = false;
                FlushPending();
            }))
        {
            _flushQueued = false;
        }
    }

    /// <summary>把新增记录增量推送到界面。</summary>
    private void FlushPending()
    {
        LogEntry[] buffer;
        bool structural;
        lock (_sync)
        {
            buffer = _all.ToArray();
            structural = _pendingStructuralChange;
            _pendingStructuralChange = false;
        }

        if (structural || _renderedCount == 0 || buffer.Length < _renderedCount)
        {
            Rebuild(buffer);
            return;
        }

        if (buffer.Length == _renderedCount)
        {
            return;
        }

        // 仅追加尾部新增项，避免每次刷新都重建整个集合
        for (int i = _renderedCount; i < buffer.Length; i++)
        {
            if (Filter.Matches(buffer[i]))
            {
                Entries.Add(buffer[i]);
            }
        }

        _renderedCount = buffer.Length;
        TrimVisible();

        _appendsSinceStatusUpdate++;
        if (_appendsSinceStatusUpdate >= StatusRefreshInterval)
        {
            UpdateStatus();
        }

        Flushed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild() => Rebuild(null);

    private void Rebuild(LogEntry[]? buffer)
    {
        buffer ??= _all.ToArray();

        Entries.Clear();
        foreach (LogEntry entry in buffer)
        {
            if (Filter.Matches(entry) && Entries.Count < MaxVisible)
            {
                Entries.Add(entry);
            }
        }

        _renderedCount = buffer.Length;
        _appendsSinceStatusUpdate = 0;
        OnPropertyChanged(nameof(IsEmpty));
        UpdateStatus();
        Flushed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimVisible()
    {
        bool removed = false;
        while (Entries.Count > MaxVisible)
        {
            Entries.RemoveAt(0);
            removed = true;
        }

        if (removed)
        {
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void UpdateStatus()
    {
        _appendsSinceStatusUpdate = 0;
        StatusText = _all.Count == 0
            ? "暂无日志记录"
            : $"显示 {Entries.Count} / 共 {_all.Count} 条 · {_services.Log.LogFilePath}";
        OnPropertyChanged(nameof(IsEmpty));
    }
}
