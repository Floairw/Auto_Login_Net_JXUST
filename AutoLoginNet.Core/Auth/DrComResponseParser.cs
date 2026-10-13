using System.Text.Json;

namespace AutoLoginNet.Auth;

/// <summary>
/// 解析 Dr.COM ePortal 登录接口返回的 JSONP 响应（形如 <c>dr1003({...})</c>），
/// 并映射为结构化结果。纯静态实现，便于单元测试。
/// </summary>
public static class DrComResponseParser
{
    // 已知的业务返回码
    private const string CodeSuccess = "1";
    private const string CodeAuthFailed = "2";
    private const string CodeBillingBlocked = "3";
    private const string CodeAlreadyOnline = "9";

    /// <summary>ret_code=2：该 IP 已在线上。</summary>
    private const string AlreadyOnlineReturnCode = "2";

    /// <summary>解析完整响应体。</summary>
    public static LoginResult Parse(string? body, int httpStatusCode = 200)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new LoginResult
            {
                Status = LoginStatus.Rejected,
                RawMessage = "认证服务器返回了空响应",
                HttpStatusCode = httpStatusCode,
            };
        }

        if (!TryParseJsonp(body, out string? code, out string message, out string returnCode))
        {
            return new LoginResult
            {
                Status = LoginStatus.Rejected,
                RawMessage = "无法解析认证服务器响应",
                RawResponse = body,
                HttpStatusCode = httpStatusCode,
            };
        }

        return new LoginResult
        {
            Status = MapStatus(code, message, returnCode),
            ServerMessage = message,
            RawResponse = body,
            HttpStatusCode = httpStatusCode,
        };
    }

    /// <summary>
    /// 由返回码与消息文本判定结果分类（纯函数，便于测试）。
    /// <para>
    /// 本校园网的 ePortal 会把「IP 已在线」表达为 <c>result=0</c> + <c>ret_code=2</c>，
    /// 因此不能只看 result：必须结合 ret_code 与消息文本判断。
    /// </para>
    /// </summary>
    public static LoginStatus MapStatus(string? code, string? message, string? returnCode = null)
    {
        string normalizedCode = code?.Trim() ?? string.Empty;
        string text = message ?? string.Empty;
        string retCode = returnCode?.Trim() ?? string.Empty;

        // 文本判定优先于返回码：部分门户在成功时也会附带异常文案
        if (ContainsAny(text, "已经在线", "已在线", "已经登录", "已登录", "重复登录"))
        {
            return LoginStatus.AlreadyOnline;
        }

        if (normalizedCode == CodeSuccess)
        {
            return LoginStatus.Success;
        }

        // ret_code=2 表示该 IP 已在线上（无需重复认证）
        if (retCode == AlreadyOnlineReturnCode)
        {
            return LoginStatus.AlreadyOnline;
        }

        if (ContainsAny(text, "欠费", "停机", "余额不足", "余额", "禁用", "停用", "销户", "封停", "未开通"))
        {
            return LoginStatus.AccountSuspended;
        }

        if (normalizedCode == CodeBillingBlocked)
        {
            return LoginStatus.AccountSuspended;
        }

        if (ContainsAny(text, "密码", "账号", "账户", "用户名", "用户不存在", "认证失败"))
        {
            return LoginStatus.InvalidCredentials;
        }

        if (normalizedCode == CodeAuthFailed)
        {
            return LoginStatus.InvalidCredentials;
        }

        if (normalizedCode == CodeAlreadyOnline)
        {
            return LoginStatus.AlreadyOnline;
        }

        return LoginStatus.Rejected;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (string keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>剥离 JSONP 外壳并取出 result / msg / ret_code 字段。</summary>
    private static bool TryParseJsonp(string body, out string? code, out string message, out string returnCode)
    {
        code = null;
        message = string.Empty;
        returnCode = string.Empty;

        string json = body.Trim();
        int open = json.IndexOf('(');
        int close = json.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            json = json[(open + 1)..close];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;

            if (root.TryGetProperty("result", out var resultElement))
            {
                code = resultElement.ValueKind == JsonValueKind.String
                    ? resultElement.GetString()
                    : resultElement.ToString();
            }

            if (root.TryGetProperty("msg", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("ret_code", out var returnCodeElement)
                && returnCodeElement.ValueKind == JsonValueKind.String)
            {
                returnCode = returnCodeElement.GetString() ?? string.Empty;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
