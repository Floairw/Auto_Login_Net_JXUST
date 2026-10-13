using AutoLoginNet.Auth;

namespace AutoLoginNet.Credentials;

/// <summary>已解密、可直接用于登录的凭据。</summary>
/// <param name="UserName">账号。</param>
/// <param name="Password">明文密码（仅存在于内存）。</param>
/// <param name="Operator">运营商。</param>
/// <param name="SavedAt">保存时间。</param>
public sealed record LoginCredential(string UserName, string Password, OperatorKind Operator, DateTimeOffset SavedAt)
{
    /// <summary>提交给认证服务器时使用的账号（含运营商后缀）。</summary>
    public string Account => Operator.ApplySuffix(UserName);
}

/// <summary>凭据的持久化读写。</summary>
public interface ICredentialStore
{
    /// <summary>凭据文件路径。</summary>
    string FilePath { get; }

    /// <summary>磁盘上是否存在凭据文件。</summary>
    bool Exists { get; }

    /// <summary>加载并解密凭据；不存在、格式不受支持或解密失败时返回 null。</summary>
    LoginCredential? Load();

    /// <summary>保存凭据（密码加密后落盘）。</summary>
    void Save(string userName, string password, OperatorKind @operator);

    /// <summary>删除凭据。</summary>
    void Clear();
}
