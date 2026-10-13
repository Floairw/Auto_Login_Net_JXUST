using System.Text.Json;

namespace AutoLoginNet;

/// <summary>
/// DNS 设置结果。用于把「提权子进程」的执行结果回传给主界面：
/// 子进程没有 UI，也无法与主进程直接通信，因此把结果写入约定文件，主进程轮询读取。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Action">执行的动作：apply / automatic。</param>
/// <param name="AdapterDisplay">目标网卡展示名。</param>
/// <param name="Message">结果说明（失败时包含原因）。</param>
/// <param name="CompletedAt">完成时间。</param>
public sealed record DnsOperationResult(
    bool Success,
    string Action,
    string AdapterDisplay,
    string Message,
    DateTimeOffset CompletedAt)
{
    /// <summary>把结果原子写入指定文件。</summary>
    public static void WriteTo(string path, DnsOperationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, AppJsonContext.Default.DnsOperationResult));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>读取结果文件；不存在或损坏时返回 null。</summary>
    public static DnsOperationResult? ReadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.DnsOperationResult);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>DNS 结果文件的路径约定（主进程与提权子进程共用）。</summary>
public static class DnsResultFile
{
    /// <summary>子进程命令行参数：结果写入路径。</summary>
    public const string ResultPathArgument = "--dns-result";

    /// <summary>为一次操作分配唯一的结果文件路径。</summary>
    public static string CreatePath()
        => Path.Combine(AppPaths.DataDirectory, $"dns-result-{Guid.NewGuid():N}.json");
}
