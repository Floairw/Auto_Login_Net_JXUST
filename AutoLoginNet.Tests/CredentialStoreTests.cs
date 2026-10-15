using AutoLoginNet.Auth;
using AutoLoginNet.Credentials;
using AutoLoginNet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class JsonCredentialStoreTests
{
    [TestMethod]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        using var dir = new TempDirectory();
        var store = new JsonCredentialStore(dir.File("credentials.json"), new FakeCredentialProtector(), new RecordingLogService());

        store.Save("2024001", "p@ssw0rd", OperatorKind.Mobile);

        Assert.IsTrue(store.Exists);
        LoginCredential? loaded = store.Load();

        Assert.IsNotNull(loaded);
        Assert.AreEqual("2024001", loaded!.UserName);
        Assert.AreEqual("p@ssw0rd", loaded.Password);
        Assert.AreEqual(OperatorKind.Mobile, loaded.Operator);
        Assert.AreEqual("2024001@cmcc", loaded.Account);
    }

    [TestMethod]
    public void Save_WritesEncryptedPasswordNotPlainText()
    {
        using var dir = new TempDirectory();
        string path = dir.File("credentials.json");
        var store = new JsonCredentialStore(path, new FakeCredentialProtector(), new RecordingLogService());

        store.Save("2024001", "PlainSecret", OperatorKind.Campus);

        string raw = File.ReadAllText(path);
        Assert.IsFalse(raw.Contains("PlainSecret", StringComparison.Ordinal), "磁盘上不得出现明文密码");
    }

    [TestMethod]
    public void Load_MissingFile_ReturnsNull()
    {
        using var dir = new TempDirectory();
        var store = new JsonCredentialStore(dir.File("nope.json"), new FakeCredentialProtector(), new RecordingLogService());
        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void Load_UnsupportedSchemaVersion_ReturnsNullAndWarns()
    {
        using var dir = new TempDirectory();
        string path = dir.File("credentials.json");

        // v1 与 v2 的差异：v1 的字段名与运营商表示都不同。这里用 v1 的真实 PascalCase 键名，
        // 才能同时验证「旧文件缺 userName 字段」时版本检查先于内容检查生效。
        File.WriteAllText(path, """
        {
          "SchemaVersion": 1,
          "Username": "olduser",
          "Password": "anything",
          "Choice": "@telecom",
          "SaveDate": "2026-01-01T00:00:00+08:00"
        }
        """);

        var log = new RecordingLogService();
        var store = new JsonCredentialStore(path, new FakeCredentialProtector(), log);

        Assert.IsNull(store.Load(), "旧版本凭据必须被拒绝，而不是静默降级使用");
        Assert.IsTrue(
            log.Entries.Any(e => e.Message.Contains("重新登录", StringComparison.Ordinal)),
            $"应提示用户重新登录以保存新格式。实际日志：{string.Join(" | ", log.Entries.Select(e => e.Message))}");
    }

    [TestMethod]
    public void Load_LegacySchemaVersionWithUserName_StillRejected()
    {
        using var dir = new TempDirectory();
        string path = dir.File("credentials.json");
        File.WriteAllText(path, """
        { "schemaVersion": 1, "userName": "olduser", "password": "x", "operator": 1 }
        """);

        var log = new RecordingLogService();
        var store = new JsonCredentialStore(path, new FakeCredentialProtector(), log);

        Assert.IsNull(store.Load());
        Assert.IsTrue(log.Entries.Any(e => e.Message.Contains("重新登录", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsNullWithoutThrowing()
    {
        using var dir = new TempDirectory();
        string path = dir.File("credentials.json");
        File.WriteAllText(path, "{ this is not json");

        var log = new RecordingLogService();
        var store = new JsonCredentialStore(path, new FakeCredentialProtector(), log);

        Assert.IsNull(store.Load());
        Assert.IsTrue(log.Entries.Count > 0, "损坏的凭据文件应留下日志线索");
    }

    [TestMethod]
    public void Load_DecryptionFailure_ReturnsNullAndLogs()
    {
        using var dir = new TempDirectory();
        string path = dir.File("credentials.json");
        File.WriteAllText(path, """
        { "schemaVersion": 2, "userName": "u", "password": "not-encrypted", "operator": 0 }
        """);

        var log = new RecordingLogService();
        var store = new JsonCredentialStore(path, new FakeCredentialProtector(), log);

        Assert.IsNull(store.Load());
        Assert.IsTrue(log.Entries.Any(e => e.Message.Contains("解密失败", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Clear_RemovesFile()
    {
        using var dir = new TempDirectory();
        var store = new JsonCredentialStore(dir.File("credentials.json"), new FakeCredentialProtector(), new RecordingLogService());

        store.Save("u", "p", OperatorKind.Campus);
        Assert.IsTrue(store.Exists);

        store.Clear();
        Assert.IsFalse(store.Exists);
    }

    [TestMethod]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        using var dir = new TempDirectory();
        var store = new JsonCredentialStore(dir.File("credentials.json"), new FakeCredentialProtector(), new RecordingLogService());
        store.Save("u", "p", OperatorKind.Campus);

        Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length, "原子写入的临时文件应已被移动");
    }

    [TestMethod]
    public void Record_DefaultSchemaVersionIsZero_SoLegacyFilesAreDetected()
        => Assert.AreEqual(0, new CredentialRecord().SchemaVersion, "默认值必须为 0，否则旧文件会被误判为新格式");
}

[TestClass]
public sealed class DpapiCredentialProtectorTests
{
    [TestMethod]
    public void ProtectThenUnprotect_RoundTrips()
    {
        var protector = new DpapiCredentialProtector();
        const string secret = "p@ssw0rd-中文-🔐";

        string cipher = protector.Protect(secret);

        Assert.AreNotEqual(secret, cipher);
        Assert.AreEqual(secret, protector.Unprotect(cipher));
    }

    [TestMethod]
    public void Protect_ProducesDifferentCipherTextEachTime()
    {
        var protector = new DpapiCredentialProtector();
        Assert.AreNotEqual(protector.Protect("same"), protector.Protect("same"), "DPAPI 每次加密结果应不同");
    }

    [TestMethod]
    public void Unprotect_GarbageInput_Throws()
    {
        var protector = new DpapiCredentialProtector();
        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(
            () => protector.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
    }
}
