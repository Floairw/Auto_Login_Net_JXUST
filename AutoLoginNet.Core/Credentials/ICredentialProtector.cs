namespace AutoLoginNet.Credentials;

/// <summary>
/// 密码保护器：把明文密码转换为可落盘的密文，以及反向还原。
/// Core 层只依赖该抽象；Windows 平台实现使用 DPAPI，测试实现为恒等变换。
/// </summary>
public interface ICredentialProtector
{
    /// <summary>明文 → 可存储的密文。</summary>
    string Protect(string plainText);

    /// <summary>密文 → 明文；密文损坏时抛 <see cref="System.Security.Cryptography.CryptographicException"/>。</summary>
    string Unprotect(string cipherText);
}
