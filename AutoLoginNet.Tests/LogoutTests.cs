using AutoLoginNet.Auth;
using AutoLoginNet.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Text;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class DrComLogoutResponseParserTests
{
    /// <summary>门户注销成功的真实响应（实测：在线状态下注销）。</summary>
    private const string SuccessHtml =
        "<html><head> <script language=\"javascript\"><!--Dr.COMWebLoginID_2.htm--> </script></head> <body> Logout succeed. </body> </html>";

    /// <summary>门户注销失败的真实响应（实测：本就不在线时注销）。</summary>
    private const string FailureHtml =
        "<html><head> <script language=\"javascript\"><!--Dr.COMWebLoginID_2.htm--> </script></head> <body> Logout failed. </body> </html>";

    [TestMethod]
    public void Parse_SucceedText_IsSuccess()
    {
        LogoutResult result = DrComLogoutResponseParser.Parse(SuccessHtml);

        Assert.AreEqual(LogoutStatus.Success, result.Status);
        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public void Parse_SameMarkerButFailedText_IsRejected()
    {
        // 关键回归防护（来自实测）：成功与失败的响应标记**都是 _2**，
        // 标记对注销没有区分能力。若改回以标记判定，这两条会一起判错。
        Assert.AreEqual(LogoutStatus.Success, DrComLogoutResponseParser.Parse(SuccessHtml).Status);
        Assert.AreEqual(LogoutStatus.Rejected, DrComLogoutResponseParser.Parse(FailureHtml).Status);
    }

    [TestMethod]
    public void Parse_FailedText_IsNotMistakenForSuccess()
    {
        // 「Logout failed.」若判定顺序写反（先判断包含 "Logout" 或后判 failed）会被当成成功
        LogoutResult result = DrComLogoutResponseParser.Parse(FailureHtml);

        Assert.AreNotEqual(LogoutStatus.Success, result.Status);
    }

    [TestMethod]
    public void Parse_ToleratesDrCom1PrefixVariant()
    {
        // 门户页面注释里提到过 Dr.COM → Dr.COM1.0 的改名
        LogoutResult result = DrComLogoutResponseParser.Parse(
            "<!--Dr.COM1.0WebLoginID_3.htm--> Logout succeed.");

        Assert.AreEqual(LogoutStatus.Success, result.Status);
    }

    [TestMethod]
    public void Parse_HtmlWithoutTextButWithSuccessMarker_FallsBackToMarker()
    {
        // 文案缺失时才退回标记：_3 视为成功
        LogoutResult result = DrComLogoutResponseParser.Parse("<!--Dr.COMWebLoginID_3.htm-->");

        Assert.AreEqual(LogoutStatus.Success, result.Status);
    }

    [TestMethod]
    public void Parse_NotOnlineWording_IsTreatedAsAlreadyLoggedOut()
    {
        LogoutResult result = DrComLogoutResponseParser.Parse("当前不在线");

        Assert.AreEqual(LogoutStatus.NotOnline, result.Status);
        Assert.IsTrue(result.IsSuccess, "本来就不在线不应显示为失败");
    }

    [TestMethod]
    public void Parse_JsonpLogoutResponse_IsHandled()
    {
        LogoutResult result = DrComLogoutResponseParser.Parse(
            "dr1003({\"result\":\"1\",\"msg\":\"注销成功\"});");

        Assert.AreEqual(LogoutStatus.Success, result.Status);
    }

    [TestMethod]
    public void Parse_JsonpRejection_IsHandled()
    {
        LogoutResult result = DrComLogoutResponseParser.Parse(
            "dr1003({\"result\":\"0\",\"msg\":\"注销操作只支持本机终端！\"});");

        Assert.AreEqual(LogoutStatus.Rejected, result.Status);
        Assert.IsTrue(result.Detail.Contains("只支持本机终端", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_EmptyBody_IsRejectedWithReason()
    {
        LogoutResult result = DrComLogoutResponseParser.Parse("   ");

        Assert.AreEqual(LogoutStatus.Rejected, result.Status);
        Assert.IsTrue(result.RawMessage.Contains("空响应", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_UnknownBody_IsRejected()
        => Assert.AreEqual(LogoutStatus.Rejected, DrComLogoutResponseParser.Parse("完全无法识别的内容").Status);

    [TestMethod]
    public void Parse_KeepsRawResponseForDiagnostics()
    {
        LogoutResult result = DrComLogoutResponseParser.Parse(SuccessHtml);
        Assert.AreEqual(SuccessHtml, result.RawResponse);
    }

    [TestMethod]
    public void ExtractMessage_StripsTagsAndComments()
    {
        string message = DrComLogoutResponseParser.ExtractMessage(SuccessHtml);

        Assert.IsFalse(message.Contains('<'), $"不应残留标签：{message}");
        Assert.IsFalse(message.Contains("Dr.COMWebLoginID"), $"不应残留标记注释：{message}");
        Assert.IsTrue(message.Contains("Logout succeed", StringComparison.OrdinalIgnoreCase), message);
    }

    [TestMethod]
    public void LogoutResult_TitlesAreDistinct()
    {
        var success = new LogoutResult(LogoutStatus.Success, "", "", "", TimeSpan.Zero);
        var notOnline = new LogoutResult(LogoutStatus.NotOnline, "", "", "", TimeSpan.Zero);
        var error = new LogoutResult(LogoutStatus.NetworkError, "", "", "", TimeSpan.Zero);

        Assert.AreNotEqual(success.Title, error.Title);
        Assert.AreNotEqual(notOnline.Title, error.Title);
        Assert.IsTrue(success.IsSuccess);
        Assert.IsTrue(notOnline.IsSuccess);
        Assert.IsFalse(error.IsSuccess);
    }
}

[TestClass]
public sealed class DrComLogoutServiceTests
{
    [TestMethod]
    public async Task LogoutAsync_UsesPortalDeclaredEndpoint()
    {
        Uri? seenUri = null;
        var handler = new StubHandler(request =>
        {
            seenUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!--Dr.COMWebLoginID_2.htm--> Logout succeed.", Encoding.UTF8),
            });
        });

        var options = new AppOptions();
        options.Normalize();

        using var http = new HttpClient(handler);
        var service = new DrComLogoutService(options, http, new RecordingLogService());

        LogoutResult result = await service.LogoutAsync();

        Assert.AreEqual(LogoutStatus.Success, result.Status);
        Assert.IsNotNull(seenUri);
        Assert.AreEqual("10.17.8.18", seenUri!.Host);
        Assert.AreEqual(801, seenUri.Port);

        // 端点与参数必须与门户自己声明的 authlogoutpath 一致
        Assert.AreEqual("/eportal/", seenUri.AbsolutePath);
        Assert.IsTrue(seenUri.Query.Contains("c=ACSetting", StringComparison.OrdinalIgnoreCase), seenUri.Query);
        Assert.IsTrue(seenUri.Query.Contains("a=Logout", StringComparison.OrdinalIgnoreCase), seenUri.Query);
        Assert.IsTrue(seenUri.Query.Contains("ver=1.0", StringComparison.OrdinalIgnoreCase), seenUri.Query);
    }

    [TestMethod]
    public async Task LogoutAsync_HttpFailure_ReturnsNetworkError()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("目标主机不可达"));
        var options = new AppOptions();
        options.Normalize();

        using var http = new HttpClient(handler);
        var service = new DrComLogoutService(options, http, new RecordingLogService());

        LogoutResult result = await service.LogoutAsync();

        Assert.AreEqual(LogoutStatus.NetworkError, result.Status);
        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void BuildLogoutUri_MatchesPortalConfiguration()
    {
        var options = new AppOptions();
        options.Normalize();
        var service = new DrComLogoutService(options, new HttpClient(), new RecordingLogService());

        Assert.AreEqual(
            "http://10.17.8.18:801/eportal/?c=ACSetting&a=Logout&ver=1.0",
            service.BuildLogoutUri().ToString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}
