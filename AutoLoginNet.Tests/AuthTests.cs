using AutoLoginNet.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class DrComParserTests
{
    [TestMethod]
    public void Parse_Success_ReturnsSuccess()
        => Assert.AreEqual(LoginStatus.Success, DrComResponseParser.Parse("dr1003({\"result\":\"1\",\"msg\":\"登录成功\"})").Status);

    [TestMethod]
    public void Parse_AlreadyOnline_IsDetectedFromMessage()
        => Assert.AreEqual(LoginStatus.AlreadyOnline, DrComResponseParser.Parse("dr1003({\"result\":\"1\",\"msg\":\"该账号已经在线\"})").Status);

    [TestMethod]
    public void Parse_NumericResultOne_IsSuccess()
        => Assert.AreEqual(LoginStatus.Success, DrComResponseParser.Parse("dr1003({\"result\":1,\"msg\":\"ok\"})").Status);

    [TestMethod]
    public void Parse_InvalidCredentials_FromChineseMessage()
        => Assert.AreEqual(
            LoginStatus.InvalidCredentials,
            DrComResponseParser.Parse("dr1003({\"result\":\"0\",\"msg\":\"账号或密码错误\"})").Status);

    [TestMethod]
    public void Parse_Suspended_FromBillingMessage()
        => Assert.AreEqual(
            LoginStatus.AccountSuspended,
            DrComResponseParser.Parse("dr1003({\"result\":\"0\",\"msg\":\"账号已欠费停机\"})").Status);

    [TestMethod]
    public void Parse_ResultCodeTwo_IsInvalidCredentials()
        => Assert.AreEqual(LoginStatus.InvalidCredentials, DrComResponseParser.Parse("dr1003({\"result\":\"2\",\"msg\":\"认证被拒绝\"})").Status);

    [TestMethod]
    public void Parse_ResultCodeThree_IsSuspended()
        => Assert.AreEqual(LoginStatus.AccountSuspended, DrComResponseParser.Parse("dr1003({\"result\":\"3\",\"msg\":\"\"})").Status);

    [TestMethod]
    public void Parse_EmptyBody_IsRejected()
        => Assert.AreEqual(LoginStatus.Rejected, DrComResponseParser.Parse("   ").Status);

    [TestMethod]
    public void Parse_MalformedBody_IsRejected()
        => Assert.AreEqual(LoginStatus.Rejected, DrComResponseParser.Parse("not a jsonp payload").Status);

    [TestMethod]
    public void Parse_BareJsonWithoutJsonpWrapper_StillWorks()
        => Assert.AreEqual(LoginStatus.Success, DrComResponseParser.Parse("{\"result\":\"1\",\"msg\":\"ok\"}").Status);

    [TestMethod]
    public void Parse_KeepsRawResponseForDiagnostics()
    {
        const string body = "dr1003({\"result\":\"0\",\"msg\":\"密码错误\"})";
        Assert.AreEqual(body, DrComResponseParser.Parse(body).RawResponse);
    }

    [TestMethod]
    public void IsTransient_OnlyForNetworkErrors()
    {
        Assert.IsTrue(new LoginResult { Status = LoginStatus.NetworkError }.IsTransient);
        Assert.IsFalse(new LoginResult { Status = LoginStatus.InvalidCredentials }.IsTransient);
    }
}

[TestClass]
public sealed class OperatorsTests
{
    [TestMethod]
    public void ApplySuffix_AppendsOperatorSuffix()
    {
        Assert.AreEqual("2024001@telecom", OperatorKind.Telecom.ApplySuffix("2024001"));
        Assert.AreEqual("2024001@cmcc", OperatorKind.Mobile.ApplySuffix("2024001"));
        Assert.AreEqual("2024001@unicom", OperatorKind.Unicom.ApplySuffix("2024001"));
        Assert.AreEqual("2024001", OperatorKind.Campus.ApplySuffix("2024001"));
    }

    [TestMethod]
    public void ApplySuffix_DoesNotDoubleAppend()
        => Assert.AreEqual("2024001@telecom", OperatorKind.Telecom.ApplySuffix("2024001@telecom"));

    [TestMethod]
    public void ApplySuffix_TrimsWhitespace()
        => Assert.AreEqual("2024001@cmcc", OperatorKind.Mobile.ApplySuffix("  2024001  "));

    [TestMethod]
    public void FromAccount_DetectsOperator()
    {
        Assert.AreEqual(OperatorKind.Telecom, Operators.FromAccount("2024001@telecom"));
        Assert.AreEqual(OperatorKind.Mobile, Operators.FromAccount("2024001@cmcc"));
        Assert.AreEqual(OperatorKind.Unicom, Operators.FromAccount("2024001@unicom"));
        Assert.AreEqual(OperatorKind.Campus, Operators.FromAccount("2024001"));
        Assert.AreEqual(OperatorKind.Campus, Operators.FromAccount(null));
    }

    [TestMethod]
    public void AllOperators_HaveDisplayNames()
        => Assert.IsTrue(Operators.All.All(kind => !string.IsNullOrWhiteSpace(kind.ToDisplayName())));
}
