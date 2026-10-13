using System.Text.RegularExpressions;

namespace AutoLoginNet.Auth;

/// <summary>注销（下线）结果分类。</summary>
public enum LogoutStatus
{
    /// <summary>已成功注销，当前不再处于认证在线状态。</summary>
    Success,

    /// <summary>本机本来就不在线，无需注销。</summary>
    NotOnline,

    /// <summary>服务器拒绝了注销请求。</summary>
    Rejected,

    /// <summary>网络异常：超时、连不上认证服务器。</summary>
    NetworkError,
}

/// <summary>注销结果。</summary>
/// <param name="Status">结果分类。</param>
/// <param name="ServerMessage">服务器返回的消息（已去除 HTML 标签）。</param>
/// <param name="RawMessage">排查用的补充说明。</param>
/// <param name="RawResponse">服务器原始响应体。</param>
/// <param name="Elapsed">请求耗时。</param>
public sealed record LogoutResult(
    LogoutStatus Status,
    string ServerMessage,
    string RawMessage,
    string RawResponse,
    TimeSpan Elapsed)
{
    /// <summary>是否成功断开认证。</summary>
    public bool IsSuccess => Status is LogoutStatus.Success or LogoutStatus.NotOnline;

    /// <summary>界面标题。</summary>
    public string Title => Status switch
    {
        LogoutStatus.Success => "已断开校园网认证",
        LogoutStatus.NotOnline => "当前未处于认证在线状态",
        LogoutStatus.NetworkError => "网络异常",
        _ => "注销失败",
    };

    /// <summary>界面正文。</summary>
    public string Detail => !string.IsNullOrWhiteSpace(ServerMessage) ? ServerMessage : RawMessage;

    public static LogoutResult NetworkFailure(string message, TimeSpan elapsed, string raw = "")
        => new(LogoutStatus.NetworkError, string.Empty, message, raw, elapsed);
}

/// <summary>
/// 解析 Dr.COM 门户的注销响应。
/// <para>
/// 该门户的注销返回 <b>HTML</b>。实测（在线注销 vs 离线注销各执行一次）得到：
/// </para>
/// <list type="table">
///   <item><description>真正的注销成功：<c>Logout succeed.</c></description></item>
///   <item><description>本就不在线：<c>Logout failed.</c></description></item>
/// </list>
/// <para>
/// <b>关键结论：<c>Dr.COMWebLoginID_n.htm</c> 标记对注销没有区分能力</b>——上述两种情况的
/// 标记都是 <c>_2</c>，所以它不能用来判定成败（门户配置里的
/// <c>authsuccess/_3</c>、<c>authfail/_2</c> 是针对登录页的，不适用于注销）。
/// 因此以正文的 succeed/failed 文案为准，标记仅作为文案缺失时的兜底。
/// </para>
/// </summary>
public static partial class DrComLogoutResponseParser
{
    /// <summary><c>Dr.COMWebLoginID_2.htm</c>。</summary>
    private const string MarkerCodeTwo = "2";

    /// <summary><c>Dr.COMWebLoginID_3.htm</c>。</summary>
    private const string MarkerCodeThree = "3";

    /// <summary>解析注销响应体。</summary>
    public static LogoutResult Parse(string? body, int httpStatusCode = 200, TimeSpan elapsed = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new LogoutResult(
                LogoutStatus.Rejected,
                string.Empty,
                "认证服务器返回了空响应",
                string.Empty,
                elapsed);
        }

        string message = ExtractMessage(body);

        // 1) JSONP 形态（部分门户版本用 JSON 返回注销结果）
        if (body.Contains("result", StringComparison.OrdinalIgnoreCase)
            && body.Contains('(', StringComparison.Ordinal)
            && body.Contains(')', StringComparison.Ordinal))
        {
            LoginResult loginLike = DrComResponseParser.Parse(body, httpStatusCode);
            LogoutStatus jsonpStatus = loginLike.Status switch
            {
                LoginStatus.Success => LogoutStatus.Success,
                LoginStatus.NetworkError => LogoutStatus.NetworkError,
                _ => LogoutStatus.Rejected,
            };

            return new LogoutResult(jsonpStatus, loginLike.ServerMessage, loginLike.RawMessage, body, elapsed);
        }

        // 2) 正文文案优先：这是唯一被实测证明能区分成败的信号
        LogoutStatus? fromText = ClassifyByBodyText(body);
        Match marker = MarkerRegex().Match(body);
        string markerCode = marker.Success ? marker.Groups["code"].Value : string.Empty;

        if (fromText is { } textStatus)
        {
            string detail = marker.Success
                ? $"正文判定；响应标记 Dr.COMWebLoginID_{markerCode}.htm"
                : "正文判定";

            return new LogoutResult(textStatus, message, detail, body, elapsed);
        }

        // 3) 文案缺失时退回标记（_3 视为成功；_2 语义不明确，保守判为失败）
        if (marker.Success)
        {
            LogoutStatus markerStatus = markerCode == MarkerCodeThree
                ? LogoutStatus.Success
                : LogoutStatus.Rejected;

            return new LogoutResult(markerStatus, message, $"响应标记 Dr.COMWebLoginID_{markerCode}.htm", body, elapsed);
        }

        return new LogoutResult(LogoutStatus.Rejected, message, "无法识别的注销响应", body, elapsed);
    }

    /// <summary>
    /// 依据正文文案判定注销结果。
    /// 注意判定顺序：先排除 failed，再判断 succeed，避免
    /// <c>Logout failed.</c> 里包含的子串造成误判。
    /// </summary>
    private static LogoutStatus? ClassifyByBodyText(string body)
    {
        if (ContainsAny(body, "Logout failed", "注销失败", "logoutfail"))
        {
            // 明确失败，但如果文案说明是「本来就不在线」，按已注销处理
            return ContainsAny(body, "不在线", "未登录", "没有登录", "not online", "not logged")
                ? LogoutStatus.NotOnline
                : LogoutStatus.Rejected;
        }

        if (ContainsAny(body, "不在线", "未登录", "没有登录", "not online", "not logged"))
        {
            return LogoutStatus.NotOnline;
        }

        if (ContainsAny(body, "Logout succeed", "注销成功", "logoutsuccess"))
        {
            return LogoutStatus.Success;
        }

        return null;
    }

    /// <summary>去除 HTML 标签与注释，得到可展示的一行文本（同时保留原始标记信息供诊断）。</summary>
    internal static string ExtractMessage(string body)
    {
        string text = TagRegex().Replace(body, " ");
        text = CommentRegex().Replace(text, " ");

        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static bool ContainsAny(string text, params string[] keywords)
        => keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"Dr\.COM(?:1\.0)?WebLoginID_(?<code>\w+)\.htm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
