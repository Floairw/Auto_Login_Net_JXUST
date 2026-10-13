using System.Diagnostics;
using AutoLoginNet.Diagnostics;

namespace AutoLoginNet.Services;

/// <summary>
/// 通过计划任务（登录时 + 最高权限）管理开机自启。
/// <para>
/// 创建/删除最高权限任务需要管理员权限，因此调用方（App 层）负责 UAC 提权。
/// 这里额外维护一个「期望状态」标记文件：普通权限下查询计划任务可能被拒绝，
/// 标记文件保证界面开关状态始终与用户意图一致，而不会闪现回退。
/// </para>
/// </summary>
public sealed class ScheduledTaskAutoStartService : IAutoStartService
{
    private const string DefaultTaskName = "AutologinForDrcom";

    private readonly ILogService _log;
    private readonly string? _markerFile;

    public ScheduledTaskAutoStartService(ILogService log, string? markerFile = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _markerFile = markerFile;
    }

    public string? LastError { get; private set; }

    public bool IsEnabled
    {
        get
        {
            // 只读一次标记文件：既用于取任务名，也作为查询失败时的兜底
            AutoStartMarker? marker = ReadMarker();
            string taskName = marker?.TaskName is { Length: > 0 } name ? name : DefaultTaskName;
            bool? taskExists = QueryTaskState(taskName);

            // 能明确查询到时以实际状态为准；查询本身失败（通常是权限不足）才退回标记文件
            return taskExists ?? marker?.Enabled ?? false;
        }
    }

    public async Task<bool> SetEnabledAsync(
        bool enabled,
        AutoStartCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        LastError = null;

        bool ok = enabled
            ? await CreateTaskAsync(command, cancellationToken).ConfigureAwait(false)
            : await DeleteTaskAsync(command, cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            WriteMarker(new AutoStartMarker(enabled, command.TaskName));
            return true;
        }

        LastError ??= enabled
            ? "创建开机自启任务失败，请确认已同意管理员授权"
            : "取消开机自启失败，请确认已同意管理员授权";
        return false;
    }

    private async Task<bool> CreateTaskAsync(AutoStartCommand command, CancellationToken cancellationToken)
    {
        string action = string.IsNullOrWhiteSpace(command.Arguments)
            ? NetUtils.Quote(command.ExecutablePath)
            : $"{NetUtils.Quote(command.ExecutablePath)} {command.Arguments}";

        int exitCode = await RunSchTasksAsync(
            cancellationToken,
            "/Create",
            "/TN", command.TaskName,
            "/TR", action,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F").ConfigureAwait(false);

        if (exitCode == 0)
        {
            _log.Info($"已启用开机自启（计划任务 {command.TaskName}，最高权限）：{command.ExecutablePath}", "开机自启");
            return true;
        }

        LastError = exitCode < 0
            ? "无法启动 schtasks.exe，可能被安全软件拦截"
            : $"schtasks 创建任务失败（退出码 {exitCode}），通常表示未获得管理员权限";
        _log.Error(LastError, "开机自启");
        return false;
    }

    private async Task<bool> DeleteTaskAsync(AutoStartCommand command, CancellationToken cancellationToken)
    {
        int exitCode = await RunSchTasksAsync(
            cancellationToken,
            "/Delete",
            "/TN", command.TaskName,
            "/F").ConfigureAwait(false);

        // 任务本来就不存在时同样视为成功
        if (exitCode == 0 || QueryTaskState(command.TaskName) == false)
        {
            _log.Info("已取消开机自启", "开机自启");
            return true;
        }

        LastError = exitCode < 0
            ? "无法启动 schtasks.exe，可能被安全软件拦截"
            : $"schtasks 删除任务失败（退出码 {exitCode}），通常表示未获得管理员权限";
        _log.Error(LastError, "开机自启");
        return false;
    }

    /// <summary>查询计划任务是否存在；返回 null 表示查询本身失败（通常是权限不足）。</summary>
    private static bool? QueryTaskState(string taskName)
    {
        int exitCode = RunSchTasks("/Query", "/TN", taskName);
        if (exitCode == 0)
        {
            return true;
        }

        // 退出码 1 表示「找不到任务」，属于可判定的否定结果
        return exitCode == 1 ? false : null;
    }

    private static async Task<int> RunSchTasksAsync(CancellationToken cancellationToken, params string[] args)
        => await Task.Run(() => RunSchTasks(args), cancellationToken).ConfigureAwait(false);

    private static int RunSchTasks(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return -1;
            }

            // 必须先读完输出，否则进程可能因管道写满而阻塞
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(10000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略终止失败
                }
                return -1;
            }

            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private AutoStartMarker? ReadMarker()
    {
        if (_markerFile is null)
        {
            return null;
        }

        try
        {
            return File.Exists(_markerFile)
                ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(_markerFile), AppJsonContext.Default.AutoStartMarker)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteMarker(AutoStartMarker marker)
    {
        if (_markerFile is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_markerFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(
                _markerFile,
                System.Text.Json.JsonSerializer.Serialize(marker, AppJsonContext.Default.AutoStartMarker));
        }
        catch
        {
            // 标记文件仅用于界面状态回显，失败不影响主流程
        }
    }
}
