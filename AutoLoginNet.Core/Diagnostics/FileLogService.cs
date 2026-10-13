using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoLoginNet.Diagnostics;

/// <summary>
/// 文件日志实现：追加写入（不独占句柄，便于外部工具 tail）、按大小滚动、保留若干历史份数，
/// 并在内存中保留最近若干条供界面实时展示。所有异常都被吞掉，日志失败不影响主流程。
/// </summary>
public sealed partial class FileLogService : ILogService
{
    private const int MaxInMemoryEntries = 20000;

    private readonly object _sync = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly long _maxBytes;
    private readonly int _maxFiles;

    public FileLogService(string logFilePath, int maxFileSizeMb, int retainedFileCount, int inMemoryCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        LogFilePath = logFilePath;
        _maxBytes = Math.Max(1, maxFileSizeMb) * 1024L * 1024L;
        _maxFiles = Math.Clamp(retainedFileCount, 1, 50);
        InMemoryCapacity = Math.Clamp(inMemoryCapacity, 100, MaxInMemoryEntries);
    }

    /// <summary>内存中保留的日志条数上限。</summary>
    public int InMemoryCapacity { get; }

    public string LogFilePath { get; }

    public event EventHandler<LogEntry>? EntryWritten;

    public void Log(LogLevel level, string message, string category = "应用")
    {
        LogEntry entry;
        try
        {
            string safeMessage = LogRedactor.Redact(message);
            string safeCategory = string.IsNullOrWhiteSpace(category) ? "应用" : category.Trim();
            entry = new LogEntry(DateTimeOffset.Now, level, safeCategory, safeMessage);

            lock (_sync)
            {
                _recent.Enqueue(entry);
                while (_recent.Count > InMemoryCapacity)
                {
                    _recent.Dequeue();
                }

                AppendToFile(entry);
            }
        }
        catch
        {
            // 日志失败不阻断主流程
            return;
        }

        try
        {
            EntryWritten?.Invoke(this, entry);
        }
        catch
        {
            // 订阅者异常同样不影响主流程
        }
    }

    public IReadOnlyList<LogEntry> GetRecent()
    {
        lock (_sync)
        {
            return _recent.ToArray();
        }
    }

    public IReadOnlyList<LogEntry> ReadFromDisk(int maxLines = 500)
    {
        maxLines = Math.Clamp(maxLines, 1, 50000);
        try
        {
            if (!File.Exists(LogFilePath))
            {
                return Array.Empty<LogEntry>();
            }

            var lines = new LinkedList<string>();
            using var stream = new FileStream(
                LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                lines.AddLast(line);
                if (lines.Count > maxLines)
                {
                    lines.RemoveFirst();
                }
            }

            var entries = new List<LogEntry>(lines.Count);
            foreach (string line in lines)
            {
                if (TryParseLine(line, out var parsed))
                {
                    entries.Add(new LogEntry(parsed.Timestamp, parsed.Level, parsed.Category, parsed.Message));
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    entries.Add(new LogEntry(DateTimeOffset.MinValue, LogLevel.Info, "历史", line));
                }
            }

            return entries;
        }
        catch
        {
            return Array.Empty<LogEntry>();
        }
    }

    public void Clear()
    {
        try
        {
            lock (_sync)
            {
                _recent.Clear();
                for (int i = 1; i <= _maxFiles; i++)
                {
                    string rotated = $"{LogFilePath}.{i}";
                    if (File.Exists(rotated))
                    {
                        File.Delete(rotated);
                    }
                }
                if (File.Exists(LogFilePath))
                {
                    File.Delete(LogFilePath);
                }
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private void AppendToFile(LogEntry entry)
    {
        string? directory = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RollIfNeeded();

        File.AppendAllText(LogFilePath, entry.ToDisplayString() + Environment.NewLine, Encoding.UTF8);
    }

    private void RollIfNeeded()
    {
        if (!File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length < _maxBytes)
        {
            return;
        }

        string oldest = $"{LogFilePath}.{_maxFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = _maxFiles - 1; i >= 1; i--)
        {
            string source = $"{LogFilePath}.{i}";
            if (File.Exists(source))
            {
                File.Move(source, $"{LogFilePath}.{i + 1}", overwrite: true);
            }
        }

        File.Move(LogFilePath, $"{LogFilePath}.1", overwrite: true);
    }

    private static bool TryParseLine(string line, out ParsedLine parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = LineRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                match.Groups["ts"].Value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var timestamp))
        {
            return false;
        }

        LogLevel level = Enum.TryParse<LogLevel>(match.Groups["level"].Value, ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : LogLevel.Info;

        parsed = new ParsedLine(
            timestamp,
            level,
            match.Groups["cat"].Value,
            match.Groups["msg"].Value);
        return true;
    }

    [GeneratedRegex(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(?<level>\w+)\] \((?<cat>[^)]*)\) (?<msg>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LineRegex();

    private readonly record struct ParsedLine(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);
}
