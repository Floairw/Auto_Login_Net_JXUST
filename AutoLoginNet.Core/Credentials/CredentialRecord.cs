using AutoLoginNet.Auth;

namespace AutoLoginNet.Credentials;

/// <summary>
/// 持久化到磁盘的凭据记录。<see cref="Password"/> 字段存放的是
/// 经 <see cref="ICredentialProtector"/> 加密后的密文，磁盘上不存在明文密码。
/// </summary>
public sealed class CredentialRecord
{
    /// <summary>当前凭据格式版本。</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 格式版本。历史文件缺少该字段时为 0，加载时会被判定为不受支持并要求重新登录。
    /// 注意：不要给此属性设置等于 <see cref="CurrentSchemaVersion"/> 的初始值。
    /// </summary>
    public int SchemaVersion { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>加密后的密码（Base64 密文）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>运营商（登录方式）。</summary>
    public OperatorKind Operator { get; set; } = OperatorKind.Campus;

    /// <summary>凭据保存时间。</summary>
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}
