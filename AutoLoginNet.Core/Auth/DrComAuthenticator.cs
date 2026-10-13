using System.Diagnostics;
using System.Net.Http;
using AutoLoginNet.Configuration;
using AutoLoginNet.Diagnostics;

namespace AutoLoginNet.Auth;

/// <summary>
/// Dr.COM ePortal 认证实现。
/// <para>
/// <b>实测得出的调用约定</b>：本校园网门户的 <c>/eportal/portal/login</c> 只从
/// <b>查询串</b>读取账号与密码，完全不解析 POST body。曾经把凭据放进 POST body，
/// 服务器会直接返回 <c>{"result":0,"msg":"无法获取用户认证账号！"}</c>——这正是之前
/// 登录失败的原因。因此现在账号与密码都编码进查询串（见
/// <see cref="Configuration.AppOptions.BuildLoginQuery"/>），密码的泄漏面由日志脱敏收敛。
/// </para>
/// </summary>
public sealed class DrComAuthenticator : IAuthenticator
{
    private readonly AppOptions _options;
    private readonly HttpClient _http;
    private readonly ILogService _log;

    public DrComAuthenticator(AppOptions options, HttpClient http, ILogService log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<LoginResult> LoginAsync(
        string account,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
        {
            return new LoginResult
            {
                Status = LoginStatus.InvalidCredentials,
                RawMessage = "账号或密码为空",
            };
        }

        Uri endpoint = _options.BuildLoginUri();
        string query = _options.BuildLoginQuery(account, password);

        // 直接拼接而不是用 UriBuilder.Query：后者会对已编码内容再次转义，
        // 把 %20 变成裸空格，从而破坏参数值（曾被单测抓出）。
        var uri = new Uri($"{endpoint.GetLeftPart(UriPartial.Path)}?{query}");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 只记录脱敏后的账号，绝不记录完整 URL（其中含密码）
            _log.Info($"向 {endpoint.Host}:{endpoint.Port} 提交认证请求（账号 {Redact(account)}）", "登录");

            // 用 POST + 空 body：凭据全部在查询串中，这是门户唯一读取的位置
            using var content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
            using var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            var result = DrComResponseParser.Parse(body, (int)response.StatusCode) with
            {
                Elapsed = stopwatch.Elapsed,
            };

            _log.Log(
                result.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                $"认证结果：{result.Status}（HTTP {(int)response.StatusCode}，耗时 {stopwatch.ElapsedMilliseconds} ms）",
                "登录");

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return LoginResult.NetworkFailure("操作已取消", stopwatch.Elapsed);
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            _log.Warn($"认证请求超时（{_options.Auth.HttpTimeoutSeconds} 秒）", "登录");
            return LoginResult.NetworkFailure(
                $"请求超时：认证服务器 {endpoint.Host}:{endpoint.Port} 在 {_options.Auth.HttpTimeoutSeconds} 秒内未响应，请确认已连接校园网。",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _log.Error($"认证请求失败：{ex.Message}", "登录");
            return LoginResult.NetworkFailure(
                $"无法连接认证服务器 {endpoint.Host}:{endpoint.Port}：{ex.Message}",
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Error($"认证过程出现未预期异常：{ex.GetType().Name}: {ex.Message}", "登录");
            return LoginResult.NetworkFailure($"{ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed);
        }
    }

    /// <summary>日志中的账号只保留前 2 位与后 1 位，避免完整学号进入日志。</summary>
    private static string Redact(string account)
    {
        if (account.Length <= 4)
        {
            return "***";
        }

        int suffixIndex = account.IndexOf('@', StringComparison.Ordinal);
        string suffix = suffixIndex >= 0 ? account[suffixIndex..] : string.Empty;
        string name = suffixIndex >= 0 ? account[..suffixIndex] : account;

        if (name.Length <= 3)
        {
            return "***" + suffix;
        }

        return string.Concat(name.AsSpan(0, 2), "***", name.AsSpan(name.Length - 1), suffix);
    }
}
