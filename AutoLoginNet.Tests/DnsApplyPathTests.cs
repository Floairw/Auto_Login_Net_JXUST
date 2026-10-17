using AutoLoginNet.Diagnostics;
using AutoLoginNet.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Text.RegularExpressions;

namespace AutoLoginNet.Tests;

/// <summary>
/// DNS 结果文件的「文件协议」测试：提权子进程写、主进程读，双方只靠这个文件和
/// 命令行参数 --dns-result 约定，因此字段名、时间与路径规则都属于兼容性契约。
/// 与 <c>DnsOperationResultTests</c>（ServiceTests.cs）互补，不重复其已有用例。
/// </summary>
[TestClass]
public sealed class DnsResultFileProtocolTests
{
    [TestMethod]
    public void WriteToThenReadFrom_PreservesCompletedAtOffset()
    {
        using var dir = new TempDirectory();
        string path = dir.File("dns-result.json");
        DateTimeOffset completedAt = new DateTimeOffset(2026, 10, 17, 21, 43, 48, TimeSpan.FromHours(8));

        DnsOperationResult.WriteTo(
            path,
            new DnsOperationResult(false, "automatic", "以太网", "改为自动获取失败", completedAt));

        DnsOperationResult? loaded = DnsOperationResult.ReadFrom(path);

        Assert.IsNotNull(loaded);
        Assert.IsFalse(loaded!.Success);
        Assert.AreEqual("automatic", loaded.Action);
        Assert.AreEqual(completedAt, loaded.CompletedAt, "完成时间应原样往返");
        Assert.AreEqual(completedAt.Offset, loaded.CompletedAt.Offset, "时区偏移也是协议的一部分，不能被归一化成 UTC");
    }

    [TestMethod]
    public void WriteTo_ProducesCamelCaseFieldsReadableByMainProcess()
    {
        using var dir = new TempDirectory();
        string path = dir.File("dns-result.json");

        DnsOperationResult.WriteTo(
            path,
            new DnsOperationResult(true, "apply", "以太网", "已应用", DateTimeOffset.Now));

        string json = File.ReadAllText(path);

        // 主进程用源生成的同一上下文读取，字段名必须保持 camelCase，否则升级前后会读成 null
        foreach (string field in new[] { "\"success\"", "\"action\"", "\"adapterDisplay\"", "\"message\"", "\"completedAt\"" })
        {
            Assert.IsTrue(json.Contains(field, StringComparison.Ordinal), $"结果文件应包含字段 {field}");
        }
    }

    [TestMethod]
    public void ResultPathArgument_IsTheDocumentedSwitch()
        => Assert.AreEqual("--dns-result", DnsResultFile.ResultPathArgument);

    [TestMethod]
    public void CreatePath_LivesInDataDirectoryAndMatchesNamingConvention()
    {
        string path = DnsResultFile.CreatePath();

        Assert.AreEqual(AppPaths.DataDirectory, Path.GetDirectoryName(path), "结果文件应放在应用数据目录");
        Assert.IsTrue(
            Regex.IsMatch(Path.GetFileName(path), "^dns-result-[0-9a-f]{32}\\.json$"),
            $"结果文件名应为 dns-result-{{32 位十六进制}}.json，实际为 {Path.GetFileName(path)}");
    }

    [TestMethod]
    public void WriteTo_CreatesMissingParentDirectories()
    {
        using var dir = new TempDirectory();
        string path = dir.File(Path.Combine("nested", "deeper", "dns-result.json"));

        DnsOperationResult.WriteTo(path, new DnsOperationResult(true, "apply", "x", "y", DateTimeOffset.Now));

        Assert.IsTrue(File.Exists(path), "结果文件应被原子写入，父目录需自动创建");
        Assert.IsNotNull(DnsOperationResult.ReadFrom(path));
    }

    [TestMethod]
    public void WriteTo_OverwritesPreviousResult()
    {
        using var dir = new TempDirectory();
        string path = dir.File("dns-result.json");

        DnsOperationResult.WriteTo(path, new DnsOperationResult(true, "apply", "x", "旧结果", DateTimeOffset.Now));
        DnsOperationResult.WriteTo(path, new DnsOperationResult(false, "automatic", "x", "新结果", DateTimeOffset.Now));

        DnsOperationResult? loaded = DnsOperationResult.ReadFrom(path);
        Assert.IsNotNull(loaded);
        Assert.IsFalse(loaded!.Success, "同一路径再次写入应覆盖旧结果");
        Assert.AreEqual("新结果", loaded.Message);
        Assert.AreEqual("automatic", loaded.Action);
    }

    [TestMethod]
    public void ReadFrom_EmptyOrNonObjectJson_ReturnsNull()
    {
        using var dir = new TempDirectory();

        string empty = dir.File("empty.json");
        File.WriteAllText(empty, string.Empty);
        Assert.IsNull(DnsOperationResult.ReadFrom(empty), "空文件应按损坏处理");

        string jsonNull = dir.File("null.json");
        File.WriteAllText(jsonNull, "null");
        Assert.IsNull(DnsOperationResult.ReadFrom(jsonNull), "JSON null 应读成 null");

        string array = dir.File("array.json");
        File.WriteAllText(array, "[]");
        Assert.IsNull(DnsOperationResult.ReadFrom(array), "非对象 JSON 应按损坏处理");
    }
}

/// <summary>
/// <see cref="WindowsDnsService"/> 的可注入逻辑测试。
/// 进程调用（netsh / PowerShell）无法注入，因此这里只覆盖构造、网卡解析委托、
/// 候选列表与「命令必然失败」时的结果判定；不依赖真实网卡状态。
/// </summary>
[TestClass]
public sealed class WindowsDnsServiceInjectableTests
{
    [TestMethod]
    public void Constructor_NullResolver_Throws()
    {
        var ex = Assert.ThrowsException<ArgumentNullException>(
            () => new WindowsDnsService(null!, "1.1.1.1", new RecordingLogService()));
        Assert.AreEqual("resolver", ex.ParamName);
    }

    [TestMethod]
    public void Constructor_NullLog_Throws()
    {
        var ex = Assert.ThrowsException<ArgumentNullException>(
            () => new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", null!));
        Assert.AreEqual("log", ex.ParamName);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void GetActiveAdapterAsync_BlankCampusTarget_FallsBackToPublicDns(string campusTarget)
    {
        var adapter = new AdapterInfo(7, "以太网", "10.22.0.5");
        var resolver = new StubAdapterResolver(adapter);
        var service = new WindowsDnsService(resolver, campusTarget, new RecordingLogService());

        _ = service.GetActiveAdapterAsync().GetAwaiter().GetResult();

        Assert.AreEqual("1.1.1.1", resolver.LastTarget, "空白探测目标应回退到 1.1.1.1，否则解析网卡会失败");
    }

    [TestMethod]
    public void GetActiveAdapterAsync_ForwardsTargetAndCancellationToken()
    {
        var adapter = new AdapterInfo(7, "以太网", "10.22.0.5");
        var resolver = new StubAdapterResolver(adapter);
        var service = new WindowsDnsService(resolver, "10.22.187.75", new RecordingLogService());
        using var cts = new CancellationTokenSource();

        AdapterInfo? resolved = service.GetActiveAdapterAsync(cts.Token).GetAwaiter().GetResult();

        Assert.AreEqual(adapter, resolved, "应原样返回解析器选出的网卡");
        Assert.AreEqual("10.22.187.75", resolver.LastTarget, "校园网探测目标应透传给解析器");
        Assert.AreEqual(cts.Token, resolver.LastToken, "取消令牌应透传，便于提权等场景提前放弃");
    }

    [TestMethod]
    public void GetActiveAdapterAsync_WhenResolverFindsNothing_ReturnsNull()
    {
        var service = new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", new RecordingLogService());

        Assert.IsNull(service.GetActiveAdapterAsync().GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Providers_ExposeFiveDistinctPublicDnsPairs()
    {
        var service = new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", new RecordingLogService());

        Assert.AreEqual(5, service.Providers.Count, "内置候选应为 5 个公共 DNS 提供商");

        var primaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (DnsProvider provider in service.Providers)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(provider.Name), "提供商名称不能为空");
            Assert.IsTrue(IPAddress.TryParse(provider.Primary, out _), $"{provider.Name} 的首选地址应为合法 IPv4");
            Assert.IsTrue(IPAddress.TryParse(provider.Backup, out _), $"{provider.Name} 的备选地址应为合法 IPv4");
            Assert.AreNotEqual(provider.Primary, provider.Backup, $"{provider.Name} 的主备地址不应相同");
            Assert.IsTrue(primaries.Add(provider.Primary), $"{provider.Name} 的首选地址与其他提供商重复");
        }
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void ApplyAsync_BlankPrimary_ReturnsFalseWithoutTouchingSystem(string primary)
    {
        var log = new RecordingLogService();
        var service = new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", log);
        var adapter = new AdapterInfo(7, "以太网", "10.22.0.5");

        bool ok = service.ApplyAsync(adapter, new DnsPair(primary, "8.8.8.8")).GetAwaiter().GetResult();

        Assert.IsFalse(ok, "首选为空时应直接判定失败");
        Assert.AreEqual(0, log.Entries.Count, "首选为空时不应调用任何外部命令，也就不应产生日志");
    }

    [TestMethod]
    public void GetCurrentDnsAsync_ForNonexistentAdapter_ReturnsNullAndWarns()
    {
        var log = new RecordingLogService();
        var service = new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", log);
        var missing = new AdapterInfo(-1, "ALN_Test_" + Guid.NewGuid().ToString("N"), "10.0.0.1");

        DnsPair? dns = service.GetCurrentDnsAsync(missing).GetAwaiter().GetResult();

        Assert.IsNull(dns, "netsh 找不到该网卡时应按读取失败返回 null，而不是抛异常");
        Assert.IsTrue(log.Contains("读取 DNS 失败", LogLevel.Warning), "读取失败应记 Warning 并带上网卡信息");
    }

    [TestMethod]
    public void ApplyAsync_WhenBothCommandPathsFail_ReturnsFalseAndLogsError()
    {
        var log = new RecordingLogService();
        var service = new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", log);
        // Index<=0 会让 PowerShell 兜底路径直接放弃，因此这里只依赖 netsh 对不存在网卡的失败
        var missing = new AdapterInfo(-1, "ALN_Test_" + Guid.NewGuid().ToString("N"), "10.0.0.1");

        bool ok = service.ApplyAsync(missing, new DnsPair("223.5.5.5", "223.6.6.6")).GetAwaiter().GetResult();

        Assert.IsFalse(ok, "两条命令路径都失败时应返回 false");
        Assert.IsTrue(log.Contains("netsh 设置首选 DNS 失败", LogLevel.Warning), "netsh 失败应记 Warn 说明将改用 PowerShell");
        Assert.IsTrue(log.Contains("设置 DNS 失败", LogLevel.Error), "两条路径均失败时应记 Error，供界面提示用户");
    }

    [TestMethod]
    public void ResetToAutomaticAsync_ForNonexistentAdapter_ReturnsFalseAndLogsError()
    {
        var log = new RecordingLogService();
        var service = new WindowsDnsService(new StubAdapterResolver(null), "1.1.1.1", log);
        var missing = new AdapterInfo(-1, "ALN_Test_" + Guid.NewGuid().ToString("N"), "10.0.0.1");

        bool ok = service.ResetToAutomaticAsync(missing).GetAwaiter().GetResult();

        Assert.IsFalse(ok, "改回自动获取失败时应返回 false");
        Assert.IsTrue(log.Contains("改为自动获取失败", LogLevel.Error), "失败应记 Error 供界面提示");
    }
}

/// <summary>测试用网卡解析器：记录调用参数，返回预置网卡。</summary>
internal sealed class StubAdapterResolver : INetworkAdapterResolver
{
    private readonly AdapterInfo? _adapter;

    public StubAdapterResolver(AdapterInfo? adapter) => _adapter = adapter;

    public string? LastTarget { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public Task<AdapterInfo?> ResolveAsync(string? target = null, CancellationToken cancellationToken = default)
    {
        LastTarget = target;
        LastToken = cancellationToken;
        return Task.FromResult(_adapter);
    }
}
