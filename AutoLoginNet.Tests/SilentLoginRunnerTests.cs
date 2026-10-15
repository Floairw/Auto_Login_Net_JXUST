using AutoLoginNet.Auth;
using AutoLoginNet.Configuration;
using AutoLoginNet.Credentials;
using AutoLoginNet.Network;
using AutoLoginNet.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

/// <summary>
/// 开机自启静默登录流程的测试。
/// 这段逻辑只在开机那一刻执行，出问题极难复现，因此刻意抽到 Core 层以便完整覆盖。
/// </summary>
[TestClass]
public sealed class SilentLoginRunnerTests
{
    private static LoginCredential Credential => new("2024001", "pw", OperatorKind.Telecom, DateTimeOffset.Now);

    private static StartupOptions Options(int retryCount = 2, int retryDelaySeconds = 3) => new()
    {
        LoginRetryCount = retryCount,
        LoginRetryDelaySeconds = retryDelaySeconds,
    };

    [TestMethod]
    public async Task RunAsync_WithoutCredential_ReportsNoCredential()
    {
        var login = new FakeLoginService(_ => LoginStatus.Success);
        var runner = NewRunner(login, options: Options());

        SilentLoginOutcome outcome = await runner.RunAsync(null);

        Assert.AreEqual(SilentLoginOutcome.NoCredentialExitCode, outcome.ExitCode);
        Assert.AreEqual(0, login.CallCount, "没有凭据时不应发起认证");
    }

    [TestMethod]
    public async Task RunAsync_AlreadyOnline_SkipsAuthentication()
    {
        var login = new FakeLoginService(_ => LoginStatus.Success);
        var runner = NewRunner(login, online: true, options: Options());

        SilentLoginOutcome outcome = await runner.RunAsync(Credential);

        Assert.AreEqual(0, outcome.ExitCode);
        Assert.AreEqual(0, login.CallCount, "外网已连通时不应重复认证");
    }

    [TestMethod]
    public async Task RunAsync_Success_ExitsZero()
    {
        var login = new FakeLoginService(_ => LoginStatus.Success);
        var runner = NewRunner(login, options: Options());

        SilentLoginOutcome outcome = await runner.RunAsync(Credential);

        Assert.AreEqual(0, outcome.ExitCode);
        Assert.AreEqual(1, login.CallCount);
    }

    [TestMethod]
    public async Task RunAsync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        // 模拟开机场景：认证服务器一开始不可达，稍后才可用
        var login = new FakeLoginService(attempt =>
            attempt < 3 ? LoginStatus.NetworkError : LoginStatus.Success);

        var delays = new List<int>();
        var runner = NewRunner(login, options: Options(retryCount: 3, retryDelaySeconds: 2), delays: delays);

        SilentLoginOutcome outcome = await runner.RunAsync(Credential);

        Assert.AreEqual(0, outcome.ExitCode, "重试后成功应返回成功码");
        Assert.AreEqual(3, login.CallCount, "应尝试 3 次（前两次瞬时失败）");
        Assert.AreEqual(3, runner.AttemptCount);
        CollectionAssert.AreEqual(new[] { 2000, 2000 }, delays.ToArray(), "每次重试间隔应为配置的秒数");
    }

    [TestMethod]
    public async Task RunAsync_TransientFailureExhaustsRetries_ReportsFailure()
    {
        var login = new FakeLoginService(_ => LoginStatus.NetworkError);
        var runner = NewRunner(login, options: Options(retryCount: 2, retryDelaySeconds: 1));

        SilentLoginOutcome outcome = await runner.RunAsync(Credential);

        Assert.AreEqual(SilentLoginOutcome.AuthFailedExitCode, outcome.ExitCode);
        // 首次 + 2 次重试
        Assert.AreEqual(3, login.CallCount);
    }

    [TestMethod]
    public async Task RunAsync_NonTransientFailure_DoesNotRetry()
    {
        // 密码错误重试再多次也没用，应立即停止，避免白等
        var login = new FakeLoginService(_ => LoginStatus.InvalidCredentials);
        var delays = new List<int>();
        var runner = NewRunner(login, options: Options(retryCount: 5), delays: delays);

        SilentLoginOutcome outcome = await runner.RunAsync(Credential);

        Assert.AreEqual(SilentLoginOutcome.AuthFailedExitCode, outcome.ExitCode);
        Assert.AreEqual(1, login.CallCount, "非瞬时故障不应重试");
        Assert.AreEqual(0, delays.Count, "不应产生任何等待");
    }

    [TestMethod]
    public async Task RunAsync_SuspendedAccount_DoesNotRetry()
    {
        var login = new FakeLoginService(_ => LoginStatus.AccountSuspended);
        var runner = NewRunner(login, options: Options(retryCount: 3));

        await runner.RunAsync(Credential);

        Assert.AreEqual(1, login.CallCount);
    }

    [TestMethod]
    public async Task RunAsync_ZeroRetryCount_TriesExactlyOnce()
    {
        var login = new FakeLoginService(_ => LoginStatus.NetworkError);
        var delays = new List<int>();
        var runner = NewRunner(login, options: Options(retryCount: 0), delays: delays);

        await runner.RunAsync(Credential);

        Assert.AreEqual(1, login.CallCount, "重试次数为 0 时只尝试一次");
        Assert.AreEqual(0, delays.Count);
    }

    [TestMethod]
    public async Task RunAsync_HttpException_IsTreatedAsTransient()
    {
        var login = new FakeLoginService(attempt =>
            attempt < 2 ? throw new HttpRequestException("连接被拒绝") : LoginStatus.Success);

        var runner = NewRunner(login, options: Options(retryCount: 2, retryDelaySeconds: 1));

        SilentLoginOutcome outcome = await runner.RunAsync(Credential);

        Assert.AreEqual(0, outcome.ExitCode, "HttpRequestException 也应触发重试");
        Assert.AreEqual(2, login.CallCount);
    }

    [TestMethod]
    public async Task RunAsync_AlwaysSkipsNetworkCheckInsideLoginService()
    {
        // 静默登录已经自己做过网络判定，不应让 LoginService 再探测一次
        var login = new FakeLoginService(_ => LoginStatus.Success);
        var runner = NewRunner(login, options: Options());

        await runner.RunAsync(Credential);

        Assert.IsTrue(login.LastSkipNetworkCheck, "静默登录必须跳过重复的网络检测");
    }

    private static SilentLoginRunner NewRunner(
        FakeLoginService login,
        bool online = false,
        StartupOptions? options = null,
        List<int>? delays = null)
    {
        var detector = new FakeNetworkDetector(online);
        return new SilentLoginRunner(
            detector,
            login,
            new RecordingLogService(),
            options ?? Options(),
            (ms, _) =>
            {
                delays?.Add(ms);
                return Task.CompletedTask;   // 测试中不真的等待
            });
    }

    private sealed class FakeLoginService : ILoginService
    {
        private readonly Func<int, LoginStatus> _statusForAttempt;

        public FakeLoginService(Func<int, LoginStatus> statusForAttempt) => _statusForAttempt = statusForAttempt;

        public int CallCount { get; private set; }

        public bool LastSkipNetworkCheck { get; private set; }

        public Task<LoginAttempt> LoginAsync(
            string userName,
            string password,
            OperatorKind @operator,
            bool skipNetworkCheck = false,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSkipNetworkCheck = skipNetworkCheck;

            LoginStatus status = _statusForAttempt(CallCount);

            return Task.FromResult(new LoginAttempt(
                new LoginResult { Status = status, Elapsed = TimeSpan.FromMilliseconds(50) },
                Snapshot: null,
                StartedAt: DateTimeOffset.Now));
        }
    }

    private sealed class FakeNetworkDetector : INetworkDetector
    {
        private readonly bool _online;

        public FakeNetworkDetector(bool online) => _online = online;

        public Task<bool> WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<NetworkSnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            var campus = new ProbeResult("campus", true, TimeSpan.Zero, "", "HTTP");
            var external = new ProbeResult("external", _online, TimeSpan.Zero, "", "ICMP");

            return Task.FromResult(new NetworkSnapshot(
                campus,
                external,
                _online ? AuthRequirement.NotRequired : AuthRequirement.Required,
                DateTimeOffset.Now));
        }
    }
}
