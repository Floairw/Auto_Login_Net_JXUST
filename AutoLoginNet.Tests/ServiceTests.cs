using AutoLoginNet.Auth;
using AutoLoginNet.Credentials;
using AutoLoginNet.Network;
using AutoLoginNet.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class LoginServiceTests
{
    [TestMethod]
    public async Task LoginAsync_Success_SavesCredentialAutomatically()
    {
        using var dir = new TempDirectory();
        var store = new JsonCredentialStore(dir.File("c.json"), new FakeCredentialProtector(), new RecordingLogService());
        var service = new LoginService(new StubAuthenticator(true), new StubDetector(), store, new RecordingLogService());

        LoginAttempt attempt = await service.LoginAsync("2024001", "pw", OperatorKind.Mobile);

        Assert.IsTrue(attempt.Result.IsSuccess);
        Assert.IsNotNull(store.Load(), "认证成功后应自动保存凭据以支持开机自启");
        Assert.AreEqual("2024001@cmcc", attempt.Result.RawResponse);
    }

    [TestMethod]
    public async Task LoginAsync_Failure_DoesNotSaveCredential()
    {
        using var dir = new TempDirectory();
        var store = new JsonCredentialStore(dir.File("c.json"), new FakeCredentialProtector(), new RecordingLogService());
        var service = new LoginService(new StubAuthenticator(false), new StubDetector(), store, new RecordingLogService());

        await service.LoginAsync("2024001", "wrong", OperatorKind.Campus);

        Assert.IsFalse(store.Exists, "认证失败时不应保存凭据");
    }

    [TestMethod]
    public async Task LoginAsync_DetectsNetworkByDefault()
    {
        var detector = new StubDetector();
        var service = NewService(new StubAuthenticator(true), detector);

        LoginAttempt attempt = await service.LoginAsync("u", "p", OperatorKind.Campus);

        Assert.IsTrue(detector.WasCalled, "默认应在认证前检测网络");
        Assert.IsNotNull(attempt.Snapshot);
    }

    [TestMethod]
    public async Task LoginAsync_SkipNetworkCheck_DoesNotProbe()
    {
        var detector = new StubDetector();
        var service = NewService(new StubAuthenticator(true), detector);

        LoginAttempt attempt = await service.LoginAsync("u", "p", OperatorKind.Campus, skipNetworkCheck: true);

        Assert.IsFalse(detector.WasCalled, "跳过检测时不应触发探测");
        Assert.IsNull(attempt.Snapshot);
    }

    private static LoginService NewService(IAuthenticator authenticator, INetworkDetector detector)
    {
        var store = new JsonCredentialStore(
            Path.Combine(Path.GetTempPath(), "aln_unused_" + Guid.NewGuid().ToString("N") + ".json"),
            new FakeCredentialProtector(),
            new RecordingLogService());

        return new LoginService(authenticator, detector, store, new RecordingLogService());
    }

    private sealed class StubAuthenticator : IAuthenticator
    {
        private readonly bool _succeed;

        public StubAuthenticator(bool succeed) => _succeed = succeed;

        public Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
            => Task.FromResult(new LoginResult
            {
                Status = _succeed ? LoginStatus.Success : LoginStatus.InvalidCredentials,
                RawResponse = account,
                ServerMessage = _succeed ? "ok" : "账号或密码错误",
            });
    }

    private sealed class StubDetector : INetworkDetector
    {
        public bool WasCalled { get; private set; }

        public Task<bool> WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<NetworkSnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new NetworkSnapshot(
                new ProbeResult("campus", true, TimeSpan.Zero, "", "HTTP"),
                new ProbeResult("external", false, TimeSpan.Zero, "", "ICMP"),
                AuthRequirement.Required,
                DateTimeOffset.Now));
        }
    }
}

[TestClass]
public sealed class ScheduledTaskAutoStartTests
{
    [TestMethod]
    public async Task SetEnabledAsync_DisablingNonExistentTask_Succeeds()
    {
        using var dir = new TempDirectory();
        string marker = dir.File("autostart.json");

        var service = new ScheduledTaskAutoStartService(new RecordingLogService(), marker);

        // 关键：任务名必须唯一，否则会与开发机上真实存在的自启任务互相干扰
        var command = new AutoStartCommand(
            "C:\\nonexistent\\app.exe",
            "--autostart",
            "AlnTestTask_" + Guid.NewGuid().ToString("N"));

        // 删除一个不存在的任务：schtasks 返回「找不到任务」，实现应视为成功
        bool ok = await service.SetEnabledAsync(enabled: false, command);

        Assert.IsTrue(ok, "取消一个本就不存在的任务应视为成功");
        Assert.IsFalse(service.IsEnabled);
    }

    [TestMethod]
    public void IsEnabled_WhenMarkerSaysDisabled_IsFalse()
    {
        using var dir = new TempDirectory();
        string marker = dir.File("autostart.json");

        // 显式写入「已关闭 + 唯一任务名」的期望状态，使断言不依赖宿主机上是否存在同名任务
        AutoStartMarker state = new(
            enabled: false,
            taskName: "AlnTestTask_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(
            marker,
            System.Text.Json.JsonSerializer.Serialize(state, AppJsonContext.Default.AutoStartMarker));

        var service = new ScheduledTaskAutoStartService(new RecordingLogService(), marker);

        Assert.IsFalse(service.IsEnabled);
    }

    [TestMethod]
    public void IsEnabled_WhenMarkerSaysEnabledButTaskMissing_UsesMarkerAsIntent()
    {
        using var dir = new TempDirectory();
        string marker = dir.File("autostart.json");

        // 任务不存在时，标记文件代表用户意图；schtasks 明确返回「找不到任务」时应以实际为准，
        // 因此这里用唯一任务名构造「明确不存在」的场景，期望得到 false
        AutoStartMarker state = new(
            enabled: true,
            taskName: "AlnTestTask_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(
            marker,
            System.Text.Json.JsonSerializer.Serialize(state, AppJsonContext.Default.AutoStartMarker));

        var service = new ScheduledTaskAutoStartService(new RecordingLogService(), marker);

        // 能明确查到「任务不存在」时，以实际状态为准，不应被标记文件误导
        Assert.IsFalse(service.IsEnabled, "查询到任务确实不存在时，不应仅凭标记文件报告已启用");
    }
}

[TestClass]
public sealed class NetUtilsTests
{
    [TestMethod]
    public void Quote_WrapsPathInQuotes()
        => Assert.AreEqual("\"C:\\Program Files\\App\\app.exe\"", NetUtils.Quote("C:\\Program Files\\App\\app.exe"));

    [TestMethod]
    public void Quote_DoesNotDoubleWrap()
        => Assert.AreEqual("\"C:\\app.exe\"", NetUtils.Quote("\"C:\\app.exe\""));

    [TestMethod]
    public void Quote_EscapesInnerQuotes()
        => Assert.AreEqual("\"a\\\"b\"", NetUtils.Quote("a\"b"));
}

[TestClass]
public sealed class DnsPairTests
{
    [TestMethod]
    public void ToString_ShowsBothServers()
        => Assert.AreEqual("手动设置：1.1.1.1 / 1.0.0.1", new DnsPair("1.1.1.1", "1.0.0.1").ToString());

    [TestMethod]
    public void ToString_OmitsEmptyBackup()
        => Assert.AreEqual("手动设置：1.1.1.1", new DnsPair("1.1.1.1", string.Empty).ToString());
}

[TestClass]
public sealed class DnsOperationResultTests
{
    [TestMethod]
    public void WriteToThenReadFrom_RoundTrips()
    {
        using var dir = new TempDirectory();
        string path = dir.File("dns-result.json");

        var result = new DnsOperationResult(
            true, "apply", "以太网（10.22.187.75）", "已应用 114.114.114.114", DateTimeOffset.Now);

        DnsOperationResult.WriteTo(path, result);

        DnsOperationResult? loaded = DnsOperationResult.ReadFrom(path);

        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded!.Success);
        Assert.AreEqual("apply", loaded.Action);
        Assert.AreEqual("以太网（10.22.187.75）", loaded.AdapterDisplay);
        Assert.AreEqual("已应用 114.114.114.114", loaded.Message);
    }

    [TestMethod]
    public void ReadFrom_MissingOrCorruptFile_ReturnsNull()
    {
        using var dir = new TempDirectory();

        Assert.IsNull(DnsOperationResult.ReadFrom(dir.File("missing.json")));

        string corrupt = dir.File("corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        Assert.IsNull(DnsOperationResult.ReadFrom(corrupt));
    }

    [TestMethod]
    public void WriteTo_LeavesNoTemporaryFileBehind()
    {
        using var dir = new TempDirectory();
        string path = dir.File("dns-result.json");

        DnsOperationResult.WriteTo(path, new DnsOperationResult(false, "automatic", "x", "y", DateTimeOffset.Now));

        Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void CreatePath_IsUniquePerCall()
    {
        string first = DnsResultFile.CreatePath();
        string second = DnsResultFile.CreatePath();

        Assert.AreNotEqual(first, second, "并发操作需要互不覆盖的结果文件");
        Assert.IsTrue(first.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }
}
