using AutoLoginNet.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class LogRedactorTests
{
    [TestMethod]
    public void Redact_MasksPasswordAfterCharacterS()
    {
        // 旧实现的正则 [^&s] 会在 's' 处截断，导致密码后半段泄漏
        string result = LogRedactor.Redact("user_account=alice&user_password=p@ssw0rdXYZ&jsVersion=4.1.3");

        Assert.IsFalse(result.Contains("p@ssw0rdXYZ", StringComparison.Ordinal), "密码不得残留");
        Assert.IsFalse(result.Contains("ssw0rdXYZ", StringComparison.Ordinal), "密码后半段不得残留");
        Assert.IsTrue(result.Contains("user_password=***", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("jsVersion=4.1.3", StringComparison.Ordinal), "非敏感参数应保留");
    }

    [TestMethod]
    public void Redact_HandlesColonAndQuotedForms()
    {
        Assert.IsFalse(LogRedactor.Redact("password: \"hunter2\"").Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(LogRedactor.Redact("{\"password\":\"hunter2\"}").Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(LogRedactor.Redact("token=abc123").Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(LogRedactor.Redact("Secret = topsecret").Contains("topsecret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Redact_IsCaseInsensitive()
        => Assert.IsFalse(LogRedactor.Redact("Password=Abc123").Contains("Abc123", StringComparison.Ordinal));

    [TestMethod]
    public void Redact_LeavesOrdinaryTextUntouched()
    {
        const string text = "登录成功，耗时 123 ms，账号 20***1";
        Assert.AreEqual(text, LogRedactor.Redact(text));
    }

    [TestMethod]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, LogRedactor.Redact(null));
        Assert.AreEqual(string.Empty, LogRedactor.Redact(string.Empty));
    }

    [TestMethod]
    public void Redact_DoesNotMaskUnrelatedWordsContainingPass()
        => Assert.AreEqual("bypass=1", LogRedactor.Redact("bypass=1"));
}

[TestClass]
public sealed class FileLogServiceTests
{
    [TestMethod]
    public void Log_WritesRedactedEntryToDisk()
    {
        using var dir = new TempDirectory();
        string path = dir.File("app.log");
        ILogService log = new FileLogService(path, maxFileSizeMb: 5, retainedFileCount: 3, inMemoryCapacity: 100);

        log.Log(LogLevel.Info, "user_password=TopSecret&user_account=alice", "登录");

        string content = File.ReadAllText(path);
        Assert.IsFalse(content.Contains("TopSecret", StringComparison.Ordinal), "文件日志也必须脱敏");
        Assert.IsTrue(content.Contains("[Info] (登录)"), "日志应含级别与分类");
    }

    [TestMethod]
    public void Log_KeepsRecentEntriesInMemory()
    {
        using var dir = new TempDirectory();
        ILogService log = new FileLogService(dir.File("app.log"), 5, 3, 100);

        log.Info("第一条", "测试");
        log.Warn("第二条", "测试");

        Assert.AreEqual(2, log.GetRecent().Count);
        Assert.AreEqual(LogLevel.Warning, log.GetRecent()[1].Level);
    }

    [TestMethod]
    public void Log_RaisesEntryWrittenEvent()
    {
        using var dir = new TempDirectory();
        ILogService log = new FileLogService(dir.File("app.log"), 5, 3, 100);
        LogEntry? captured = null;
        log.EntryWritten += (_, entry) => captured = entry;

        log.Error("出错了", "测试");

        Assert.IsNotNull(captured);
        Assert.AreEqual(LogLevel.Error, captured!.Level);
    }

    [TestMethod]
    public void InMemoryBuffer_IsBoundedByCapacity()
    {
        using var dir = new TempDirectory();
        ILogService log = new FileLogService(dir.File("app.log"), 5, 3, inMemoryCapacity: 100);

        for (int i = 0; i < 250; i++)
        {
            log.Info($"第 {i} 条");
        }

        Assert.AreEqual(100, log.GetRecent().Count, "内存缓冲应有上限，避免长时间运行内存膨胀");
    }

    [TestMethod]
    public void ReadFromDisk_ParsesWrittenLines()
    {
        using var dir = new TempDirectory();
        string path = dir.File("app.log");
        ILogService log = new FileLogService(path, 5, 3, 100);
        log.Info("可解析的消息", "网络");

        var entries = log.ReadFromDisk();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("可解析的消息", entries[0].Message);
        Assert.AreEqual("网络", entries[0].Category);
        Assert.AreEqual(LogLevel.Info, entries[0].Level);
    }

    [TestMethod]
    public void ReadFromDisk_ToleratesForeignLines()
    {
        using var dir = new TempDirectory();
        string path = dir.File("app.log");
        File.WriteAllText(path, "这是一行旧格式日志" + Environment.NewLine);

        ILogService log = new FileLogService(path, 5, 3, 100);
        var entries = log.ReadFromDisk();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("这是一行旧格式日志", entries[0].Message);
    }

    [TestMethod]
    public void Clear_RemovesFileAndMemory()
    {
        using var dir = new TempDirectory();
        string path = dir.File("app.log");
        ILogService log = new FileLogService(path, 5, 3, 100);
        log.Info("message");

        log.Clear();

        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, log.GetRecent().Count);
    }

    [TestMethod]
    public void RollIfNeeded_CreatesNumberedBackupWhenExceedingSize()
    {
        using var dir = new TempDirectory();
        string path = dir.File("app.log");

        // 1 MB 上限，写入足够多的数据触发滚动
        ILogService log = new FileLogService(path, maxFileSizeMb: 1, retainedFileCount: 2, inMemoryCapacity: 100);
        string filler = new('x', 20_000);
        for (int i = 0; i < 120; i++)
        {
            log.Info(filler);
        }

        Assert.IsTrue(File.Exists($"{path}.1"), "超过大小上限后应生成滚动文件");
    }
}
