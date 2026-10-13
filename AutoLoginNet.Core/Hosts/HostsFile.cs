using System.Net;
using System.Net.Sockets;

namespace AutoLoginNet.Hosts;

/// <summary>一条 hosts 映射规则。</summary>
/// <param name="IpAddress">IP 地址（IPv4 或 IPv6）。</param>
/// <param name="HostName">域名。</param>
/// <param name="Comment">行尾注释，例如「官网」。</param>
public sealed record HostsEntry(string IpAddress, string HostName, string Comment = "")
{
    /// <summary>是否为 IPv6 地址。</summary>
    public bool IsIPv6 => IPAddress.TryParse(IpAddress, out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>是否为合法的 IPv4 / IPv6 地址。</summary>
    public bool IsValidIp => IPAddress.TryParse(IpAddress, out _);

    /// <summary>
    /// 渲染为 hosts 文件中的一行。
    /// 对齐域名与注释列，便于人工查看——这些内容用户是会直接打开文件看的。
    /// </summary>
    public string ToLine(int hostNameColumnWidth)
    {
        string padded = HostName.PadRight(Math.Max(hostNameColumnWidth, HostName.Length));
        return string.IsNullOrWhiteSpace(Comment)
            ? $"{IpAddress} {padded}".TrimEnd()
            : $"{IpAddress} {padded} # {Comment}";
    }

    /// <summary>界面预览用的单行文本：域名 + 注释。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Comment) ? HostName : $"{HostName}（{Comment}）";

    /// <summary>解析一行；非映射行（注释、空行）返回 null。</summary>
    public static HostsEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string work = line.Trim();
        string comment = string.Empty;

        // 注释以 # 开始；但 IPv6 的 :: 不含 #，可安全按第一个 # 切分
        int hashIndex = work.IndexOf('#');
        if (hashIndex >= 0)
        {
            comment = work[(hashIndex + 1)..].Trim();
            work = work[..hashIndex].Trim();
        }

        if (work.Length == 0)
        {
            return null;
        }

        string[] parts = work.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return new HostsEntry(parts[0], parts[1], comment);
    }
}

/// <summary>
/// hosts 文件的解析与「托管块」生成。
/// <para>
/// 设计要点：只维护一个带哨兵注释的托管区块。
/// 优点有三——幂等（重复应用不会累积重复行）、可精确撤销（只删自己的块，绝不动用户手写的条目）、
/// 用户可读（打开文件就能看到哪些是应用写入的）。
/// </para>
/// </summary>
public static class HostsFile
{
    /// <summary>托管块起始标记。</summary>
    public const string BeginMarker = "# ==== AutoLoginNet 校园网域名解析（由程序维护，请勿手动修改） ====";

    /// <summary>托管块结束标记。</summary>
    public const string EndMarker = "# ==== AutoLoginNet 校园网域名解析结束 ====";

    /// <summary>从文件内容中解析出所有映射规则（含用户手写与托管块内的）。</summary>
    public static List<HostsEntry> ParseEntries(string content)
    {
        var entries = new List<HostsEntry>();
        if (string.IsNullOrEmpty(content))
        {
            return entries;
        }

        foreach (string line in content.Split('\n'))
        {
            HostsEntry? entry = HostsEntry.Parse(line);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>文件是否已包含托管块。</summary>
    public static bool HasManagedBlock(string content)
        => !string.IsNullOrEmpty(content)
            && content.Contains(BeginMarker, StringComparison.Ordinal);

    /// <summary>
    /// 统计用户<b>手写</b>条目里已经存在的同名映射数量（不含托管块内部）。
    /// 用途：若用户此前手工维护过这些域名，启用功能后不会产生任何变化，
    /// 界面需要据此提示，避免用户误以为开关失效。
    /// </summary>
    public static int CountExistingMappings(string content, IReadOnlyList<HostsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        HashSet<string> existing = ParseUserEntries(content)
            .Select(e => e.HostName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries.Count(e => existing.Contains(e.HostName));
    }

    /// <summary>解析用户手写条目（跳过托管块内部）。</summary>
    public static List<HostsEntry> ParseUserEntries(string content)
    {
        var entries = new List<HostsEntry>();
        if (string.IsNullOrEmpty(content))
        {
            return entries;
        }

        bool insideBlock = false;
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();

            if (trimmed == BeginMarker)
            {
                insideBlock = true;
                continue;
            }

            if (trimmed == EndMarker)
            {
                insideBlock = false;
                continue;
            }

            if (insideBlock)
            {
                continue;
            }

            HostsEntry? entry = HostsEntry.Parse(trimmed);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// 生成应用后的完整文件内容：移除旧的托管块，再追加新的托管块。
    /// 用户原有内容（包括结尾换行风格）尽量保持原样。
    /// </summary>
    public static string ApplyManagedBlock(string originalContent, IReadOnlyList<HostsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        string newline = DetectNewline(originalContent);
        string withoutBlock = RemoveManagedBlock(originalContent, newline);

        var builder = new System.Text.StringBuilder(withoutBlock);

        // 保证托管块之前有且仅有一个换行
        if (builder.Length > 0 && !withoutBlock.EndsWith(newline, StringComparison.Ordinal))
        {
            builder.Append(newline);
        }

        builder.Append(BeginMarker).Append(newline);

        int width = entries.Count == 0 ? 0 : entries.Max(e => e.HostName.Length);
        foreach (HostsEntry entry in entries)
        {
            builder.Append(entry.ToLine(width)).Append(newline);
        }

        builder.Append(EndMarker).Append(newline);

        return builder.ToString();
    }

    /// <summary>移除托管块，返回剩余内容。</summary>
    public static string RemoveManagedBlock(string content, string newline = "\r\n")
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var kept = new List<string>();
        bool insideBlock = false;

        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');

            if (!insideBlock && trimmed.Trim() == BeginMarker)
            {
                insideBlock = true;
                continue;
            }

            if (insideBlock)
            {
                if (trimmed.Trim() == EndMarker)
                {
                    insideBlock = false;
                }

                continue;
            }

            // 去掉块之前由我们补上的多余空行
            kept.Add(trimmed);
        }

        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept.Count == 0 ? string.Empty : string.Join(newline, kept) + newline;
    }

    /// <summary>探测文件使用的换行风格，默认 CRLF（Windows hosts 文件的常规写法）。</summary>
    public static string DetectNewline(string content)
        => string.IsNullOrEmpty(content) || content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
