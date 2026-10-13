using AutoLoginNet.Auth;
using AutoLoginNet.Credentials;
using AutoLoginNet.Diagnostics;
using AutoLoginNet.Network;

namespace AutoLoginNet.Services;

/// <summary>一次登录尝试的完整记录。</summary>
/// <param name="Result">认证服务器返回结果。</param>
/// <param name="Snapshot">登录前的网络环境快照；跳过检测时为 null。</param>
/// <param name="StartedAt">开始时间。</param>
public sealed record LoginAttempt(LoginResult Result, NetworkSnapshot? Snapshot, DateTimeOffset StartedAt)
{
    /// <summary>本次尝试总耗时（含网络检测）。</summary>
    public TimeSpan TotalElapsed => DateTimeOffset.Now - StartedAt;
}

/// <summary>登录编排：网络检测 → 认证 → 按需保存凭据。</summary>
public interface ILoginService
{
    /// <summary>
    /// 执行一次端到端登录。
    /// </summary>
    /// <param name="userName">账号（不含后缀）。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="operator">运营商。</param>
    /// <param name="skipNetworkCheck">为 true 时跳过网络环境检测，直接认证。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<LoginAttempt> LoginAsync(
        string userName,
        string password,
        OperatorKind @operator,
        bool skipNetworkCheck = false,
        CancellationToken cancellationToken = default);
}

/// <summary>默认登录编排实现。</summary>
public sealed class LoginService : ILoginService
{
    private readonly IAuthenticator _authenticator;
    private readonly INetworkDetector _networkDetector;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogService _log;

    public LoginService(
        IAuthenticator authenticator,
        INetworkDetector networkDetector,
        ICredentialStore credentialStore,
        ILogService log)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _networkDetector = networkDetector ?? throw new ArgumentNullException(nameof(networkDetector));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<LoginAttempt> LoginAsync(
        string userName,
        string password,
        OperatorKind @operator,
        bool skipNetworkCheck = false,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        NetworkSnapshot? snapshot = null;

        if (!skipNetworkCheck)
        {
            snapshot = await _networkDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        }

        LoginResult result = await _authenticator
            .LoginAsync(@operator.ApplySuffix(userName), password, cancellationToken)
            .ConfigureAwait(false);

        // 认证成功后立即落盘，保证下次开机自启可直接登录
        if (result.IsSuccess)
        {
            _credentialStore.Save(userName, password, @operator);
        }

        return new LoginAttempt(result, snapshot, startedAt);
    }
}
