namespace AutoLoginNet.Auth;

/// <summary>校园网登录方式（运营商）。</summary>
public enum OperatorKind
{
    /// <summary>校园网（无运营商后缀）。</summary>
    Campus = 0,

    /// <summary>中国电信。</summary>
    Telecom = 1,

    /// <summary>中国移动。</summary>
    Mobile = 2,

    /// <summary>中国联通。</summary>
    Unicom = 3,
}

/// <summary>运营商的展示名与账号后缀规则。</summary>
public static class Operators
{
    /// <summary>全部登录方式，顺序即界面展示顺序。</summary>
    public static readonly IReadOnlyList<OperatorKind> All = new[]
    {
        OperatorKind.Campus,
        OperatorKind.Telecom,
        OperatorKind.Mobile,
        OperatorKind.Unicom,
    };

    /// <summary>界面展示名称。</summary>
    public static string ToDisplayName(this OperatorKind kind) => kind switch
    {
        OperatorKind.Telecom => "中国电信",
        OperatorKind.Mobile => "中国移动",
        OperatorKind.Unicom => "中国联通",
        _ => "校园网",
    };

    /// <summary>账号后缀，校园网为空字符串。</summary>
    public static string ToSuffix(this OperatorKind kind) => kind switch
    {
        OperatorKind.Telecom => "@telecom",
        OperatorKind.Mobile => "@cmcc",
        OperatorKind.Unicom => "@unicom",
        _ => string.Empty,
    };

    /// <summary>把账号与运营商后缀拼接为服务器可识别的完整账号。</summary>
    public static string ApplySuffix(this OperatorKind kind, string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);

        string trimmed = userName.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // 用户自行填写了后缀时不再重复拼接
        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return trimmed + kind.ToSuffix();
    }

    /// <summary>从完整账号中反解出运营商；无法识别时返回校园网。</summary>
    public static OperatorKind FromAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return OperatorKind.Campus;
        }

        return account.Trim().ToLowerInvariant() switch
        {
            var a when a.EndsWith("@telecom", StringComparison.Ordinal) => OperatorKind.Telecom,
            var a when a.EndsWith("@cmcc", StringComparison.Ordinal) => OperatorKind.Mobile,
            var a when a.EndsWith("@unicom", StringComparison.Ordinal) => OperatorKind.Unicom,
            _ => OperatorKind.Campus,
        };
    }
}
