namespace AutoLoginNet;

/// <summary>应用数据路径（全部位于当前用户的漫游 AppData 目录）。</summary>
public static class AppPaths
{
    private const string FolderName = "AutologinForDrcom";

    /// <summary>应用数据目录：%AppData%\AutologinForDrcom。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        FolderName);

    /// <summary>本地运行配置：appsettings.json。</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "appsettings.json");

    /// <summary>DPAPI 加密的登录凭据。</summary>
    public static string CredentialsFile { get; } = Path.Combine(DataDirectory, "credentials.json");

    /// <summary>日志文件。</summary>
    public static string LogFile { get; } = Path.Combine(DataDirectory, "autologin.log");

    /// <summary>确保数据目录存在。</summary>
    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
