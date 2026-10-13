namespace AutoLoginNet.Auth;

/// <summary>校园网认证客户端。</summary>
public interface IAuthenticator
{
    /// <summary>
    /// 提交一次认证请求。
    /// </summary>
    /// <param name="account">完整账号（已含运营商后缀）。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default);
}
