using System.Text.Json.Serialization;

namespace AutoLoginNet;

/// <summary>
/// System.Text.Json 源生成上下文。只声明真正作为序列化根的类型，
/// 其余类型由根类型传递生成，避免运行时反射序列化（同时也是 AOT/裁剪场景的前提）。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Configuration.AppOptions))]
[JsonSerializable(typeof(Credentials.CredentialRecord))]
[JsonSerializable(typeof(Services.AutoStartMarker))]
[JsonSerializable(typeof(DnsOperationResult))]
[JsonSerializable(typeof(Hosts.HostsOperationResult))]
[JsonSerializable(typeof(Network.AdapterPrioritySnapshot))]
[JsonSerializable(typeof(Network.AdapterPriorityOperationResult))]
public sealed partial class AppJsonContext : JsonSerializerContext
{
}
