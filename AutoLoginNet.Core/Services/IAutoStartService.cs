namespace AutoLoginNet.Services;

/// <summary>描述要注册为开机自启的进程。</summary>
/// <param name="ExecutablePath">可执行文件完整路径。</param>
/// <param name="Arguments">命令行参数。</param>
/// <param name="TaskName">计划任务名称。</param>
/// <param name="Description">任务描述。</param>
public sealed record AutoStartCommand(
    string ExecutablePath,
    string Arguments,
    string TaskName = "AutologinForDrcom",
    string Description = "校园网认证客户端自动登录");

/// <summary>管理开机自启。</summary>
public interface IAutoStartService
{
    /// <summary>当前是否已启用开机自启。</summary>
    bool IsEnabled { get; }

    /// <summary>最近一次操作失败的原因；成功时为 null。</summary>
    string? LastError { get; }

    /// <summary>启用/取消开机自启。</summary>
    Task<bool> SetEnabledAsync(bool enabled, AutoStartCommand command, CancellationToken cancellationToken = default);
}