using AutoLoginNet.Configuration;
using AutoLoginNet.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class AppOptionsTests
{
    [TestMethod]
    public void Defaults_MatchJxustEnvironment()
    {
        var options = new AppOptions();

        Assert.AreEqual("10.17.8.18", options.Auth.Host);
        Assert.AreEqual(801, options.Auth.Port);
        Assert.AreEqual("/eportal/portal/login", options.Auth.LoginPath);
        Assert.AreEqual("dr1003", options.Auth.LoginCallback);
        Assert.AreEqual("baidu.com", options.Probes.ExternalProbeHost);
    }

    [TestMethod]
    public void BuildLoginUri_IncludesHostPortAndPath()
    {
        var options = new AppOptions();
        Uri uri = options.BuildLoginUri();

        Assert.AreEqual("http", uri.Scheme);
        Assert.AreEqual("10.17.8.18", uri.Host);
        Assert.AreEqual(801, uri.Port);
        Assert.AreEqual("/eportal/portal/login", uri.AbsolutePath);
    }

    [TestMethod]
    public void BuildLoginUri_AddsLeadingSlashWhenMissing()
    {
        var options = new AppOptions();
        options.Auth.LoginPath = "eportal/portal/login";

        Assert.AreEqual("/eportal/portal/login", options.BuildLoginUri().AbsolutePath);
    }

    [TestMethod]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var options = new AppOptions();
        options.Auth.Port = 99999;
        options.Auth.HttpTimeoutSeconds = 0;
        options.Logging.MaxFileSizeMb = -5;
        options.Startup.NetworkReadyMaxRetries = 0;
        options.Probes.ExternalProbeTimeoutMs = 5;

        options.Normalize();

        Assert.IsTrue(options.Auth.Port is >= 1 and <= 65535);
        Assert.IsTrue(options.Auth.HttpTimeoutSeconds >= 1);
        Assert.IsTrue(options.Logging.MaxFileSizeMb >= 1);
        Assert.IsTrue(options.Startup.NetworkReadyMaxRetries >= 1);
        Assert.IsTrue(options.Probes.ExternalProbeTimeoutMs >= 200);
    }

    [TestMethod]
    public void Normalize_FillsBlankRequiredFields()
    {
        var options = new AppOptions();
        options.Auth.Host = "   ";
        options.Probes.ExternalProbeHost = "";
        options.Auth.LoginPath = "";

        options.Normalize();

        Assert.IsFalse(string.IsNullOrWhiteSpace(options.Auth.Host));
        Assert.IsFalse(string.IsNullOrWhiteSpace(options.Probes.ExternalProbeHost));
        Assert.IsFalse(string.IsNullOrWhiteSpace(options.Auth.LoginPath));
    }

    [TestMethod]
    public void Normalize_DeduplicatesAndTrimsPingTargets()
    {
        var options = new AppOptions();
        options.Tools.PingTargets = new List<string> { " baidu.com ", "baidu.com", "", "  ", "1.1.1.1" };

        options.Normalize();

        CollectionAssert.AreEqual(new[] { "baidu.com", "1.1.1.1" }, options.Tools.PingTargets.ToArray());
    }

    [TestMethod]
    public void Normalize_EmptyPingTargets_FallsBackToDefault()
    {
        var options = new AppOptions();
        options.Tools.PingTargets = new List<string>();

        options.Normalize();

        Assert.IsTrue(options.Tools.PingTargets.Count > 0);
    }
}

[TestClass]
public sealed class JsonSettingsServiceTests
{
    [TestMethod]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var dir = new TempDirectory();
        var service = new JsonSettingsService(dir.File("appsettings.json"), new RecordingLogService());

        AppOptions options = service.Load();

        Assert.AreEqual("10.17.8.18", options.Auth.Host);
        Assert.AreEqual(801, options.Auth.Port);
    }

    [TestMethod]
    public void SaveThenLoad_RoundTripsValues()
    {
        using var dir = new TempDirectory();
        var service = new JsonSettingsService(dir.File("appsettings.json"), new RecordingLogService());

        AppOptions options = service.Load();
        options.Auth.Host = "10.0.0.9";
        options.Auth.Port = 8080;
        options.Tools.CampusSpeedTestUrl = "http://speed.example.edu.cn";
        service.Save(options);

        AppOptions reloaded = service.Load();

        Assert.AreEqual("10.0.0.9", reloaded.Auth.Host);
        Assert.AreEqual(8080, reloaded.Auth.Port);
        Assert.AreEqual("http://speed.example.edu.cn", reloaded.Tools.CampusSpeedTestUrl);
    }

    [TestMethod]
    public void Load_PartialJson_FillsMissingGroupsWithDefaults()
    {
        using var dir = new TempDirectory();
        string path = dir.File("appsettings.json");
        File.WriteAllText(path, """{ "auth": { "host": "10.1.1.1" } }""");

        var service = new JsonSettingsService(path, new RecordingLogService());
        AppOptions options = service.Load();

        Assert.AreEqual("10.1.1.1", options.Auth.Host);
        Assert.IsNotNull(options.Probes);
        Assert.IsNotNull(options.Logging);
        Assert.IsNotNull(options.Startup);
        Assert.IsNotNull(options.Tools);
        Assert.AreEqual(801, options.Auth.Port, "未覆盖的字段应保持默认");
    }

    [TestMethod]
    public void Load_CorruptJson_FallsBackToDefaultsAndLogs()
    {
        using var dir = new TempDirectory();
        string path = dir.File("appsettings.json");
        File.WriteAllText(path, "{ not valid json");

        var log = new RecordingLogService();
        var service = new JsonSettingsService(path, log);
        AppOptions options = service.Load();

        Assert.AreEqual("10.17.8.18", options.Auth.Host);
        Assert.IsTrue(log.Entries.Count > 0, "损坏配置应留下日志");
    }

    [TestMethod]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        using var dir = new TempDirectory();
        var service = new JsonSettingsService(dir.File("appsettings.json"), new RecordingLogService());
        service.Save(new AppOptions());

        Assert.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length);
    }
}
