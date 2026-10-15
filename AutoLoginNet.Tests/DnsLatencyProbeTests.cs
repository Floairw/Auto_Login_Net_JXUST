using AutoLoginNet.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;

namespace AutoLoginNet.Tests;

[TestClass]
public sealed class UdpDnsLatencyProbeTests
{
    [TestMethod]
    public void BuildQuery_ProducesWellFormedSingleQuestionPacket()
    {
        byte[] query = UdpDnsLatencyProbe.BuildQuery(0x1234, "www.baidu.com");

        // 头部 12 字节 + 3 段标签 + 结束字节 + QTYPE/QCLASS
        Assert.AreEqual(0x12, query[0], "ID 高字节");
        Assert.AreEqual(0x34, query[1], "ID 低字节");
        Assert.AreEqual(0x01, query[2], "Flags 高字节：标准查询");
        Assert.AreEqual(0x00, query[3], "Flags 低字节：期望递归");
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)), "QDCOUNT 应为 1");

        // 域名按「长度前缀 + 标签」编码，以 0 结束
        int offset = 12;
        foreach (string label in new[] { "www", "baidu", "com" })
        {
            int length = query[offset];
            Assert.AreEqual(label.Length, length, $"标签 {label} 的长度前缀");
            Assert.AreEqual(label, System.Text.Encoding.ASCII.GetString(query, offset + 1, length));
            offset += 1 + length;
        }

        Assert.AreEqual(0, query[offset], "域名应以 0 结束");
        offset++;

        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(offset)), "QTYPE 应为 A");
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(offset + 2)), "QCLASS 应为 IN");
        Assert.AreEqual(offset + 4, query.Length, "报文长度应与内容一致");
    }

    [TestMethod]
    public void BuildQuery_RejectsInvalidDomain()
    {
        Assert.ThrowsException<ArgumentException>(() => UdpDnsLatencyProbe.BuildQuery(1, ""));
        Assert.ThrowsException<ArgumentException>(() => UdpDnsLatencyProbe.BuildQuery(1, new string('a', 64) + ".com"));
    }

    [TestMethod]
    public void IsValidResponse_AcceptsMatchingReply()
    {
        byte[] response = BuildResponse(id: 0x4321, isResponse: true, rcode: 0);
        Assert.IsTrue(UdpDnsLatencyProbe.IsValidResponse(response, 0x4321));
    }

    [TestMethod]
    public void IsValidResponse_RejectsMismatchedId()
        => Assert.IsFalse(UdpDnsLatencyProbe.IsValidResponse(BuildResponse(1, true, 0), 2));

    [TestMethod]
    public void IsValidResponse_RejectsQueryEchoAndErrorCodes()
    {
        // QR=0 表示这还是查询报文，不是应答
        Assert.IsFalse(UdpDnsLatencyProbe.IsValidResponse(BuildResponse(7, isResponse: false, rcode: 0), 7));

        // RCODE=SERVFAIL
        Assert.IsFalse(UdpDnsLatencyProbe.IsValidResponse(BuildResponse(7, isResponse: true, rcode: 2), 7));
    }

    [TestMethod]
    public void IsValidResponse_RejectsTruncatedPacket()
        => Assert.IsFalse(UdpDnsLatencyProbe.IsValidResponse(new byte[8], 1));

    [TestMethod]
    public async Task MeasureAsync_InvalidAddress_IsUnreachableWithoutThrowing()
    {
        var probe = new UdpDnsLatencyProbe();

        ProbeResult result = await probe.MeasureAsync("not-an-ip");

        Assert.IsFalse(result.Reachable);
        Assert.IsTrue(result.Detail.Contains("IPv4", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task MeasureAsync_UnroutableAddress_TimesOutGracefully()
    {
        var probe = new UdpDnsLatencyProbe();

        // 保留测试网段地址，不会有 DNS 服务应答
        ProbeResult result = await probe.MeasureAsync("192.0.2.1", timeoutMs: 700);

        Assert.IsFalse(result.Reachable);
        Assert.AreEqual("DNS", result.AttemptedMethod);
    }

    private static byte[] BuildResponse(ushort id, bool isResponse, int rcode)
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, id);

        ushort flags = (ushort)(rcode & 0x000F);
        if (isResponse)
        {
            flags |= 0x8000;
        }

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), flags);
        return buffer;
    }
}

[TestClass]
public sealed class DnsSlotMeasurementTests
{
    [TestMethod]
    public void Apply_RecordsLatencyAndMethod()
    {
        var slot = new DnsSlot("114.114.114.114");

        slot.Apply(new ProbeResult("114.114.114.114", true, TimeSpan.FromMilliseconds(23), "ok", "DNS"));

        Assert.IsTrue(slot.Reachable);
        Assert.AreEqual(23, slot.LatencyMs);
        Assert.AreEqual("23 ms", slot.LatencyText);
        Assert.AreEqual("DNS", slot.Method);
    }

    [TestMethod]
    public void Apply_Unreachable_ShowsPlaceholderInsteadOfZero()
    {
        var slot = new DnsSlot("8.8.8.8");

        slot.Apply(new ProbeResult("8.8.8.8", false, TimeSpan.FromSeconds(2), "超时", "DNS"));

        Assert.IsFalse(slot.Reachable);
        Assert.AreEqual(-1, slot.LatencyMs);
        Assert.AreEqual("—", slot.LatencyText, "无应答不能显示成 0 ms");
    }

    [TestMethod]
    public void Reset_ClearsPreviousMeasurement()
    {
        var slot = new DnsSlot("1.1.1.1");
        slot.Apply(new ProbeResult("1.1.1.1", true, TimeSpan.FromMilliseconds(9), "", "DNS"));
        slot.MarkAsChosen("首选");

        slot.Reset();

        Assert.IsFalse(slot.Reachable);
        Assert.IsFalse(slot.IsChosen);
        Assert.AreEqual(string.Empty, slot.Rank);
        Assert.AreEqual("—", slot.LatencyText);
    }

    [TestMethod]
    public void LatencyText_RaisesChangeNotification()
    {
        var slot = new DnsSlot("223.5.5.5");
        var changed = new List<string?>();
        slot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        slot.Apply(new ProbeResult("223.5.5.5", true, TimeSpan.FromMilliseconds(31), "", "DNS"));

        CollectionAssert.Contains(changed, nameof(DnsSlot.LatencyMs));
        CollectionAssert.Contains(changed, nameof(DnsSlot.LatencyText));
        CollectionAssert.Contains(changed, nameof(DnsSlot.Reachable));
    }

    [TestMethod]
    public void DnsCandidateEntry_ExposesBothSlotsAndResetsThem()
    {
        var entry = new DnsCandidateEntry(new DnsProvider("114 DNS", "114.114.114.114", "114.114.115.115"));

        Assert.AreEqual("114 DNS", entry.Name);
        Assert.AreEqual("114.114.114.114", entry.Primary.Address);
        Assert.AreEqual("114.114.115.115", entry.Backup.Address);

        entry.Primary.Apply(new ProbeResult("114.114.114.114", true, TimeSpan.FromMilliseconds(20), "", "DNS"));
        entry.Reset();

        Assert.IsFalse(entry.Primary.Reachable);
        Assert.IsFalse(entry.Backup.Reachable);
    }
}
