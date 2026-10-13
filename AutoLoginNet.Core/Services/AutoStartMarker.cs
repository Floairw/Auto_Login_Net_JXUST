using System.Text.Json.Serialization;

namespace AutoLoginNet.Services;

/// <summary>
/// 开机自启的「期望状态」标记文件内容。
/// <para>
/// 独立为顶层类型而非嵌套类：嵌套类配合默认值初始化器时，一旦反序列化键名不匹配，
/// 属性会静默保留初始值（曾因此把宿主机上真实存在的同名任务当成「已启用」），
/// 顶层 + 显式 <see cref="JsonPropertyNameAttribute"/> 让键名不再依赖命名策略。
/// </para>
/// <para>
/// <see cref="TaskName"/> 故意<b>不设</b>初始化器：反序列化失败时它会是 null，
/// 调用方必须显式兜底，从而不会掩盖问题。
/// </para>
/// </summary>
public sealed class AutoStartMarker
{
    public AutoStartMarker()
    {
    }

    public AutoStartMarker(bool enabled, string taskName)
    {
        Enabled = enabled;
        TaskName = taskName;
    }

    /// <summary>用户期望的自启状态。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>计划任务名称；为 null 表示标记文件缺失或损坏。</summary>
    [JsonPropertyName("taskName")]
    public string? TaskName { get; set; }
}
