using AutoLoginNet.Configuration;
using AutoLoginNet.Diagnostics;
using AutoLoginNet.Hosts;
using AutoLoginNet.Tests.Infrastructure;
using AutoLoginNet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class HostsEntryTests
{
    [TestMethod]
    public void Parse_ReadsIpHostAndComment()
    {
        HostsEntry? entry = HostsEntry.Parse("10.16.17.37 jw.jxust.edu.cn #教务系统");

        Assert.IsNotNull(entry);
        Assert.AreEqual("10.16.17.37", entry!.IpAddress);
        Assert.AreEqual("jw.jxust.edu.cn", entry.HostName);
        Assert.AreEqual("教务系统", entry.Comment);
    }

    [TestMethod]
    public void Parse_HandlesLineWithoutComment()
    {
        HostsEntry? entry = HostsEntry.Parse("10.16.10.70 proxy.jxust.edu.cn");

        Assert.IsNotNull(entry);
        Assert.AreEqual(string.Empty, entry!.Comment);
    }

    [TestMethod]
    public void Parse_RecognizesIpv6()
    {
        HostsEntry? entry = HostsEntry.Parse("2001:250:6c0b:6010::10:50 jxust.edu.cn");

        Assert.IsNotNull(entry);
        Assert.IsTrue(entry!.IsIPv6, "IPv6 地址必须被识别，不能因为含冒号而误判");
        Assert.IsTrue(entry.IsValidIp);
    }

    [TestMethod]
    public void Parse_RecognizesIpv4()
    {
        var entry = new HostsEntry("219.229.232.50", "jxust.edu.cn");
        Assert.IsFalse(entry.IsIPv6);
        Assert.IsTrue(entry.IsValidIp);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("# 只是注释")]
    [DataRow("只有一段内容")]
    public void Parse_ReturnsNullForNonMappingLines(string line)
        => Assert.IsNull(HostsEntry.Parse(line));

    [TestMethod]
    public void ToLine_PadsHostNameToRequestedWidth()
    {
        HostsEntry entry = new("10.16.10.70", "proxy.jxust.edu.cn", "代理");
        const int width = 30;
        string line = entry.ToLine(width);

        // 注释的起始列应与指定宽度一致，保证多行注释对齐
        int hashIndex = line.IndexOf('#');
        Assert.AreEqual("10.16.10.70 ".Length + width + 1, hashIndex, "注释应对齐到固定列");
        Assert.IsTrue(line.StartsWith("10.16.10.70 proxy.jxust.edu.cn", StringComparison.Ordinal));
        Assert.IsTrue(line.EndsWith("# 代理", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToLine_DoesNotTruncateLongHostName()
    {
        HostsEntry entry = new("10.0.0.1", "a-very-long-host-name.example.edu.cn");

        // 域名比指定宽度更长时不能被截断
        Assert.AreEqual("10.0.0.1 a-very-long-host-name.example.edu.cn", entry.ToLine(hostNameColumnWidth: 10));
    }

    [TestMethod]
    public void ToLine_OmitsTrailingWhitespaceWhenNoComment()
    {
        HostsEntry entry = new("10.16.10.70", "proxy.jxust.edu.cn");
        Assert.AreEqual("10.16.10.70 proxy.jxust.edu.cn", entry.ToLine(hostNameColumnWidth: 30));
    }
}

[TestClass]
public sealed class HostsFileManagedBlockTests
{
    private const string UserContent = """
        # 用户自己写的条目
        127.0.0.1 localhost
        192.168.1.10 nas.local # 群晖
        """;

    [TestMethod]
    public void ApplyManagedBlock_AppendsBlockAndKeepsUserContent()
    {
        string result = HostsFile.ApplyManagedBlock(UserContent, Defaults());

        Assert.IsTrue(result.Contains("127.0.0.1 localhost", StringComparison.Ordinal), "用户原有条目必须保留");
        Assert.IsTrue(result.Contains("nas.local", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains(HostsFile.BeginMarker, StringComparison.Ordinal));
        Assert.IsTrue(result.Contains(HostsFile.EndMarker, StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("jw.jxust.edu.cn", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApplyManagedBlock_IsIdempotent()
    {
        string once = HostsFile.ApplyManagedBlock(UserContent, Defaults());
        string twice = HostsFile.ApplyManagedBlock(once, Defaults());

        Assert.AreEqual(once, twice, "重复应用不得累积重复的托管块");
        Assert.AreEqual(1, CountOccurrences(twice, HostsFile.BeginMarker));
    }

    [TestMethod]
    public void RemoveManagedBlock_RestoresUserContentExactly()
    {
        string applied = HostsFile.ApplyManagedBlock(UserContent, Defaults());
        string restored = HostsFile.RemoveManagedBlock(applied);

        // 恢复后不应残留任何托管规则或标记
        Assert.IsFalse(restored.Contains(HostsFile.BeginMarker, StringComparison.Ordinal));
        Assert.IsFalse(restored.Contains("jw.jxust.edu.cn", StringComparison.Ordinal));
        Assert.IsTrue(restored.Contains("nas.local", StringComparison.Ordinal));
        Assert.AreEqual(
            HostsFile.ParseEntries(UserContent).Count,
            HostsFile.ParseEntries(restored).Count,
            "用户原有映射条数应保持一致");
    }

    [TestMethod]
    public void RemoveManagedBlock_OnFileWithoutBlock_IsNoOp()
    {
        string restored = HostsFile.RemoveManagedBlock(UserContent);

        CollectionAssert.AreEqual(
            HostsFile.ParseEntries(UserContent).Select(e => (e.IpAddress, e.HostName)).ToList(),
            HostsFile.ParseEntries(restored).Select(e => (e.IpAddress, e.HostName)).ToList());
    }

    [TestMethod]
    public void ApplyManagedBlock_RoundTripsThroughParse()
    {
        string applied = HostsFile.ApplyManagedBlock(string.Empty, Defaults());

        List<HostsEntry> parsed = HostsFile.ParseEntries(applied);
        List<HostsEntry> expected = Defaults();

        Assert.AreEqual(expected.Count, parsed.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].IpAddress, parsed[i].IpAddress, $"第 {i + 1} 条 IP");
            Assert.AreEqual(expected[i].HostName, parsed[i].HostName, $"第 {i + 1} 条域名");
        }
    }

    [TestMethod]
    public void DetectNewline_PrefersCrlfOnWindowsFiles()
        => Assert.AreEqual("\r\n", HostsFile.DetectNewline("a\r\nb"));

    [TestMethod]
    public void HasManagedBlock_DetectsMarker()
    {
        Assert.IsFalse(HostsFile.HasManagedBlock(UserContent));
        Assert.IsTrue(HostsFile.HasManagedBlock(HostsFile.ApplyManagedBlock(UserContent, Defaults())));
    }

    [TestMethod]
    public void ParseUserEntries_SkipsManagedBlock()
    {
        string content = $"""
        # 用户条目
        127.0.0.1 localhost
        {HostsFile.BeginMarker}
        10.16.17.37 jw.jxust.edu.cn # 教务系统
        {HostsFile.EndMarker}
        192.168.1.10 nas.local
        """;

        List<HostsEntry> user = HostsFile.ParseUserEntries(content);

        Assert.AreEqual(2, user.Count, "托管块内的条目不应算作用户条目");
        Assert.IsTrue(user.Any(e => e.HostName == "localhost"));
        Assert.IsTrue(user.Any(e => e.HostName == "nas.local"));
    }

    [TestMethod]
    public void CountExistingMappings_DetectsUserMaintainedEntries()
    {
        // 模拟「用户自己已经手工写好这些规则」的情形：这些条目在托管块之外
        string content = """
        # Copyright (c) 1993-2009 Microsoft Corp.
        219.229.232.50 jxust.edu.cn #官网
        10.16.10.70 proxy.jxust.edu.cn
        10.16.17.37 jw.jxust.edu.cn #教务系统
        """;

        List<HostsEntry> expected = new()
        {
            new HostsEntry("219.229.232.50", "jxust.edu.cn", "官网"),
            new HostsEntry("10.16.10.70", "proxy.jxust.edu.cn"),
            new HostsEntry("10.16.17.37", "jw.jxust.edu.cn", "教务系统"),
            new HostsEntry("10.16.10.4", "info.jxust.edu.cn", "资源列表"),
        };

        int mapped = HostsFile.CountExistingMappings(content, expected);

        Assert.AreEqual(3, mapped, "只有前三条已存在；info.jxust.edu.cn 未覆盖");
    }

    [TestMethod]
    public void CountExistingMappings_IgnoresManagedBlockItself()
    {
        // 已注入的情况下不应把托管块自己算作「用户手工覆盖」
        string applied = HostsFile.ApplyManagedBlock("# 空文件", Defaults());

        int mapped = HostsFile.CountExistingMappings(applied, Defaults());

        Assert.AreEqual(0, mapped);
    }

    [TestMethod]
    public void CountExistingMappings_IsCaseInsensitiveOnHostName()
    {
        string content = "10.0.0.1 JW.JXUST.EDU.CN";
        int mapped = HostsFile.CountExistingMappings(content, new List<HostsEntry>
        {
            new("10.16.17.37", "jw.jxust.edu.cn"),
        });

        Assert.AreEqual(1, mapped);
    }

    private static List<HostsEntry> Defaults() => new()
    {
        new HostsEntry("219.229.232.50", "jxust.edu.cn", "官网"),
        new HostsEntry("2001:250:6c0b:6010::10:50", "jxust.edu.cn"),
        new HostsEntry("10.16.17.37", "jw.jxust.edu.cn", "教务系统"),
    };

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}

[TestClass]
public sealed class WindowsHostsFileServiceTests
{
    [TestMethod]
    public async Task ApplyThenRestore_EndToEndOnRealFile()
    {
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");
        await File.WriteAllTextAsync(hostsPath, "# 用户条目\r\n127.0.0.1 localhost\r\n");

        var options = new AppOptions();
        options.Normalize();
        var service = new WindowsHostsFileService(options, new RecordingLogService(), hostsPath);

        // 初始：未注入
        (HostsState initial, _) = await service.GetStateAsync();
        Assert.AreEqual(HostsState.NotApplied, initial);

        // 注入
        HostsApplyResult applied = await service.ApplyAsync();
        Assert.IsTrue(applied.Success, applied.Message);
        Assert.AreEqual(HostsState.Applied, applied.State);

        string content = await File.ReadAllTextAsync(hostsPath);
        Assert.IsTrue(content.Contains("127.0.0.1 localhost", StringComparison.Ordinal), "用户条目必须保留");
        Assert.IsTrue(content.Contains("jw.jxust.edu.cn", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("2001:250:6c0b:6010::10:50", StringComparison.Ordinal), "IPv6 规则也要写入");
        Assert.IsFalse(content.StartsWith('\uFEFF'), "hosts 文件不应带 BOM");

        // 重复注入保持幂等
        await service.ApplyAsync();
        string twice = await File.ReadAllTextAsync(hostsPath);
        int markerCount = twice.Split(HostsFile.BeginMarker).Length - 1;
        Assert.AreEqual(1, markerCount, "托管块只能存在一个");

        // 恢复
        HostsApplyResult restored = await service.RestoreAsync();
        Assert.IsTrue(restored.Success, restored.Message);

        string final = await File.ReadAllTextAsync(hostsPath);
        Assert.IsFalse(final.Contains("jw.jxust.edu.cn", StringComparison.Ordinal));
        Assert.IsTrue(final.Contains("127.0.0.1 localhost", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetState_WithDriftedBlock_ReportsOutdated()
    {
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");

        // 手写一个内容与当前配置不符的托管块
        await File.WriteAllTextAsync(hostsPath, $"""
        {HostsFile.BeginMarker}
        1.2.3.4 stale.jxust.edu.cn
        {HostsFile.EndMarker}
        """);

        var options = new AppOptions();
        options.Normalize();
        var service = new WindowsHostsFileService(options, new RecordingLogService(), hostsPath);

        (HostsState state, _) = await service.GetStateAsync();

        Assert.AreEqual(HostsState.Outdated, state, "被改动过的托管块应提示需要重新注入");
    }

    [TestMethod]
    public async Task GetState_MissingFile_ReportsUnknown()
    {
        using var dir = new TempDirectory();
        var service = new WindowsHostsFileService(new AppOptions(), new RecordingLogService(), dir.File("nope"));

        (HostsState state, _) = await service.GetStateAsync();

        Assert.AreEqual(HostsState.Unknown, state);
    }

    [TestMethod]
    public async Task Apply_CreatesBackupBeforeWriting()
    {
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");
        const string original = "# 原始内容\r\n127.0.0.1 localhost\r\n";
        await File.WriteAllTextAsync(hostsPath, original);

        var service = new WindowsHostsFileService(new AppOptions(), new RecordingLogService(), hostsPath);
        HostsApplyResult result = await service.ApplyAsync();

        Assert.IsTrue(result.Success);
        Assert.IsFalse(string.IsNullOrEmpty(result.BackupPath), "写入前必须留下备份");
        Assert.IsTrue(File.Exists(result.BackupPath), "备份文件应真实存在");
        Assert.AreEqual(original, await File.ReadAllTextAsync(result.BackupPath));
    }

    [TestMethod]
    public async Task Apply_WritesInPlace_WithoutReplacingTheFile()
    {
        // 根因回归：旧实现「写 hosts.aln.tmp 再同目录改名替换」会被安全软件 HIPS 驱动
        // 拦成 Access denied（火绒实测），宿主目录里改名这条路不可用。
        // 原地改写不会改变文件身份；一旦有人改回「删除+改名」替换，这个断言会失败。
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");
        await File.WriteAllTextAsync(hostsPath, "# 用户条目\r\n127.0.0.1 localhost\r\n");

        (ulong Volume, ulong Index)? before = FileIdentity.TryGet(hostsPath);

        var options = new AppOptions();
        options.Normalize();
        var service = new WindowsHostsFileService(options, new RecordingLogService(), hostsPath);

        HostsApplyResult applied = await service.ApplyAsync();
        Assert.IsTrue(applied.Success, applied.Message);

        (ulong Volume, ulong Index)? after = FileIdentity.TryGet(hostsPath);
        if (before is not null && after is not null)
        {
            Assert.AreEqual(before.Value.Volume, after.Value.Volume, "文件所在卷不应改变");
            Assert.AreEqual(before.Value.Index, after.Value.Index, "hosts 必须是原地改写，不能被别的文件替换");
        }

        Assert.IsFalse(File.Exists(hostsPath + ".aln.tmp"), "不应再产生同目录临时文件");
        Assert.IsTrue((await File.ReadAllTextAsync(hostsPath)).Contains("jw.jxust.edu.cn", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Apply_CleansUpStrayTempFileLeftByOlderVersions()
    {
        // 旧版本被拦时会在 etc 目录里留下 hosts.aln.tmp，新版本写入成功后应顺手清掉
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");
        await File.WriteAllTextAsync(hostsPath, "127.0.0.1 localhost\r\n");
        string stray = hostsPath + ".aln.tmp";
        await File.WriteAllTextAsync(stray, "老版本遗留的半成品内容");

        var options = new AppOptions();
        options.Normalize();
        var service = new WindowsHostsFileService(options, new RecordingLogService(), hostsPath);

        HostsApplyResult applied = await service.ApplyAsync();

        Assert.IsTrue(applied.Success, applied.Message);
        Assert.IsFalse(File.Exists(stray), "遗留临时文件应被清理");
        Assert.IsTrue((await File.ReadAllTextAsync(hostsPath)).Contains("jw.jxust.edu.cn", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExtractManagedEntries_IgnoresUserEntries()
    {
        string content = $"""
        # 用户条目
        127.0.0.1 localhost
        {HostsFile.BeginMarker}
        10.16.17.37 jw.jxust.edu.cn # 教务系统
        {HostsFile.EndMarker}
        10.0.0.1 after.jxust.edu.cn
        """;

        List<HostsEntry> managed = WindowsHostsFileService.ExtractManagedEntries(content);

        Assert.AreEqual(1, managed.Count);
        Assert.AreEqual("jw.jxust.edu.cn", managed[0].HostName);
    }

    [TestMethod]
    public async Task GetStatusInfo_CountsUserMaintainedMappings()
    {
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");

        // 用户已经自己维护了全部域名
        var lines = ToolsOptions.CreateDefaultHostsEntries()
            .Select(e => $"{e.IpAddress} {e.HostName}");
        await File.WriteAllLinesAsync(hostsPath, lines);

        var options = new AppOptions();
        options.Normalize();
        var service = new WindowsHostsFileService(options, new RecordingLogService(), hostsPath);

        HostsStatusInfo info = await service.GetStatusInfoAsync();

        Assert.AreEqual(HostsState.NotApplied, info.State);
        Assert.AreEqual(
            service.DefaultEntries.Count,
            info.AlreadyMappedCount,
            "应识别出用户已手工维护了全部默认映射");
    }

    [TestMethod]
    public async Task GetStatusInfo_AfterApply_ReportsAppliedAndZeroUserMappings()
    {
        using var dir = new TempDirectory();
        string hostsPath = dir.File("hosts");
        await File.WriteAllTextAsync(hostsPath, "# 空\r\n");

        var service = new WindowsHostsFileService(new AppOptions(), new RecordingLogService(), hostsPath);
        await service.ApplyAsync();

        HostsStatusInfo info = await service.GetStatusInfoAsync();

        Assert.AreEqual(HostsState.Applied, info.State);
        Assert.AreEqual(0, info.AlreadyMappedCount, "已注入时不应把托管块算作用户条目");
    }
}

[TestClass]
public sealed class DefaultHostsEntriesTests
{
    [TestMethod]
    public void Defaults_ContainRequestedJxustMappings()
    {
        List<HostsEntry> entries = ToolsOptions.CreateDefaultHostsEntries();

        // 用户提供的 16 条规则（jxust.edu.cn 同时有 IPv4 与 IPv6 两条）
        Assert.AreEqual(16, entries.Count);
        Assert.IsTrue(entries.All(e => e.IsValidIp), "所有规则都必须是合法 IP");
        Assert.IsTrue(entries.All(e => e.HostName.EndsWith("jxust.edu.cn", StringComparison.OrdinalIgnoreCase)));

        void AssertMapping(string ip, string host)
            => Assert.IsTrue(
                entries.Any(e => e.IpAddress == ip && e.HostName == host),
                $"缺少映射：{ip} {host}");

        AssertMapping("219.229.232.50", "jxust.edu.cn");
        AssertMapping("2001:250:6c0b:6010::10:50", "jxust.edu.cn");
        AssertMapping("10.16.10.70", "proxy.jxust.edu.cn");
        AssertMapping("10.16.10.4", "info.jxust.edu.cn");
        AssertMapping("10.16.10.70", "ehall.jxust.edu.cn");
        AssertMapping("10.16.10.70", "authserver.jxust.edu.cn");
        AssertMapping("10.16.17.37", "jw.jxust.edu.cn");
        AssertMapping("172.20.2.59", "ecard.jxust.edu.cn");
        AssertMapping("10.16.30.100", "uam.jxust.edu.cn");
        AssertMapping("10.16.30.100", "dcs.jxust.edu.cn");
        AssertMapping("10.16.30.100", "tsa.jxust.edu.cn");
        AssertMapping("10.16.30.100", "dfm.jxust.edu.cn");
        AssertMapping("10.16.16.171", "yjsgl.jxust.edu.cn");
        AssertMapping("10.16.16.130", "dxyqyy.jxust.edu.cn");
        AssertMapping("10.16.2.100", "speedtest.jxust.edu.cn");
        AssertMapping("10.16.2.101", "speedtest-sj.jxust.edu.cn");
    }

    [TestMethod]
    public void Defaults_KeepBothIpVersionsForApexDomain()
    {
        List<HostsEntry> apex = ToolsOptions.CreateDefaultHostsEntries()
            .Where(e => e.HostName == "jxust.edu.cn")
            .ToList();

        Assert.AreEqual(2, apex.Count, "官网域名需同时保留 IPv4 与 IPv6 两条映射");
        Assert.IsTrue(apex.Any(e => !e.IsIPv6), "缺少 IPv4 映射");
        Assert.IsTrue(apex.Any(e => e.IsIPv6), "缺少 IPv6 映射");
    }

    [TestMethod]
    public void Normalize_DropsInvalidHostsEntries()
    {
        var options = new AppOptions();
        options.Tools.HostsEntries = new List<HostsEntry>
        {
            new("10.16.10.70", "ehall.jxust.edu.cn"),
            new("不是IP", "broken.jxust.edu.cn"),
            new("10.16.10.4", "   "),
        };

        options.Normalize();

        // 非法 IP 与空域名被丢掉；保留下来的这条仍然在，且内置规则被补齐。
        Assert.IsTrue(options.Tools.HostsEntries.Any(e => e.HostName == "ehall.jxust.edu.cn"),
            "合法规则不能因为清洗而丢失");
        Assert.IsFalse(options.Tools.HostsEntries.Any(e => e.HostName == "broken.jxust.edu.cn"),
            "非法 IP 必须被丢弃");
        Assert.IsTrue(options.Tools.HostsEntries.All(e => !string.IsNullOrWhiteSpace(e.HostName)),
            "不允许留下空域名规则");
    }

    [TestMethod]
    public void Normalize_FallsBackToDefaultsWhenListEmptied()
    {
        var options = new AppOptions();
        options.Tools.HostsEntries = new List<HostsEntry>();

        options.Normalize();

        Assert.AreEqual(ToolsOptions.CreateDefaultHostsEntries().Count, options.Tools.HostsEntries.Count);
    }

    [TestMethod]
    public void Normalize_MergesBuiltInEntriesMissingFromPersistedSettings()
    {
        // 复现真实场景：老用户的 appsettings.json 里是 2.0.7 之前的 14 条规则，
        // 缺少后来新增的两条校内测速规则；整组覆盖会让新增规则永远不出现。
        List<HostsEntry> persisted = ToolsOptions.CreateDefaultHostsEntries()
            .Where(e => e.HostName is not ("speedtest.jxust.edu.cn" or "speedtest-sj.jxust.edu.cn"))
            .ToList();
        Assert.AreEqual(14, persisted.Count, "前置条件：持久化配置里没有那两条测速规则");

        var options = new AppOptions();
        options.Tools.HostsEntries = persisted;
        options.Normalize();

        void AssertMapping(string ip, string host)
            => Assert.IsTrue(
                options.Tools.HostsEntries.Any(e => e.IpAddress == ip && e.HostName == host),
                $"缺少映射：{ip} {host}");

        AssertMapping("10.16.2.100", "speedtest.jxust.edu.cn");
        AssertMapping("10.16.2.101", "speedtest-sj.jxust.edu.cn");
        Assert.AreEqual(persisted.Count + 2, options.Tools.HostsEntries.Count, "只补缺失的，不重复添加");
    }

    [TestMethod]
    public void Normalize_KeepsUserEntriesAndTheirEditedComments()
    {
        var options = new AppOptions();
        options.Tools.HostsEntries = new List<HostsEntry>
        {
            new("10.16.10.70", "ehall.jxust.edu.cn", "我改过的注释"),
            new("10.16.99.99", "custom.jxust.edu.cn", "用户自己加的"),
        };

        options.Normalize();

        HostsEntry? ehall = options.Tools.HostsEntries
            .FirstOrDefault(e => e.HostName == "ehall.jxust.edu.cn");
        Assert.IsNotNull(ehall, "内置规则不能被删掉");
        Assert.AreEqual("我改过的注释", ehall.Comment, "用户改过的注释不能被内置值覆盖");
        Assert.AreEqual("10.16.10.70", ehall.IpAddress, "用户改过的地址不能被内置值覆盖");

        Assert.IsTrue(
            options.Tools.HostsEntries.Any(e => e.HostName == "custom.jxust.edu.cn"),
            "用户自己加的规则必须保留");

        // 内置规则里与用户配置重复的不能再加一遍
        Assert.AreEqual(
            1,
            options.Tools.HostsEntries.Count(e => e.HostName == "ehall.jxust.edu.cn"),
            "同一映射不允许出现两条");
    }
}
