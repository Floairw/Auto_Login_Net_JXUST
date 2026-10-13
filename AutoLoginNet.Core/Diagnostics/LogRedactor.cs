using System.Text.RegularExpressions;

namespace AutoLoginNet.Diagnostics;

/// <summary>
/// 日志脱敏。密码、口令类字段一律替换为 ***，避免任何凭据进入日志文件。
/// <para>
/// 单条正则即可覆盖表单（<c>key=value</c>）、URL 查询串与 JSON（<c>"key":"value"</c>）三种形态，
/// 保留原始分隔符，因此输出仍然是合法的键值文本，便于阅读与二次排查。
/// </para>
/// </summary>
public static partial class LogRedactor
{
    private const string Mask = "***";

    /// <summary>需要脱敏的字段名（不区分大小写、按整词匹配）。</summary>
    private const string SecretKeys =
        "user_password|password|passwd|pwd|token|access_token|refresh_token|secret|credential";

    /// <summary>对消息做脱敏处理；null 或空返回空字符串。</summary>
    public static string Redact(string? message)
        => string.IsNullOrEmpty(message) ? string.Empty : SecretRegex().Replace(message, MaskValue);

    /// <summary>把匹配到的值替换为 ***，同时原样保留键名与分隔符。</summary>
    private static string MaskValue(Match match)
        => match.Groups["sep"].Value.Length > 0
            ? match.Groups["key"].Value + match.Groups["sep"].Value + Mask
            : match.Groups["key"].Value + Mask;

    // 说明：
    //   key   —— 字段名（允许被 ' 或 " 包裹，JSON 场景）
    //   sep   —— 分隔符 = 或 :，可带空格；不含分隔符的纯单词（如 topsecret）不会命中
    //   value —— 优先匹配带引号的整体，否则匹配到 & , ; 空白或右花括号为止
    [GeneratedRegex(
        $@"(?<key>['""]?(?:\b(?:{SecretKeys})\b)['""]?)(?<sep>\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^&\s,;}}]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();
}
