using System.Net;
using System.Net.Http;
using System.Text;
using AutoLoginNet.Auth;
using AutoLoginNet.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

/// <summary>
/// 认证客户端测试。核心断言是「密码绝不出现在 URL 中」——
/// 这是相对旧实现（GET 查询串携带密码）最关键的安全性修正。
/// </summary>
[TestClass]
public sealed class DrComAuthenticatorTests
{
    [TestMethod]
    public async Task LoginAsync_PutsCredentialsInQueryString_BecausePortalOnlyReadsQuery()
    {
        // 这条断言固化的是「实测得出的服务器约定」，不是我们的偏好：
        // 把账号放进 POST body 时，门户返回 {"result":0,"msg":"无法获取用户认证账号！"}，
        // 说明 /eportal/portal/login 完全不解析 body。若将来门户升级支持 body，
        // 应改回 body 并同步修改本测试。
        Uri? seenUri = null;
        string? seenBody = null;

        var handler = new StubHandler(async request =>
        {
            seenUri = request.RequestUri;
            seenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("dr1003({\"result\":\"1\",\"msg\":\"登录成功\"})", Encoding.UTF8),
            };
        });

        var options = new AppOptions();
        options.Normalize();

        using var http = new HttpClient(handler);
        var authenticator = new DrComAuthenticator(options, http, new RecordingLogService());

        LoginResult result = await authenticator.LoginAsync("2024001@cmcc", "p@ss w0rd&x", CancellationToken.None);

        Assert.AreEqual(LoginStatus.Success, result.Status);
        Assert.IsNotNull(seenUri);

        // 断言用 AbsoluteUri：这才是真正发到网络上的转义形式。
        // Uri.ToString() 会把 %20 还原成空格，容易误判。
        string urlText = seenUri!.AbsoluteUri;

        // 账号与密码都必须出现在查询串中，且经过 URL 编码
        Assert.IsTrue(urlText.Contains("user_account=2024001%40cmcc", StringComparison.OrdinalIgnoreCase),
            $"账号应在查询串中：{urlText}");
        Assert.IsTrue(
            urlText.Contains("user_password=p%40ss%20w0rd%26x", StringComparison.OrdinalIgnoreCase)
            || urlText.Contains("user_password=p%40ss+w0rd%26x", StringComparison.OrdinalIgnoreCase),
            $"密码应在查询串中并被编码：{urlText}");

        // 其余门户参数也要带上，否则服务器同样会拒绝
        Assert.IsTrue(urlText.Contains("callback=dr1003", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(urlText.Contains("login_method=1", StringComparison.OrdinalIgnoreCase));

        // body 应为空：凭据不再重复放在正文里
        Assert.IsTrue(string.IsNullOrEmpty(seenBody), $"请求正文应为空，实际：{seenBody}");
    }

    [TestMethod]
    public void BuildLoginQuery_EncodesSeparatorsAndSpecialCharacters()
    {
        var options = new AppOptions();
        options.Normalize();

        string query = options.BuildLoginQuery("2024001@cmcc", "a&b=c d#e");

        // 特殊字符必须编码，否则会被服务器当成参数分隔符
        Assert.IsTrue(query.Contains("user_account=2024001%40cmcc", StringComparison.Ordinal));
        Assert.IsTrue(query.Contains("user_password=a%26b%3Dc%20d%23e", StringComparison.Ordinal),
            $"密码需完整编码：{query}");
        Assert.AreEqual(1, query.Split("user_password=").Length - 1, "密码参数只应出现一次");
    }

    [TestMethod]
    public async Task LoginAsync_AlreadyOnlineResponse_IsTreatedAsSuccess()
    {
        // 本校园网的真实返回：result=0 但 ret_code=2 且消息为「IP 已经在线」
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "dr1003({\"result\":0,\"msg\":\"IP: 10.22.187.75 已经在线！\",\"ret_code\":\"2\"});",
                Encoding.UTF8),
        }));

        var options = new AppOptions();
        options.Normalize();

        using var http = new HttpClient(handler);
        var authenticator = new DrComAuthenticator(options, http, new RecordingLogService());

        LoginResult result = await authenticator.LoginAsync("2024001@telecom", "pw", CancellationToken.None);

        Assert.AreEqual(LoginStatus.AlreadyOnline, result.Status);
        Assert.IsTrue(result.IsSuccess, "IP 已在线不应被当成登录失败");
    }

    [TestMethod]
    public async Task LoginAsync_MissingAccountResponse_IsReportedAsServerRejection()
    {
        // 之前登录失败时服务器给出的真实响应
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "dr1003({\"result\":0,\"msg\":\"无法获取用户认证账号！\",\"ret_code\":\"\"});",
                Encoding.UTF8),
        }));

        var options = new AppOptions();
        options.Normalize();

        using var http = new HttpClient(handler);
        var authenticator = new DrComAuthenticator(options, http, new RecordingLogService());

        LoginResult result = await authenticator.LoginAsync("2024001", "pw", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.Title.Contains("失败", StringComparison.Ordinal) || result.Title.Contains("错误", StringComparison.Ordinal),
            $"应给出明确的失败标题，实际：{result.Title}");
        Assert.IsTrue(result.Detail.Contains("无法获取用户认证账号", StringComparison.Ordinal), "应原样展示服务器消息便于排查");
    }

    [TestMethod]
    public async Task LoginAsync_PostsToConfiguredEndpoint()
    {
        Uri? seenUri = null;
        var handler = new StubHandler(request =>
        {
            seenUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("dr1003({\"result\":\"1\",\"msg\":\"ok\"})"),
            });
        });

        var options = new AppOptions();
        options.Auth.Host = "10.0.0.9";
        options.Auth.Port = 9999;
        options.Auth.LoginPath = "/eportal/portal/login";
        options.Normalize();

        using var http = new HttpClient(handler);
        var authenticator = new DrComAuthenticator(options, http, new RecordingLogService());
        await authenticator.LoginAsync("u", "p", CancellationToken.None);

        Assert.AreEqual("10.0.0.9", seenUri!.Host);
        Assert.AreEqual(9999, seenUri.Port);
        Assert.AreEqual("/eportal/portal/login", seenUri.AbsolutePath);
    }

    [TestMethod]
    public async Task LoginAsync_HttpFailure_ReturnsNetworkError()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("目标主机不可达"));
        using var http = new HttpClient(handler);
        var options = new AppOptions();
        options.Normalize();

        var authenticator = new DrComAuthenticator(options, http, new RecordingLogService());
        LoginResult result = await authenticator.LoginAsync("u", "p", CancellationToken.None);

        Assert.AreEqual(LoginStatus.NetworkError, result.Status);
        Assert.IsTrue(result.RawMessage.Contains("不可达", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoginAsync_EmptyPassword_IsRejectedWithoutHttpCall()
    {
        bool called = false;
        var handler = new StubHandler(_ =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var http = new HttpClient(handler);
        var options = new AppOptions();
        options.Normalize();
        var authenticator = new DrComAuthenticator(options, http, new RecordingLogService());

        LoginResult result = await authenticator.LoginAsync("u", string.Empty, CancellationToken.None);

        Assert.AreEqual(LoginStatus.InvalidCredentials, result.Status);
        Assert.IsFalse(called, "空密码时不应发起请求");
    }

    [TestMethod]
    public async Task LoginAsync_DoesNotLogPlainPassword()
    {
        var log = new RecordingLogService();
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("dr1003({\"result\":\"1\",\"msg\":\"ok\"})"),
        }));

        using var http = new HttpClient(handler);
        var options = new AppOptions();
        options.Normalize();

        await new DrComAuthenticator(options, http, log).LoginAsync("2024001", "SuperSecret123", CancellationToken.None);

        Assert.IsFalse(
            log.Entries.Any(e => e.Message.Contains("SuperSecret123", StringComparison.Ordinal)),
            "明文密码绝不能出现在日志中");
    }

    /// <summary>可注入响应的 HttpClient 处理器。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}
