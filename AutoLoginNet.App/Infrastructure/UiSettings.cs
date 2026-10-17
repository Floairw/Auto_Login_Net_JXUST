using System.Text.Json;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>界面偏好设置（与业务配置分开存放，避免混在一起互相污染）。</summary>
public sealed class UiSettings
{
    /// <summary>主题：System / Light / Dark。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>上次使用的运营商。</summary>
    public string Operator { get; set; } = "Campus";

    /// <summary>登录成功时记住账号。</summary>
    public bool RememberCredential { get; set; } = true;

    /// <summary>每次启动自动检测网络环境。</summary>
    public bool AutoDetectOnStartup { get; set; } = true;

    /// <summary>启动时自动登录，无需手动点「立即登录」。</summary>
    public bool AutoLoginOnStartup { get; set; }

    /// <summary>登录时跳过网络环境检测，直接提交认证。</summary>
    public bool SkipNetworkCheck { get; set; }

    /// <summary>开机自启时最小化到系统托盘，不弹主窗口。</summary>
    public bool StartMinimizedToTray { get; set; }

    /// <summary>关闭主窗口时最小化到系统托盘，而不是退出程序。</summary>
    public bool CloseToTray { get; set; }

    /// <summary>
    /// 手动指定的上网网卡名称；空字符串表示自动（按路由表选择）。
    /// 名称比索引稳定：网卡重新插拔或驱动重装后索引会变，名称通常不变。
    /// </summary>
    public string NetworkAdapterName { get; set; } = string.Empty;
}

/// <summary>界面偏好的读写。</summary>
public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public UiSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    /// <summary>当前设置值（进程内唯一实例，供各页面共享）。</summary>
    public UiSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            Current = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_filePath), Options) ?? new UiSettings()
                : new UiSettings();
        }
        catch
        {
            // 设置损坏时回退默认，保证界面仍可打开
            Current = new UiSettings();
        }
    }

    public void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(Current, Options));
        }
        catch
        {
            // 忽略界面设置写入失败
        }
    }
}
