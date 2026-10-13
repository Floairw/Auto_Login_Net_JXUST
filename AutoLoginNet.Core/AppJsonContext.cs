using System.Text.Json.Serialization;

namespace AutoLoginNet;

/// <summary>
/// System.Text.Json 源生成上下文。集中声明全部可序列化类型，
/// 避免运行时反射序列化（同时也是 AOT/裁剪场景的前提）。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Configuration.AppOptions))]
[JsonSerializable(typeof(Configuration.AuthOptions))]
[JsonSerializable(typeof(Configuration.ProbeOptions))]
[JsonSerializable(typeof(Configuration.LoggingOptions))]
[JsonSerializable(typeof(Configuration.StartupOptions))]
[JsonSerializable(typeof(Configuration.ToolsOptions))]
[JsonSerializable(typeof(Hosts.HostsEntry))]
[JsonSerializable(typeof(List<Hosts.HostsEntry>))]
[JsonSerializable(typeof(Credentials.CredentialRecord))]
[JsonSerializable(typeof(Diagnostics.LogEntry))]
[JsonSerializable(typeof(Diagnostics.LogEntry[]))]
[JsonSerializable(typeof(List<Diagnostics.LogEntry>))]
[JsonSerializable(typeof(Auth.LoginResult))]
[JsonSerializable(typeof(Services.AutoStartMarker))]
[JsonSerializable(typeof(DnsOperationResult))]
[JsonSerializable(typeof(Hosts.HostsOperationResult))]
public sealed partial class AppJsonContext : JsonSerializerContext
{
}
