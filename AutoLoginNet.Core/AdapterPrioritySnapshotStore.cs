using AutoLoginNet.Network;
using System.Text.Json;

namespace AutoLoginNet;

/// <summary>
/// 「应用优先网卡」前的原跃点快照存储。
/// <para>
/// 快照必须跨进程、跨重启保留：用户白天切了网卡，晚上重启后仍要能一键恢复原跃点。
/// 因此落盘到应用数据目录，与其他设置文件同目录、同样的原子写。
/// </para>
/// </summary>
public sealed class AdapterPrioritySnapshotStore
{
    /// <summary>默认文件名：%AppData%\AutologinForDrcom\adapter-priority.json。</summary>
    public const string FileName = "adapter-priority.json";

    private readonly string _path;

    /// <summary>用显式路径构造（测试与便携部署用）。</summary>
    public AdapterPrioritySnapshotStore(string path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("快照路径不能为空", nameof(path))
            : path;
    }

    /// <summary>默认存储位置。</summary>
    public static AdapterPrioritySnapshotStore CreateDefault()
        => new(Path.Combine(AppPaths.DataDirectory, FileName));

    /// <summary>快照文件路径。</summary>
    public string FilePath => _path;

    /// <summary>快照文件是否存在。</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>读取快照；不存在或损坏时返回 null。</summary>
    public AdapterPrioritySnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.AdapterPrioritySnapshot);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写入快照。</summary>
    public void Save(AdapterPrioritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.AdapterPrioritySnapshot));
    }

    /// <summary>删除快照（恢复成功后调用）；文件不存在或删除失败都不抛异常。</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // 删除失败不影响恢复结果，下次应用时会覆盖
        }
    }
}
