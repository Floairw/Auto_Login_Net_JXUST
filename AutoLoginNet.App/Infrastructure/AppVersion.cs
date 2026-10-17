using System.Reflection;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>
/// 界面与日志里显示的版本号。
/// 取 <c>AssemblyInformationalVersion</c>（csproj 的 <c>&lt;InformationalVersion&gt;</c>，例如 2.0.9.1.dev），
/// 取不到时退回程序集版本的前三段。
/// 注意：MSI 的 ProductVersion 只能纯数字，所以「ARP 里显示的版本」与这里显示的字符串刻意不同
/// （见 AutoLoginNet.App.csproj 的版本注释）。
/// </summary>
internal static class AppVersion
{
    /// <summary>完整版本字符串，例如 <c>2.0.9.1.dev</c>。</summary>
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = typeof(AppVersion).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "未知";
    }
}
