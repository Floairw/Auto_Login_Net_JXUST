using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Pages;
using AutoLoginNet.App.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AutoLoginNet.App;

/// <summary>应用主窗口：NavigationView 外壳 + 页面导航。</summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["network"] = typeof(NetworkPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        InitializeComponent();

        Title = "校园网认证客户端";
        ConfigureWindow();

        // 应用持久化的主题设置
        Theme = new ThemeService(RootGrid);
        Theme.Apply(ThemeService.Parse(App.Services.UiSettings.Current.Theme));

        // 侧栏状态与首页共享同一个连接状态模型
        Connectivity = new ConnectivityViewModel(App.Services);
        Connectivity.StatusChanged += (_, text) => PaneStatusText.Text = text;

        Nav.SelectedItem = Nav.MenuItems[0];
        Navigate("dashboard");
    }

    /// <summary>主题服务，供设置页调用。</summary>
    public ThemeService Theme { get; }

    /// <summary>全局网络连接状态（首页与侧栏共用）。</summary>
    public ConnectivityViewModel Connectivity { get; }

    /// <summary>显示/隐藏全局忙碌指示条。</summary>
    public void SetBusy(bool busy)
        => BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 把窗口显示出来并置于最前。
    /// <para>
    /// 计划任务拉起进程时前台窗口属于别的程序，仅调用 <see cref="Window.Activate"/>
    /// 往往只会让任务栏图标闪烁，因此这里补一次 <c>SetForegroundWindow</c>。
    /// </para>
    /// </summary>
    public void BringToForeground()
    {
        try
        {
            AppWindow.Show();
            Activate();

            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (handle != 0)
            {
                SetForegroundWindow(handle);
            }
        }
        catch
        {
            // 置前失败不影响功能：窗口已由 AppWindow.Show() 显示
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// 最小化窗口到任务栏（用于「登录成功后自动最小化窗口」）。
    /// <para>
    /// 刻意用 <see cref="OverlappedPresenter.Minimize"/> 而不是 <c>Window.Minimize</c>：
    /// WinUI 3 的 Window 没有 Minimize 方法，最小化能力在 AppWindow 的呈现器上。
    /// </para>
    /// </summary>
    public void Minimize()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }
        catch
        {
            // 最小化失败不影响登录结果：窗口保持原状即可
        }
    }

    /// <summary>跳转到指定页面。</summary>
    public void Navigate(string key)
    {
        if (_routes.TryGetValue(key, out Type? pageType) && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            // 系统不支持 Mica（Windows 10）时退回默认背景
            SystemBackdrop = null;
        }

        try
        {
            var appWindow = AppWindow;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 900;
                presenter.PreferredMinimumHeight = 620;
            }

            appWindow.Resize(new SizeInt32(1140, 780));
            appWindow.Title = "校园网认证客户端";
        }
        catch
        {
            // 调整窗口尺寸失败不影响功能
        }
    }
}
