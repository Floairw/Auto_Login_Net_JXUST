using AutoLoginNet.Configuration;
using AutoLoginNet.Diagnostics;
using System.Diagnostics;

namespace AutoLoginNet.Auth;

/// <summary>校园网注销（断开在线认证会话）。</summary>
public interface ILogoutService
{
    /// <summary>断开当前在线认证。成功后会失去外网访问能力。</summary>
    Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Dr.COM 门户注销实现。
/// <para>
/// 端点与参数取自门户页面自己声明的配置：
/// <c>authlogoutpath='/eportal/?c=ACSetting&amp;a=Logout&amp;ver=1.0'</c>。
/// 与登录接口不同，注销返回的是 HTML（含 <c>Dr.COMWebLoginID_n.htm</c> 结果标记）。
/// </para>
/// </summary>
public sealed class DrComLogoutService : ILogoutService
{
    private readonly AppOptions _options;
    private readonly HttpClient _http;
    private readonly ILogService _log;

    public DrComLogoutService(AppOptions options, HttpClient http, ILogService log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>注销请求的完整地址。</summary>
    public Uri BuildLogoutUri() => new($"http://{_options.Auth.Host}:{_options.Auth.Port}{LogoutPath}");

    /// <summary>门户声明的注销路径。</summary>
    public const string LogoutPath = "/eportal/?c=ACSetting&a=Logout&ver=1.0";

    public async Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        Uri uri = BuildLogoutUri();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _log.Info($"向 {uri.Host}:{uri.Port} 提交注销请求", "注销");

            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            LogoutResult result = DrComLogoutResponseParser.Parse(body, (int)response.StatusCode, stopwatch.Elapsed);

            _log.Log(
                result.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                $"注销结果：{result.Status}（HTTP {(int)response.StatusCode}，耗时 {stopwatch.ElapsedMilliseconds} ms）",
                "注销");

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return LogoutResult.NetworkFailure("操作已取消", stopwatch.Elapsed);
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            _log.Warn("注销请求超时", "注销");
            return LogoutResult.NetworkFailure(
                $"请求超时：认证服务器 {uri.Host}:{uri.Port} 未响应，请确认已连接校园网。",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _log.Error($"注销请求失败：{ex.Message}", "注销");
            return LogoutResult.NetworkFailure($"无法连接认证服务器 {uri.Host}:{uri.Port}：{ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Error($"注销过程出现未预期异常：{ex.GetType().Name}: {ex.Message}", "注销");
            return LogoutResult.NetworkFailure($"{ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed);
        }
    }
}
