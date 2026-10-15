using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Mvvm;
using AutoLoginNet.Configuration;
using System.Diagnostics;
using System.Windows.Input;

namespace AutoLoginNet.App.ViewModels;

/// <summary>设置页视图模型：界面偏好、开机自启、认证服务器与工具配置。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    private ThemeMode _theme;
    private string _authHost;
    private int _authPort;
    private string _loginPath;
    private string _httpTimeoutSeconds;
    private string _campusSpeedTestUrl;
    private string _internetSpeedTestUrl;
    private bool _autoDetectOnStartup;
    private bool _minimizeAfterLogin;
    private bool _rememberCredential;
    private string _statusMessage = string.Empty;
    private string _statusTitle = string.Empty;
    private bool _isStatusOpen;
    private bool _statusIsError;

    public SettingsViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _theme = ThemeService.Parse(services.UiSettings.Current.Theme);
        _autoDetectOnStartup = services.UiSettings.Current.AutoDetectOnStartup;
        _minimizeAfterLogin = services.UiSettings.Current.MinimizeAfterLogin;
        _rememberCredential = services.UiSettings.Current.RememberCredential;

        AppOptions options = services.Options;
        _authHost = options.Auth.Host;
        _authPort = options.Auth.Port;
        _loginPath = options.Auth.LoginPath;
        _httpTimeoutSeconds = options.Auth.HttpTimeoutSeconds.ToString();
        _campusSpeedTestUrl = options.Tools.CampusSpeedTestUrl;
        _internetSpeedTestUrl = options.Tools.InternetSpeedTestUrl;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        OpenDataFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(AppPaths.DataDirectory));
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile);
        ResetCommand = new AsyncRelayCommand(ResetToDefaultsAsync);
    }

    public ICommand SaveCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenConfigFileCommand { get; }

    public ICommand ResetCommand { get; }

    public string VersionText
        => $"版本 {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "未知"}";

    public string ConfigFilePath => _services.Settings.FilePath;

    public string LogFilePath => _services.Log.LogFilePath;

    public string DataDirectory => AppPaths.DataDirectory;

    public ThemeMode Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                App.MainWindow?.Theme.Apply(value);
                _services.UiSettings.Current.Theme = ThemeService.ToName(value);
                Persist();
            }
        }
    }

    /// <summary>下拉框索引 ↔ 主题（0 跟随系统 / 1 浅色 / 2 深色）。</summary>
    public int ThemeIndex
    {
        get => _theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0,
        };
        set => Theme = value switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    public string AuthHost
    {
        get => _authHost;
        set => SetProperty(ref _authHost, value);
    }

    public int AuthPort
    {
        get => _authPort;
        set => SetProperty(ref _authPort, value);
    }

    public string LoginPath
    {
        get => _loginPath;
        set => SetProperty(ref _loginPath, value);
    }

    public string HttpTimeoutSeconds
    {
        get => _httpTimeoutSeconds;
        set => SetProperty(ref _httpTimeoutSeconds, value);
    }

    public string CampusSpeedTestUrl
    {
        get => _campusSpeedTestUrl;
        set => SetProperty(ref _campusSpeedTestUrl, value);
    }

    public string InternetSpeedTestUrl
    {
        get => _internetSpeedTestUrl;
        set => SetProperty(ref _internetSpeedTestUrl, value);
    }


    public bool AutoDetectOnStartup
    {
        get => _autoDetectOnStartup;
        set
        {
            if (SetProperty(ref _autoDetectOnStartup, value))
            {
                _services.UiSettings.Current.AutoDetectOnStartup = value;
                Persist();
            }
        }
    }

    public bool MinimizeAfterLogin
    {
        get => _minimizeAfterLogin;
        set
        {
            if (SetProperty(ref _minimizeAfterLogin, value))
            {
                _services.UiSettings.Current.MinimizeAfterLogin = value;
                Persist();
            }
        }
    }

    public bool RememberCredential
    {
        get => _rememberCredential;
        set
        {
            if (SetProperty(ref _rememberCredential, value))
            {
                _services.UiSettings.Current.RememberCredential = value;
                Persist();
            }
        }
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        private set => SetProperty(ref _isStatusOpen, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }


    /// <summary>保存认证与工具配置。</summary>
    public async Task SaveAsync()
    {
        if (!int.TryParse(_httpTimeoutSeconds, out int timeout) || timeout is < 1 or > 120)
        {
            ShowStatus("超时时间无效", "HTTP 超时请填写 1–120 之间的整数（秒）。", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_authHost))
        {
            ShowStatus("服务器地址无效", "认证服务器地址不能为空。", isError: true);
            return;
        }

        AppOptions options = _services.Options;
        bool timeoutChanged = options.Auth.HttpTimeoutSeconds != timeout;

        options.Auth.Host = _authHost.Trim();
        options.Auth.Port = Math.Clamp(_authPort, 1, 65535);
        options.Auth.LoginPath = _loginPath.Trim();
        options.Auth.HttpTimeoutSeconds = timeout;
        options.Tools.CampusSpeedTestUrl = _campusSpeedTestUrl.Trim();
        options.Tools.InternetSpeedTestUrl = _internetSpeedTestUrl.Trim();
        options.Normalize();

        _services.Settings.Save(options);

        if (timeoutChanged)
        {
            // HttpClient 的超时无法在实例创建后修改，需要重建
            _services.RecreateHttpClient();
        }

        ShowStatus(
            "设置已保存",
            $"配置已写入 {_services.Settings.FilePath}{(timeoutChanged ? "，HTTP 超时已生效。" : "。")}",
            isError: false);

        await Task.CompletedTask;
    }

    /// <summary>恢复默认配置（不含已保存的凭据）。</summary>
    public async Task ResetToDefaultsAsync()
    {
        _services.Settings.Save(new AppOptions());
        AppOptions defaults = new();
        defaults.Normalize();

        _authHost = defaults.Auth.Host;
        _authPort = defaults.Auth.Port;
        _loginPath = defaults.Auth.LoginPath;
        _httpTimeoutSeconds = defaults.Auth.HttpTimeoutSeconds.ToString();
        _campusSpeedTestUrl = defaults.Tools.CampusSpeedTestUrl;
        _internetSpeedTestUrl = defaults.Tools.InternetSpeedTestUrl;

        OnPropertiesChanged(
            nameof(AuthHost),
            nameof(AuthPort),
            nameof(LoginPath),
            nameof(HttpTimeoutSeconds),
            nameof(CampusSpeedTestUrl),
            nameof(InternetSpeedTestUrl));

        ShowStatus("已恢复默认配置", "重启应用后生效；已保存的凭据未被删除。", isError: false);
        await Task.CompletedTask;
    }

    private void OpenConfigFile()
    {
        try
        {
            if (!File.Exists(_services.Settings.FilePath))
            {
                _services.Settings.Save(_services.Options);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _services.Settings.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus("无法打开配置文件", ex.Message, isError: true);
        }
    }

    private void ShowStatus(string title, string message, bool isError)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusIsError = isError;
        IsStatusOpen = true;
    }

    /// <summary>界面偏好变更后立即落盘（主题等无需点保存）。</summary>
    private void Persist() => _services.UiSettings.Save();
}
