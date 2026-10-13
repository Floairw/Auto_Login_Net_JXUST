namespace AutoLoginNet.Diagnostics;

/// <summary>
/// 日志服务。实现必须保证不向调用方抛出异常——日志失败绝不能影响主流程。
/// </summary>
public interface ILogService
{
    /// <summary>日志文件完整路径。</summary>
    string LogFilePath { get; }

    /// <summary>写入一条日志。</summary>
    void Log(LogLevel level, string message, string category = "应用");

    void Debug(string message, string category = "应用") => Log(LogLevel.Debug, message, category);

    void Info(string message, string category = "应用") => Log(LogLevel.Info, message, category);

    void Warn(string message, string category = "应用") => Log(LogLevel.Warning, message, category);

    void Error(string message, string category = "应用") => Log(LogLevel.Error, message, category);

    /// <summary>获取内存中缓存的最近日志（按时间升序）。</summary>
    IReadOnlyList<LogEntry> GetRecent();

    /// <summary>从磁盘读取当前日志文件的末尾若干行，用于展示历史记录。</summary>
    IReadOnlyList<LogEntry> ReadFromDisk(int maxLines = 500);

    /// <summary>清空内存缓存与磁盘日志。</summary>
    void Clear();

    /// <summary>新日志写入后触发（可能在任意线程）。</summary>
    event EventHandler<LogEntry>? EntryWritten;
}
