using System.Text.Json;

namespace AutoLoginNet.Hosts;

/// <summary>
/// hosts 操作的执行结果，用于把提权子进程的结果回传给主界面。
/// 与 DNS 操作同样的思路：子进程没有 UI，也无法与主进程直接通信，
/// 因此把结果写入约定文件，主进程等待子进程退出后读取。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Action">执行的动作：apply / restore。</param>
/// <param name="Message">结果说明。</param>
/// <param name="BackupPath">备份文件路径。</param>
/// <param name="CompletedAt">完成时间。</param>
public sealed record HostsOperationResult(
    bool Success,
    string Action,
    string Message,
    string BackupPath,
    DateTimeOffset CompletedAt)
{
    /// <summary>把结果原子写入指定文件。</summary>
    public static void WriteTo(string path, HostsOperationResult result)
    {
        AtomicFile.WriteAllText(
            path,
            JsonSerializer.Serialize(result, AppJsonContext.Default.HostsOperationResult));
    }

    /// <summary>读取结果文件；不存在或损坏时返回 null。</summary>
    public static HostsOperationResult? ReadFrom(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.HostsOperationResult)
                : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>hosts 结果文件的路径约定（主进程与提权子进程共用）。</summary>
public static class HostsResultFile
{
    /// <summary>子进程命令行参数：结果写入路径。</summary>
    public const string ResultPathArgument = "--hosts-result";

    /// <summary>为一次操作分配唯一的结果文件路径。</summary>
    public static string CreatePath()
        => Path.Combine(AppPaths.DataDirectory, $"hosts-result-{Guid.NewGuid():N}.json");
}
