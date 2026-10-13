using System.Text;
using AutoLoginNet.Configuration;
using AutoLoginNet.Diagnostics;
using AutoLoginNet.Hosts;

namespace AutoLoginNet.Windows;

/// <summary>
/// Windows hosts 文件读写实现。
/// <para>
/// 安全约束（改坏 hosts 会影响整机域名解析，因此格外保守）：
/// 1) 只维护带哨兵注释的托管区块，<b>绝不触碰用户手写的任何条目</b>；
/// 2) 每次写入前先把原文件备份到数据目录，文件名带时间戳；
/// 3) 写入后读回校验，校验不过立刻用原内容回滚；
/// 4) 保留原文件的换行风格与无 BOM 编码。
/// </para>
/// <para>
/// 为什么不走「临时文件 + 改名替换」：<c>drivers\etc</c> 目录里对 hosts 做同目录改名，
/// 会被安全软件的 HIPS 驱动拦截并返回 <c>Access denied</c>——火绒实测如此，
/// 而原地写入与跨目录覆盖复制都放行。因此这里改成原地截断写入；
/// 崩溃窗口靠「写入前备份 + 写后校验 + 失败回滚」兜住。
/// </para>
/// </summary>
public sealed class WindowsHostsFileService : IHostsFileService
{
    private static readonly string DefaultHostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"drivers\etc\hosts");

    private readonly AppOptions _options;
    private readonly ILogService _log;

    public WindowsHostsFileService(AppOptions options, ILogService log, string? hostsPath = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        FilePath = string.IsNullOrWhiteSpace(hostsPath) ? DefaultHostsPath : hostsPath;
    }

    public string FilePath { get; }

    public IReadOnlyList<HostsEntry> DefaultEntries => _options.Tools.HostsEntries;

    public async Task<(HostsState State, IReadOnlyList<HostsEntry> Entries)> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return (HostsState.Unknown, Array.Empty<HostsEntry>());
            }

            string content = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            List<HostsEntry> entries = HostsFile.ParseEntries(content);

            if (!HostsFile.HasManagedBlock(content))
            {
                return (HostsState.NotApplied, entries);
            }

            // 对比托管块内的规则是否与当前配置一致
            List<HostsEntry> managed = ExtractManagedEntries(content);
            bool inSync = managed.Count == DefaultEntries.Count
                && managed.Select(e => (e.IpAddress, e.HostName))
                    .SequenceEqual(DefaultEntries.Select(e => (e.IpAddress, e.HostName)));

            return (inSync ? HostsState.Applied : HostsState.Outdated, entries);
        }
        catch (Exception ex)
        {
            _log.Error($"读取 hosts 文件失败：{ex.Message}", "HOSTS");
            return (HostsState.Unknown, Array.Empty<HostsEntry>());
        }
    }

    public async Task<HostsStatusInfo> GetStatusInfoAsync(CancellationToken cancellationToken = default)
    {
        (HostsState state, IReadOnlyList<HostsEntry> entries) = await GetStateAsync(cancellationToken).ConfigureAwait(false);

        int alreadyMapped = 0;
        try
        {
            if (File.Exists(FilePath))
            {
                string content = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
                alreadyMapped = HostsFile.CountExistingMappings(content, DefaultEntries);
            }
        }
        catch
        {
            // 统计失败不影响状态展示
        }

        return new HostsStatusInfo(state, entries.Count, alreadyMapped);
    }

    public async Task<HostsApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        return await WriteAsync(
            transform: original => HostsFile.ApplyManagedBlock(original, DefaultEntries),
            action: "写入",
            successMessage: $"已写入 {DefaultEntries.Count} 条校园网域名解析规则",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostsApplyResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        return await WriteAsync(
            // 用 lambda 包一层：RemoveManagedBlock 带可选参数，方法组无法直接转成 Func<string,string>
            transform: original => HostsFile.RemoveManagedBlock(original),
            action: "移除",
            successMessage: "已移除程序写入的域名解析规则，其余内容保持不变",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostsApplyResult> WriteAsync(
        Func<string, string> transform,
        string action,
        string successMessage,
        CancellationToken cancellationToken)
    {
        string step = "读取 hosts 文件";
        try
        {
            if (!File.Exists(FilePath))
            {
                return HostsApplyResult.Failure($"找不到 hosts 文件：{FilePath}");
            }

            string original = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            string updated = transform(original);

            // 写入前备份，便于人工回滚
            string backupPath = Backup(original);

            step = "写入 hosts 文件";
            await ReplaceContentAsync(updated, cancellationToken).ConfigureAwait(false);

            // 读回校验：确认写进去的确实是期望内容，否则用原内容回滚
            step = "校验 hosts 文件";
            string written = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(written, updated, StringComparison.Ordinal))
            {
                await ReplaceContentAsync(original, cancellationToken).ConfigureAwait(false);
                const string corrupt = "hosts 写入后校验失败，已用写入前的内容回滚，文件未被破坏";
                _log.Error($"{corrupt}（{FilePath}）", "HOSTS");
                return HostsApplyResult.Failure(corrupt);
            }

            _log.Info($"{action} hosts 托管块成功（{FilePath}，备份：{backupPath}）", "HOSTS");
            CleanupStrayTempFile();

            (HostsState state, _) = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            return new HostsApplyResult(true, state, successMessage, backupPath);
        }
        catch (Exception ex)
        {
            string message = ex is UnauthorizedAccessException
                ? $"{step}被拒绝：安全软件或系统策略拦截了对 hosts 的修改（{ex.GetType().Name}）"
                : $"{step}失败：{ex.Message}";

            _log.Error($"{action} hosts 失败：{message}", "HOSTS");
            return HostsApplyResult.Failure(message);
        }
    }

    /// <summary>
    /// 原地覆盖 hosts 内容。
    /// <para>
    /// 刻意不用「写临时文件再改名」：同目录改名会被安全软件的 HIPS 驱动拦成
    /// <c>Access denied</c>（火绒实测），而原地写入在所有已测路径下都放行。
    /// </para>
    /// </summary>
    private async Task ReplaceContentAsync(string content, CancellationToken cancellationToken)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        byte[] bytes = encoding.GetBytes(content);

        using var stream = new FileStream(
            FilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 清掉老版本遗留在 hosts 旁边的 <c>*.aln.tmp</c>。
    /// <para>
    /// 旧实现「写临时文件再改名」在被拦截时会失败并留下这个文件，属于无主垃圾，
    /// 每次写入成功后顺带清理一次。
    /// </para>
    /// </summary>
    private void CleanupStrayTempFile()
    {
        try
        {
            string stray = FilePath + ".aln.tmp";
            if (File.Exists(stray))
            {
                File.Delete(stray);
                _log.Info($"已清理遗留的临时文件：{stray}", "HOSTS");
            }
        }
        catch (Exception ex)
        {
            // 清理失败不影响写入结果
            _log.Warn($"清理遗留临时文件失败（不影响注入）：{ex.Message}", "HOSTS");
        }
    }

    /// <summary>把当前 hosts 备份到应用数据目录；失败不影响主流程。</summary>
    private string Backup(string content)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            string path = Path.Combine(
                AppPaths.DataDirectory,
                $"hosts.backup.{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            _log.Warn($"备份 hosts 失败（不影响写入）：{ex.Message}", "HOSTS");
            return string.Empty;
        }
    }

    /// <summary>取出托管块内的映射规则。</summary>
    internal static List<HostsEntry> ExtractManagedEntries(string content)
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

            if (trimmed == HostsFile.BeginMarker)
            {
                insideBlock = true;
                continue;
            }

            if (trimmed == HostsFile.EndMarker)
            {
                break;
            }

            if (insideBlock)
            {
                HostsEntry? entry = HostsEntry.Parse(trimmed);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }
}
