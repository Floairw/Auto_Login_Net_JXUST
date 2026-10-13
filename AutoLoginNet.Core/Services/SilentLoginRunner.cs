using System.Net.Http;
using AutoLoginNet.Auth;
using AutoLoginNet.Configuration;
using AutoLoginNet.Credentials;
using AutoLoginNet.Diagnostics;
using AutoLoginNet.Network;

namespace AutoLoginNet.Services;

/// <summary>静默登录（开机自启）的结果。</summary>
/// <param name="ExitCode">建议的进程退出码。</param>
/// <param name="Message">用于日志的结论说明。</param>
public sealed record SilentLoginOutcome(int ExitCode, string Message)
{
    /// <summary>无凭据，无法登录。</summary>
    public const int NoCredentialExitCode = 1;

    /// <summary>认证失败。</summary>
    public const int AuthFailedExitCode = 2;
}

/// <summary>
/// 开机自启时的静默登录流程。
/// <para>
/// 抽到 Core 层的目的：这段逻辑只在开机那一刻跑一次，出问题很难复现，
/// 因此必须能被单元测试直接覆盖（尤其是重试策略）。
/// </para>
/// </summary>
public sealed class SilentLoginRunner
{
    private readonly INetworkDetector _networkDetector;
    private readonly ILoginService _loginService;
    private readonly ILogService _log;
    private readonly StartupOptions _options;
    private readonly Func<int, CancellationToken, Task> _delay;

    public SilentLoginRunner(
        INetworkDetector networkDetector,
        ILoginService loginService,
        ILogService log,
        StartupOptions options,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _networkDetector = networkDetector ?? throw new ArgumentNullException(nameof(networkDetector));
        _loginService = loginService ?? throw new ArgumentNullException(nameof(loginService));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    /// <summary>本次登录尝试的次数（含首次），供测试断言重试次数。</summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// 执行静默登录。
    /// </summary>
    /// <param name="credential">已保存的凭据；为 null 时直接返回无凭据结果。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<SilentLoginOutcome> RunAsync(
        LoginCredential? credential,
        CancellationToken cancellationToken = default)
    {
        if (credential is null)
        {
            _log.Warn("未找到已保存的凭据，自动登录跳过（请先在界面登录一次）", "自启");
            return new SilentLoginOutcome(SilentLoginOutcome.NoCredentialExitCode, "未找到已保存的凭据");
        }

        await _networkDetector.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);

        NetworkSnapshot snapshot = await _networkDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Requirement == AuthRequirement.NotRequired && snapshot.IsOnline)
        {
            _log.Info("外网已连通，无需重复认证", "自启");
            return new SilentLoginOutcome(0, "外网已连通，无需重复认证");
        }

        // 开机瞬间网络栈与认证服务器都可能尚未就绪，因此对「瞬时故障」做有限次重试。
        // 实测教训：曾经只尝试一次，51 ms 就因认证服务器不可达而失败退出，
        // 结果是用户开机后仍处于未认证状态，且界面没有任何提示。
        int maxRetries = Math.Max(0, _options.LoginRetryCount);
        int retryDelayMs = Math.Max(1, _options.LoginRetryDelaySeconds) * 1000;

        LoginResult? result = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                _log.Info($"第 {attempt}/{maxRetries} 次重试自动登录（等待 {retryDelayMs} ms）", "自启");
                await _delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            AttemptCount++;

            try
            {
                LoginAttempt loginAttempt = await _loginService.LoginAsync(
                    credential.UserName,
                    credential.Password,
                    credential.Operator,
                    skipNetworkCheck: true,
                    cancellationToken).ConfigureAwait(false);

                result = loginAttempt.Result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                // 网络类异常同样属于可重试的瞬时故障
                result = LoginResult.NetworkFailure("请求认证服务器失败", TimeSpan.Zero);
            }

            if (!result.IsTransient)
            {
                break;
            }
        }

        string message =
            $"自动登录结束：{result?.Status}（耗时 {result?.Elapsed.TotalMilliseconds:F0} ms，尝试 {AttemptCount} 次）";
        _log.Log(result?.IsSuccess == true ? LogLevel.Info : LogLevel.Warning, message, "自启");

        return new SilentLoginOutcome(
            result?.IsSuccess == true ? 0 : SilentLoginOutcome.AuthFailedExitCode,
            message);
    }
}
