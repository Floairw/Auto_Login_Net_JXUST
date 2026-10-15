using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Mvvm;
using AutoLoginNet.Auth;
using AutoLoginNet.Credentials;
using AutoLoginNet.Network;
using AutoLoginNet.Services;
using System.Windows.Input;

namespace AutoLoginNet.App.ViewModels;

/// <summary>登录按钮的当前状态。</summary>
public enum LoginStage
{
    /// <summary>空闲，可点击。</summary>
    Idle,

    /// <summary>正在检测网络。</summary>
    Detecting,

    /// <summary>正在提交认证。</summary>
    Authenticating,

    /// <summary>正在注销（断开认证）。</summary>
    LoggingOut,

    /// <summary>已成功（含已在线）。</summary>
    Succeeded,

    /// <summary>失败。</summary>
    Failed,
}

/// <summary>
/// 首页视图模型：把「检测网络 → 提交认证 → 反馈结果」组织成一次可观察的操作，
/// 并统一维护账号/密码/运营商表单状态。
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ConnectivityViewModel _connectivity;

    private string _userName = string.Empty;
    private string _password = string.Empty;
    private OperatorKind _operator = OperatorKind.Campus;
    private bool _rememberCredential = true;
    private bool _hasSavedCredential;
    private bool _skipNetworkCheck;
    private LoginStage _stage = LoginStage.Idle;
    private LoginResult? _lastResult;
    private string _statusTitle = "准备就绪";
    private string _statusMessage = "填写账号密码后点击「立即登录」，或直接检测当前网络状态。";
    private bool _hasSavedPassword;
    private bool _autoStartEnabled;
    private bool _autoLoginOnStartup;
    private bool _autoLoginTriggered;

    public DashboardViewModel(AppServices services, ConnectivityViewModel connectivity)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));

        _rememberCredential = _services.UiSettings.Current.RememberCredential;

        // 这两个开关的状态在启动时恢复，因此「跳过网络检测」不需要每次重新勾选
        _skipNetworkCheck = _services.UiSettings.Current.SkipNetworkCheck;
        _autoLoginOnStartup = _services.UiSettings.Current.AutoLoginOnStartup;

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => CanLogin);
        DetectCommand = new AsyncRelayCommand(DetectAsync, () => !IsBusy);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => CanLogout);
        ClearCredentialCommand = new RelayCommand(ClearCredential, () => HasSavedCredential);
        ForgetPasswordCommand = new RelayCommand(ForgetPassword, () => _hasSavedPassword);

        // 连通性变化直接影响「能否注销」，需要同步刷新按钮可用状态
        _connectivity.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanLogout));
            (LogoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        };

        LoadSavedCredential();
    }

    /// <summary>登录命令。</summary>
    public ICommand LoginCommand { get; }

    /// <summary>重新检测网络命令。</summary>
    public ICommand DetectCommand { get; }

    /// <summary>清除已保存凭据命令。</summary>
    public ICommand ClearCredentialCommand { get; }

    /// <summary>仅清除已填充的密码。</summary>
    public ICommand ForgetPasswordCommand { get; }

    /// <summary>注销命令。</summary>
    public ICommand LogoutCommand { get; }

    // ---------------- 开机自启 ----------------

    /// <summary>是否已启用开机自启。</summary>
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        private set
        {
            if (SetProperty(ref _autoStartEnabled, value))
            {
                OnPropertyChanged(nameof(AutoStartStateText));
            }
        }
    }

    /// <summary>开机自启状态的一行说明。</summary>
    public string AutoStartStateText => _autoStartEnabled
        ? "已注册「登录时」触发的高优先级计划任务"
        : "未注册计划任务，开机后需要手动登录";

    /// <summary>刷新开机自启状态（页面每次显示时调用）。</summary>
    public void RefreshAutoStart()
    {
        AutoStartEnabled = _services.AutoStart.IsEnabled;
    }

    /// <summary>
    /// 把开机自启设置为 <paramref name="enabled"/>。
    /// 刻意接收目标值而不是自行取反：ToggleSwitch 的 IsOn 是 OneWay 绑定，
    /// 程序性回写会再次触发 Toggled，传入明确目标状态可彻底避开这类回环。
    /// </summary>
    public async Task SetAutoStartAsync(bool enabled)
    {
        if (enabled == AutoStartEnabled)
        {
            return;
        }

        var command = new AutoStartCommand(ShellHelper.ExecutablePath, StartupRouter.AutoStartArgument);

        if (ShellHelper.IsElevated())
        {
            bool ok = await _services.AutoStart.SetEnabledAsync(enabled, command);
            RefreshAutoStart();

            StatusTitle = ok ? "开机自启已更新" : "开机自启设置失败";
            StatusMessage = ok
                ? (enabled ? "已注册「登录时」触发的高优先级计划任务。" : "已移除开机自启计划任务。")
                : _services.AutoStart.LastError ?? "未知错误";
            return;
        }

        // 非管理员：由提权进程完成注册
        System.Diagnostics.Process? elevated = ShellHelper.RunElevated(
            ElevatedCommands.RegisterAutoStart, enabled ? "1" : "0");

        if (elevated is null)
        {
            StatusTitle = "已取消";
            StatusMessage = "未获得管理员授权，开机自启设置保持不变。";
            RefreshAutoStart();
            return;
        }

        StatusTitle = "已请求管理员授权";
        StatusMessage = enabled
            ? "在 UAC 窗口中确认后，登录时将自动完成认证。"
            : "在 UAC 窗口中确认后，开机自启将被移除。";

        // 等待提权进程真正退出后再回读状态，避免用固定延时猜时间
        try
        {
            await elevated.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);
        }
        catch (OperationCanceledException)
        {
            // 超时也回读一次，界面以真实状态为准
        }

        RefreshAutoStart();
    }

    public string UserName
    {
        get => _userName;
        set
        {
            if (SetProperty(ref _userName, value))
            {
                OnPropertyChanged(nameof(CanLogin));
                (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                OnPropertyChanged(nameof(CanLogin));
                (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public OperatorKind Operator
    {
        get => _operator;
        set
        {
            if (SetProperty(ref _operator, value))
            {
                App.Services.UiSettings.Current.Operator = value.ToString();
                App.Services.UiSettings.Save();
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
                App.Services.UiSettings.Current.RememberCredential = value;
                App.Services.UiSettings.Save();
            }
        }
    }

    /// <summary>
    /// 跳过网络检测直接认证（网络探测被拦截时的逃生通道）。
    /// 该选择会被记住，下次启动仍然生效。
    /// </summary>
    public bool SkipNetworkCheck
    {
        get => _skipNetworkCheck;
        set
        {
            if (SetProperty(ref _skipNetworkCheck, value))
            {
                App.Services.UiSettings.Current.SkipNetworkCheck = value;
                App.Services.UiSettings.Save();
            }
        }
    }

    /// <summary>
    /// 启动时自动登录。勾选后每次打开程序都会直接用已保存的凭据登录。
    /// </summary>
    public bool AutoLoginOnStartup
    {
        get => _autoLoginOnStartup;
        set
        {
            if (SetProperty(ref _autoLoginOnStartup, value))
            {
                App.Services.UiSettings.Current.AutoLoginOnStartup = value;
                App.Services.UiSettings.Save();
            }
        }
    }

    /// <summary>
    /// 本次启动是否应当自动登录：用户已开启该选项，且本机确实保存了可用凭据。
    /// </summary>
    public bool ShouldAutoLoginOnStartup => _autoLoginOnStartup && HasSavedCredential;

    public LoginStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertiesChanged(
                    nameof(IsBusy),
                    nameof(LoginButtonText),
                    nameof(LogoutButtonText),
                    nameof(CanLogin),
                    nameof(CanDetect),
                    nameof(CanLogout));
                (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (LogoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public LoginResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (SetProperty(ref _lastResult, value))
            {
                OnPropertiesChanged(nameof(HasResult), nameof(RawResponse), nameof(HasRawResponse));
            }
        }
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

    public bool HasSavedCredential
    {
        get => _hasSavedCredential;
        private set
        {
            if (SetProperty(ref _hasSavedCredential, value))
            {
                (ClearCredentialCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSavedPassword
    {
        get => _hasSavedPassword;
        private set
        {
            if (SetProperty(ref _hasSavedPassword, value))
            {
                (ForgetPasswordCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasResult => _lastResult is not null;

    public string RawResponse => _lastResult?.RawResponse ?? string.Empty;

    public bool HasRawResponse => !string.IsNullOrWhiteSpace(_lastResult?.RawResponse);

    public bool IsBusy => _stage is LoginStage.Detecting or LoginStage.Authenticating or LoginStage.LoggingOut;

    /// <summary>是否可以发起登录（直接绑定到登录按钮的 IsEnabled）。</summary>
    public bool CanLogin => !IsBusy
        && !string.IsNullOrWhiteSpace(_userName)
        && !string.IsNullOrEmpty(_password);

    /// <summary>是否可以重新检测网络。</summary>
    public bool CanDetect => !IsBusy;

    /// <summary>
    /// 是否可以注销。
    /// 只在明确检测到「外网可达」时可用——注销会断开网络，
    /// 不能让用户在离线状态下误点而白白丢掉恢复能力。
    /// </summary>
    public bool CanLogout => !IsBusy && _connectivity.IsOnline;

    public string LoginButtonText => _stage switch
    {
        LoginStage.Detecting => "正在检测网络…",
        LoginStage.Authenticating => "正在认证…",
        LoginStage.LoggingOut => "正在处理…",
        LoginStage.Succeeded => "登录成功",
        LoginStage.Failed => "登录失败，点击重试",
        _ => "立即登录",
    };

    /// <summary>注销按钮文案。</summary>
    public string LogoutButtonText => _stage == LoginStage.LoggingOut ? "正在注销…" : "注销（断开认证）";

    /// <summary>读取已保存的凭据并填充表单。</summary>
    public void LoadSavedCredential()
    {
        LoginCredential? saved = _services.CredentialStore.Load();
        HasSavedCredential = saved is not null;

        if (saved is null)
        {
            _operator = ParseOperator(App.Services.UiSettings.Current.Operator);
            OnPropertyChanged(nameof(Operator));
            return;
        }

        UserName = saved.UserName;
        Password = saved.Password;
        HasSavedPassword = true;
        Operator = saved.Operator;
    }

    /// <summary>
    /// 在页面首次显示时按需自动登录。
    /// 先用 <c>_autoLoginTriggered</c> 拦截同一实例内的重复调用，
    /// 再向 <see cref="AppServices.TryClaimStartupAutoLogin"/> 认领本次运行唯一的自动登录机会——
    /// 后者才是真正的防线：返回首页时页面与视图模型都可能被重建，
    /// 仅靠实例字段会导致反复自动登录（从而反复保存凭据、反复认证）。
    /// </summary>
    /// <returns>是否真的发起了自动登录。</returns>
    public async Task<bool> TryAutoLoginOnStartupAsync()
    {
        if (_autoLoginTriggered || !ShouldAutoLoginOnStartup)
        {
            return false;
        }

        _autoLoginTriggered = true;

        if (!_services.TryClaimStartupAutoLogin())
        {
            return false;
        }

        StatusTitle = "正在自动登录…";
        StatusMessage = "已开启启动时自动登录，正在使用已保存的凭据认证。";

        await LoginAsync();
        return true;
    }

    /// <summary>检测网络环境。</summary>
    public async Task DetectAsync()
    {
        Stage = LoginStage.Detecting;
        StatusTitle = "正在检测网络环境…";
        StatusMessage = "同时探测校园网认证服务器与外网连通性。";
        try
        {
            NetworkSnapshot snapshot = await _connectivity.RefreshAsync();
            StatusTitle = snapshot.Requirement switch
            {
                AuthRequirement.Required => "需要认证",
                AuthRequirement.NotRequired => "无需认证",
                _ => "无法确定网络环境",
            };
            StatusMessage = snapshot.Conclusion;
        }
        finally
        {
            Stage = Stage == LoginStage.Detecting ? LoginStage.Idle : Stage;
        }
    }

    /// <summary>
    /// 执行登录。对「瞬时故障」（超时、连不上认证服务器）按配置重试，
    /// 因为刚开机或网络切换时认证服务器常常还没就绪。
    /// </summary>
    public async Task LoginAsync()
    {
        if (!CanLogin)
        {
            return;
        }

        int maxRetries = _services.Options.Startup.LoginRetryCount;
        int retryDelayMs = _services.Options.Startup.LoginRetryDelaySeconds * 1000;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                Stage = LoginStage.Authenticating;
                StatusTitle = $"正在重试（{attempt}/{maxRetries}）…";
                StatusMessage = $"上一次连接认证服务器失败，{retryDelayMs / 1000} 秒后重试。";
                await Task.Delay(retryDelayMs);
            }

            LoginResult result = await AttemptLoginOnceAsync();

            if (!result.IsTransient)
            {
                LastResult = result;
                ApplyResult(result);
                return;
            }

            LastResult = result;

            // 瞬时故障且还有重试机会时继续；否则用最后一次结果收尾
            if (attempt == maxRetries)
            {
                ApplyResult(result);
                return;
            }
        }
    }

    /// <summary>执行一次「检测网络（可选）+ 提交认证」。</summary>
    private async Task<LoginResult> AttemptLoginOnceAsync()
    {
        if (!SkipNetworkCheck)
        {
            Stage = LoginStage.Detecting;
            StatusTitle = "正在检测网络环境…";
            StatusMessage = "登录前确认当前网络是否需要认证。";

            NetworkSnapshot snapshot = await _connectivity.RefreshAsync();
            if (snapshot.Requirement == AuthRequirement.Unknown)
            {
                StatusMessage = "网络状态未知，仍将尝试认证。";
            }
        }

        Stage = LoginStage.Authenticating;
        StatusTitle = "正在认证…";
        StatusMessage = $"正在向 {_services.Options.Auth.Host}:{_services.Options.Auth.Port} 提交登录请求。";

        try
        {
            LoginAttempt attempt = await _services.LoginService.LoginAsync(
                UserName,
                Password,
                Operator,
                SkipNetworkCheck);

            return attempt.Result;
        }
        catch (Exception ex)
        {
            _services.Log.Error($"登录流程异常：{ex}", "登录");
            return LoginResult.NetworkFailure($"{ex.GetType().Name}: {ex.Message}", TimeSpan.Zero);
        }
    }

    /// <summary>清除已保存凭据。</summary>
    public void ClearCredential()
    {
        _services.CredentialStore.Clear();
        HasSavedCredential = false;
        HasSavedPassword = false;
        Password = string.Empty;
        StatusTitle = "已清除保存的凭据";
        StatusMessage = "下次登录需要重新输入密码。";
    }

    /// <summary>只清空输入框中的密码。</summary>
    public void ForgetPassword()
    {
        Password = string.Empty;
        HasSavedPassword = false;
    }

    /// <summary>注销（断开校园网在线认证）。</summary>
    public async Task LogoutAsync()
    {
        if (!CanLogout)
        {
            return;
        }

        Stage = LoginStage.LoggingOut;
        StatusTitle = "正在注销…";
        StatusMessage = "正在向认证服务器请求断开当前会话。";

        try
        {
            LogoutResult result = await _services.LogoutService.LogoutAsync();

            Stage = result.IsSuccess ? LoginStage.Idle : LoginStage.Failed;
            StatusTitle = result.Title;
            StatusMessage = result.Status switch
            {
                LogoutStatus.Success => "已断开认证，当前无法访问外网；可随时重新登录。",
                LogoutStatus.NotOnline => result.Detail,
                LogoutStatus.NetworkError => result.Detail,
                _ => string.IsNullOrWhiteSpace(result.Detail)
                    ? "注销被服务器拒绝，请稍后重试。"
                    : result.Detail,
            };

            LastResult = null;
        }
        catch (Exception ex)
        {
            _services.Log.Error($"注销流程异常：{ex}", "注销");
            Stage = LoginStage.Failed;
            StatusTitle = "注销过程出错";
            StatusMessage = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (Stage == LoginStage.LoggingOut)
            {
                Stage = LoginStage.Idle;
            }

            // 注销会改变连通性，立刻回读一次真实状态
            await _connectivity.RefreshAsync();
        }
    }

    private void ApplyResult(LoginResult result)
    {
        if (result.IsSuccess)
        {
            Stage = LoginStage.Succeeded;
            StatusTitle = result.Title;
            StatusMessage = result.Status == LoginStatus.AlreadyOnline
                ? "该账号已经在线上，无需重复登录。"
                : $"认证成功，耗时 {result.Elapsed.TotalMilliseconds:F0} ms。";
            _connectivity.MarkOnline();

            if (!RememberCredential)
            {
                // 用户选择不记住密码时，立即清除刚刚落盘的凭据
                _services.CredentialStore.Clear();
            }

            HasSavedCredential = _services.CredentialStore.Exists;
            return;
        }

        Stage = LoginStage.Failed;
        StatusTitle = result.Title;
        StatusMessage = BuildFailureMessage(result);
    }

    private static string BuildFailureMessage(LoginResult result)
    {
        string detail = result.Detail;
        return result.Status switch
        {
            LoginStatus.InvalidCredentials =>
                string.IsNullOrWhiteSpace(detail) ? "请检查账号与所选运营商是否正确。" : detail,
            LoginStatus.AccountSuspended =>
                string.IsNullOrWhiteSpace(detail) ? "账号已欠费或被停用，请联系网络中心。" : detail,
            LoginStatus.NetworkError =>
                $"{(string.IsNullOrWhiteSpace(detail) ? "无法连接认证服务器。" : detail)}\n排查建议：确认已连接校园网；或在设置中核对认证服务器地址。",
            _ => string.IsNullOrWhiteSpace(detail) ? "认证被拒绝，请稍后重试。" : detail,
        };
    }

    private static OperatorKind ParseOperator(string? name)
        => Enum.TryParse<OperatorKind>(name, ignoreCase: true, out var parsed) ? parsed : OperatorKind.Campus;
}
