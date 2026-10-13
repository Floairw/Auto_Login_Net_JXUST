using AutoLoginNet.Credentials;
using AutoLoginNet.Diagnostics;

namespace AutoLoginNet.Tests;

/// <summary>测试用日志实现：把日志留在内存里便于断言。</summary>
internal sealed class RecordingLogService : ILogService
{
    private readonly List<LogEntry> _entries = new();

    public string LogFilePath => "(memory)";

    public event EventHandler<LogEntry>? EntryWritten;

    public IReadOnlyList<LogEntry> Entries => _entries;

    public void Log(LogLevel level, string message, string category = "应用")
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, LogRedactor.Redact(message));
        _entries.Add(entry);
        EntryWritten?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> GetRecent() => _entries.ToArray();

    public IReadOnlyList<LogEntry> ReadFromDisk(int maxLines = 500) => Array.Empty<LogEntry>();

    public void Clear() => _entries.Clear();

    public bool Contains(string fragment, LogLevel? level = null)
        => _entries.Any(e =>
            (level is null || e.Level == level)
            && e.Message.Contains(fragment, StringComparison.Ordinal));
}

/// <summary>测试用密码保护器：可逆的伪加密，用于验证存储流程而不依赖 DPAPI。</summary>
internal sealed class FakeCredentialProtector : ICredentialProtector
{
    private const string Prefix = "enc:";

    public string Protect(string plainText) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

    public string Unprotect(string cipherText)
    {
        if (!cipherText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new System.Security.Cryptography.CryptographicException("格式不正确");
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cipherText[Prefix.Length..]));
    }
}

/// <summary>测试用临时目录，析构时自动清理。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aln_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
}
