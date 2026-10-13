namespace AutoLoginNet.Auth;

/// <summary>登录结果分类。</summary>
public enum LoginStatus
{
    /// <summary>登录成功。</summary>
    Success,

    /// <summary>账号已在线，无需重复登录。</summary>
    AlreadyOnline,

    /// <summary>账号或密码错误。</summary>
    InvalidCredentials,

    /// <summary>账号欠费、停机或被停用。</summary>
    AccountSuspended,

    /// <summary>认证服务器返回了无法识别的失败。</summary>
    Rejected,

    /// <summary>网络异常：超时、连接失败等。</summary>
    NetworkError,
}

/// <summary>登录结果。</summary>
public sealed record LoginResult
{
    /// <summary>结果分类。</summary>
    public required LoginStatus Status { get; init; }

    /// <summary>服务器返回的业务消息（脱敏后用于展示）。</summary>
    public string ServerMessage { get; init; } = string.Empty;

    /// <summary>排查用的补充说明（异常类型、超时等）。</summary>
    public string RawMessage { get; init; } = string.Empty;

    /// <summary>服务器原始响应体，便于界面展示细节。</summary>
    public string RawResponse { get; init; } = string.Empty;

    /// <summary>HTTP 状态码，未发出请求时为 0。</summary>
    public int HttpStatusCode { get; init; }

    /// <summary>本次请求耗时。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>是否视为登录成功（成功或已在线）。</summary>
    public bool IsSuccess => Status is LoginStatus.Success or LoginStatus.AlreadyOnline;

    /// <summary>是否为可重试的瞬时故障。</summary>
    public bool IsTransient => Status is LoginStatus.NetworkError;

    /// <summary>界面标题。</summary>
    public string Title => Status switch
    {
        LoginStatus.Success => "登录成功",
        LoginStatus.AlreadyOnline => "当前账号已在线",
        LoginStatus.InvalidCredentials => "账号或密码错误",
        LoginStatus.AccountSuspended => "账号不可用",
        LoginStatus.NetworkError => "网络异常",
        _ => "登录失败",
    };

    /// <summary>界面正文：优先展示服务器消息，其次展示排查说明。</summary>
    public string Detail
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ServerMessage))
            {
                return ServerMessage;
            }
            return string.IsNullOrWhiteSpace(RawMessage) ? string.Empty : RawMessage;
        }
    }

    public static LoginResult NetworkFailure(string message, TimeSpan elapsed, string raw = "")
        => new()
        {
            Status = LoginStatus.NetworkError,
            RawMessage = message,
            RawResponse = raw,
            Elapsed = elapsed,
        };
}
