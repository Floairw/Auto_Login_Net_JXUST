using AutoLoginNet.Diagnostics;

namespace AutoLoginNet.App;

/// <summary>组合根早期（日志尚未就绪）使用的空日志实现。</summary>
internal sealed class NullLogger : ILogService
{
    public static readonly NullLogger Instance = new();

    public string LogFilePath => string.Empty;

    public event EventHandler<LogEntry>? EntryWritten
    {
        add { }
        remove { }
    }

    public void Log(LogLevel level, string message, string category = "应用")
    {
    }

    public IReadOnlyList<LogEntry> GetRecent() => Array.Empty<LogEntry>();

    public IReadOnlyList<LogEntry> ReadFromDisk(int maxLines = 500) => Array.Empty<LogEntry>();

    public void Clear()
    {
    }
}
