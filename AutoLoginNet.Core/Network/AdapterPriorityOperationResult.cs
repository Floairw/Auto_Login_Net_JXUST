using System.Text.Json;

namespace AutoLoginNet.Network;

/// <summary>
/// 优先网卡设置结果。与 <see cref="DnsOperationResult"/> 同样的「文件协议」：
/// 提权子进程没有界面，把结果写入约定文件，主进程轮询读取。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Action">执行的动作：apply / restore。</param>
/// <param name="AdapterDisplay">目标网卡展示名（恢复时为首选网卡名）。</param>
/// <param name="Message">结果说明（失败时包含原因）。</param>
/// <param name="CompletedAt">完成时间。</param>
public sealed record AdapterPriorityOperationResult(
    bool Success,
    string Action,
    string AdapterDisplay,
    string Message,
    DateTimeOffset CompletedAt)
{
    /// <summary>把结果原子写入指定文件。</summary>
    public static void WriteTo(string path, AdapterPriorityOperationResult result)
    {
        AtomicFile.WriteAllText(
            path,
            JsonSerializer.Serialize(result, AppJsonContext.Default.AdapterPriorityOperationResult));
    }

    /// <summary>读取结果文件；不存在或损坏时返回 null。</summary>
    public static AdapterPriorityOperationResult? ReadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AdapterPriorityOperationResult);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>优先网卡结果文件的路径约定（主进程与提权子进程共用）。</summary>
public static class AdapterPriorityResultFile
{
    /// <summary>子进程命令行参数：结果写入路径。</summary>
    public const string ResultPathArgument = "--adapter-priority-result";

    /// <summary>为一次操作分配唯一的结果文件路径。</summary>
    public static string CreatePath()
        => Path.Combine(AppPaths.DataDirectory, $"adapter-priority-result-{Guid.NewGuid():N}.json");
}
