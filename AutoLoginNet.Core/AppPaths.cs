namespace AutoLoginNet;

/// <summary>应用数据路径（全部位于当前用户的漫游 AppData 目录）。</summary>
public static class AppPaths
{
    private const string FolderName = "AutologinForDrcom";

    /// <summary>
    /// 覆盖数据目录的环境变量名。
    /// 打包验证脚本（<c>Setup\Verify-PrunedPublish.ps1</c>）用它把裁剪后的发布目录
    /// 指向一个临时目录，避免写坏真实的 %AppData% 数据；也可以用来做便携式部署。
    /// </summary>
    public const string DataDirectoryOverrideVariable = "AUTOLOGIN_DATA_DIR";

    /// <summary>应用数据目录：默认 %AppData%\AutologinForDrcom，可通过环境变量覆盖。</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>本地运行配置：appsettings.json。</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "appsettings.json");

    /// <summary>DPAPI 加密的登录凭据。</summary>
    public static string CredentialsFile { get; } = Path.Combine(DataDirectory, "credentials.json");

    /// <summary>日志文件。</summary>
    public static string LogFile { get; } = Path.Combine(DataDirectory, "autologin.log");

    /// <summary>确保数据目录存在。</summary>
    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    private static string ResolveDataDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName);
    }
}
