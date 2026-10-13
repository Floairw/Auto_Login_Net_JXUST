namespace AutoLoginNet.Diagnostics;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>一条日志记录。</summary>
/// <param name="Timestamp">记录时间（本地时区偏移）。</param>
/// <param name="Level">级别。</param>
/// <param name="Category">来源分类，例如「网络」「登录」「开机自启」。</param>
/// <param name="Message">已脱敏的消息正文。</param>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    /// <summary>用于日志文件与界面展示的格式：时间 [级别] (分类) 消息。</summary>
    public string ToDisplayString()
        => $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] ({Category}) {Message}";
}
