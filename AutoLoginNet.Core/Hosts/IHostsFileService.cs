namespace AutoLoginNet.Hosts;

/// <summary>hosts 文件的当前状态。</summary>
public enum HostsState
{
    /// <summary>未写入托管块。</summary>
    NotApplied,

    /// <summary>已写入托管块，且内容与预期一致。</summary>
    Applied,

    /// <summary>托管块存在但内容已被改动（用户手工编辑过）。</summary>
    Outdated,

    /// <summary>无法读取 hosts 文件。</summary>
    Unknown,
}

/// <summary>hosts 文件状态详情。</summary>
/// <param name="State">状态。</param>
/// <param name="EntryCount">文件中已有映射总条数。</param>
/// <param name="AlreadyMappedCount">
/// 用户手写条目中已覆盖的默认映射数量。等于默认规则总数时，
/// 说明用户此前已手工维护过这些域名，启用功能不会带来变化。
/// </param>
public sealed record HostsStatusInfo(HostsState State, int EntryCount, int AlreadyMappedCount);

/// <summary>hosts 读写结果。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="State">操作后的状态。</param>
/// <param name="Message">说明信息（失败原因）。</param>
/// <param name="BackupPath">本次操作生成的备份文件路径。</param>
public sealed record HostsApplyResult(bool Success, HostsState State, string Message, string BackupPath = "")
{
    public static HostsApplyResult Failure(string message, HostsState state = HostsState.Unknown)
        => new(false, state, message);
}

/// <summary>hosts 文件的读取与写入。</summary>
public interface IHostsFileService
{
    /// <summary>hosts 文件路径。</summary>
    string FilePath { get; }

    /// <summary>内置的校园网域名映射规则。</summary>
    IReadOnlyList<HostsEntry> DefaultEntries { get; }

    /// <summary>读取当前状态，并返回文件中已有的映射规则。</summary>
    Task<(HostsState State, IReadOnlyList<HostsEntry> Entries)> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>读取状态详情（含「用户已手工映射多少条」）。</summary>
    Task<HostsStatusInfo> GetStatusInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>写入托管块（覆盖旧的托管块，保留用户原有内容）。</summary>
    Task<HostsApplyResult> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>移除托管块，恢复用户原始内容。</summary>
    Task<HostsApplyResult> RestoreAsync(CancellationToken cancellationToken = default);
}
