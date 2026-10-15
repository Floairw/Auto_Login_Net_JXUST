using AutoLoginNet.Credentials;
using System.Security.Cryptography;
using System.Text;

namespace AutoLoginNet.Windows;

/// <summary>
/// 使用 Windows DPAPI（CurrentUser 作用域 + 附加熵）保护密码。
/// 附加熵带来同机同用户下的额外隔离：即使凭据文件被复制到其他机器也无法解密。
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AutoLoginNet.JXUST.DPAPI.Entropy.v2");

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    public string Unprotect(string cipherText)
    {
        ArgumentNullException.ThrowIfNull(cipherText);

        byte[] cipherBytes = Convert.FromBase64String(cipherText);
        byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
