using AutoLoginNet.App.UiSupport;
using AutoLoginNet.App.ViewModels;
using AutoLoginNet.Auth;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoLoginNet.App.Pages;

/// <summary>首页：网络状态 + 登录。</summary>
public sealed partial class DashboardPage : Page
{
    private bool _initializing = true;

    public DashboardPage()
    {
        var mainWindow = App.MainWindow;
        Connectivity = mainWindow?.Connectivity ?? new ConnectivityViewModel(App.Services);

        ViewModel = new DashboardViewModel(App.Services, Connectivity);

        InitializeComponent();

        // 页面缓存：切走再切回首页时复用同一个 Page 与 ViewModel 实例。
        // 否则每次返回都会重建 ViewModel，_autoLoginTriggered 归零 → 重新自动登录并再次落盘凭据。
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        // 密码框没有可绑定的 Password 属性（安全设计），因此手动同步
        PasswordInput.Password = ViewModel.Password;

        OperatorCombo.ItemsSource = Operators.All.Select(kind => new OperatorOption(kind)).ToList();
        int operatorIndex = -1;
        for (int i = 0; i < Operators.All.Count; i++)
        {
            if (Operators.All[i] == ViewModel.Operator)
            {
                operatorIndex = i;
                break;
            }
        }
        OperatorCombo.SelectedIndex = operatorIndex >= 0 ? operatorIndex : Operators.All.Count - 1;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _initializing = false;

        Loaded += OnLoaded;
    }

    public DashboardViewModel ViewModel { get; }

    public ConnectivityViewModel Connectivity { get; }

    /// <summary>
    /// 抑制标记。ToggleSwitch 的 IsOn 是 OneWay 绑定，回读状态时会再次触发 Toggled，
    /// 没有这个标志就会形成「回写 → 事件 → 再操作」的回环。
    /// </summary>
    private bool _suppressAutoStartToggle;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded 会在页面重新进入可视树时再次触发；初始化只做一次，
        // 否则「自动登录 / 自动检测」会被重复执行。
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        UpdateResultBar();
        RefreshAutoStartState();

        // 优先执行「启动时自动登录」：勾选后无需手动点「立即登录」
        if (await ViewModel.TryAutoLoginOnStartupAsync())
        {
            UpdateResultBar();
            MinimizeIfRequested();
            return;
        }

        if (App.Services.UiSettings.Current.AutoDetectOnStartup && !Connectivity.HasResult)
        {
            await ViewModel.DetectAsync();
            UpdateResultBar();
        }
    }

    /// <summary>OnLoaded 是否已执行过（页面被缓存后 Loaded 可能再次触发）。</summary>
    private bool _initialized;

    /// <summary>回读开机自启状态，并同步开关位置。</summary>
    private void RefreshAutoStartState()
    {
        _suppressAutoStartToggle = true;
        ViewModel.RefreshAutoStart();
        AutoStartToggle.IsOn = ViewModel.AutoStartEnabled;
        _suppressAutoStartToggle = false;
    }

    private async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoStartToggle || sender is not ToggleSwitch toggle)
        {
            return;
        }

        App.MainWindow?.SetBusy(true);
        _suppressAutoStartToggle = true;
        try
        {
            await ViewModel.SetAutoStartAsync(toggle.IsOn);
            UpdateResultBar();
        }
        finally
        {
            // 用真实状态回写开关，避免界面与实际情况不一致
            toggle.IsOn = ViewModel.AutoStartEnabled;
            _suppressAutoStartToggle = false;
            App.MainWindow?.SetBusy(false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => UpdateResultBar();

    /// <summary>把登录结果映射到 InfoBar 的严重级别与显示状态。</summary>
    private void UpdateResultBar()
    {
        ResultBar.IsOpen = ViewModel.Stage != LoginStage.Idle || ViewModel.HasResult;
        ResultBar.Severity = ViewModel.Stage switch
        {
            LoginStage.Succeeded => InfoBarSeverity.Success,
            LoginStage.Failed => InfoBarSeverity.Error,
            LoginStage.Detecting or LoginStage.Authenticating => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Informational,
        };
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            ViewModel.Password = PasswordInput.Password;
        }
    }

    private void OperatorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && OperatorCombo.SelectedItem is OperatorOption option)
        {
            ViewModel.Operator = option.Kind;
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DetectAsync();
        UpdateResultBar();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginAsync();
        UpdateResultBar();
        MinimizeIfRequested();
    }

    /// <summary>
    /// 登录成功后按「界面设置 → 登录成功后最小化到系统托盘」把主窗口收进托盘。
    /// <para>
    /// 只处理界面里的登录（手动点击与「启动时自动登录」）；
    /// <c>--autostart</c> 静默登录走 <see cref="StartupRouter.RunSilentLogin"/>，
    /// 由 App.OnLaunched 直接退出进程，不经过这里，因此不会被最小化逻辑影响。
    /// </para>
    /// </summary>
    private void MinimizeIfRequested()
    {
        if (ViewModel.Stage != LoginStage.Succeeded)
        {
            return;
        }

        if (!App.Services.UiSettings.Current.MinimizeAfterLogin)
        {
            return;
        }

        App.MainWindow?.Minimize();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Infrastructure.ShellHelper.ConfirmAsync(
            XamlRoot,
            "注销（断开校园网认证）",
            "注销会立即断开当前认证会话，此后将无法访问外网，校内系统也会无法访问。\n\n确定要注销吗？",
            "确定注销");

        if (!confirmed)
        {
            return;
        }

        await ViewModel.LogoutAsync();
        UpdateResultBar();
    }

    private async void ClearCredential_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Infrastructure.ShellHelper.ConfirmAsync(
            XamlRoot,
            "清除已保存的凭据",
            "清除后开机自启将无法自动登录，需要重新输入密码。确定继续吗？",
            "清除");

        if (!confirmed)
        {
            return;
        }

        ViewModel.ClearCredential();
        PasswordInput.Password = string.Empty;
        UpdateResultBar();
    }

    private void ForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ForgetPassword();
        PasswordInput.Password = string.Empty;
    }
}
