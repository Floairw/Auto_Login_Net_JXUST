using System.Runtime.InteropServices;
using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Pages;
using AutoLoginNet.App.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AutoLoginNet.Network;
using Windows.Graphics;

namespace AutoLoginNet.App;

/// <summary>应用主窗口：NavigationView 外壳 + 页面导航。</summary>
public sealed partial class MainWindow : Window
{
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_MINIMIZE = 0xF020;

    /// <summary>窗口子类化标识（同一窗口内的唯一 id，随便取的固定值）。</summary>
    private const uint SubclassId = 0xA11;

    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["network"] = typeof(NetworkPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage),
    };

    /// <summary>托盘图标；<c>--autostart</c> 路径也会创建（登录后常驻托盘，不弹窗口）。</summary>
    private readonly TrayIcon? _tray;

    /// <summary>上网网卡选择（托盘菜单与网络页共用同一份偏好）。</summary>
    private readonly AdapterSwitcher _adapterSwitcher;

    private bool _hiddenToTray;
    private bool _exiting;
    private IntPtr _hwnd;
    private SubclassProc? _subclassProc;
    private bool _activateRetryHooked;

    /// <param name="enableTray">是否创建托盘图标。</param>
    /// <param name="startHidden">是否启动即藏进托盘（开机自启且勾选了「最小化到托盘」时使用）。</param>
    public MainWindow(bool enableTray = true, bool startHidden = false)
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

        _adapterSwitcher = new AdapterSwitcher(App.Services);

        // 托盘：左键/双击还原窗口，右键菜单可还原、切换上网网卡或退出
        _tray = enableTray
            ? new TrayIcon(
                "校园网认证客户端",
                ShowFromTray,
                ExitFromTray,
                BuildTrayAdapterMenu,
                OnTrayAdapterSelected)
            : null;

        // 拦截「最小化」：标题栏的最小化按钮不再缩到任务栏，而是直接藏进托盘
        InstallMinimizeInterceptor();
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;

        if (startHidden)
        {
            // 开机自启：登录已在后台完成，窗口直接收进托盘，等用户点托盘图标再显示
            HideToTray();
        }
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
    /// 窗口若正藏在托盘里（第二次启动的重定向激活就是这种情况），先从托盘还原。
    /// </para>
    /// </summary>
    public void BringToForeground()
    {
        if (_hiddenToTray)
        {
            ShowFromTray();
            return;
        }

        try
        {
            AppWindow.Show();
            Activate();

            IntPtr handle = GetWindowHandle();
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

    /// <summary>把主窗口藏进系统托盘（任务栏与 Alt+Tab 中都不再出现）。</summary>
    private void HideToTray()
    {
        if (_hiddenToTray)
        {
            return;
        }

        // 托盘不可用时退回普通最小化——否则窗口藏起来就再也点不回来了
        if (_tray is null || !_tray.IsAvailable)
        {
            MinimizeToTaskbar();
            return;
        }

        try
        {
            AppWindow.Hide();
            _hiddenToTray = true;
        }
        catch
        {
            _hiddenToTray = false;
            MinimizeToTaskbar();
        }
    }

    /// <summary>
    /// 从托盘还原主窗口并置于最前。
    /// <para>托盘左键/双击、菜单「显示主窗口」、以及第二次启动的重定向激活都走这里。</para>
    /// </summary>
    private void ShowFromTray()
    {
        try
        {
            AppWindow.Show();

            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            Activate();

            IntPtr handle = GetWindowHandle();
            if (handle != 0)
            {
                SetForegroundWindow(handle);
            }
        }
        catch
        {
            // 还原失败时窗口已由 AppWindow.Show() 显示，不影响继续使用
        }
        finally
        {
            _hiddenToTray = false;
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

    /// <summary>
    /// 给主窗口挂一层原生子类过程，拦截「最小化」命令。
    /// <para>
    /// WinUI 3 没有「最小化到托盘」的原生入口，也拿不到可重写的 WndProc；
    /// 这里用 <c>SetWindowSubclass</c> 截 <c>WM_SYSCOMMAND / SC_MINIMIZE</c>，
    /// 直接藏进托盘并吞掉该命令（窗口不会先缩到任务栏再消失）。
    /// 子类化失败时由 <see cref="OnAppWindowChanged"/> 兜底。
    /// </para>
    /// </summary>
    private void InstallMinimizeInterceptor()
    {
        try
        {
            _hwnd = GetWindowHandle();
            if (_hwnd == 0)
            {
                // 构造阶段极少数情况下还拿不到 HWND：等首次激活再补一次
                if (!_activateRetryHooked)
                {
                    _activateRetryHooked = true;
                    Activated += (_, _) =>
                    {
                        if (_subclassProc is null)
                        {
                            InstallMinimizeInterceptor();
                        }
                    };
                }

                return;
            }

            _subclassProc = MinimizeSubclassProc;
            SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);
        }
        catch
        {
            // 子类化失败时退化为「先最小化，再由 AppWindow.Changed 感知并隐藏到托盘」
        }
    }

    private IntPtr MinimizeSubclassProc(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint idSubclass,
        IntPtr refData)
    {
        // 标题栏的最小化按钮（含 Alt+Space 菜单、Win+↓）：直接藏进托盘
        if (msg == WM_SYSCOMMAND && (uint)(wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
        {
            HideToTray();
            return 0;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>子类化没生效（或最小化由系统直接触发）时的兜底：感知到最小化后改成藏托盘。</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        try
        {
            if (_hiddenToTray || !args.DidPresenterChange)
            {
                return;
            }

            if (sender.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                HideToTray();
            }
        }
        catch
        {
            // 兜底逻辑失败不影响窗口继续使用
        }
    }

    /// <summary>
    /// 关闭主窗口：按设置决定「收进托盘」还是「退出程序」。
    /// <para>
    /// 拦截的是 <see cref="AppWindow"/> 的 Closing（标题栏关闭按钮与 Alt+F4 都会走这条路），
    /// 取消后进程继续在托盘常驻，登录状态不受影响。
    /// 托盘图标没能出现时不吞掉关闭，否则窗口会关不掉。
    /// </para>
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            if (!App.Services.UiSettings.Current.CloseToTray || _tray is null || !_tray.IsAvailable)
            {
                return;
            }

            args.Cancel = true;
            HideToTray();
        }
        catch
        {
            // 读取设置失败时按「关闭即退出」处理
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) => DisposeTray();

    /// <summary>托盘右键菜单「上网网卡」的候选列表（枚举网卡很轻，直接在弹菜单时取）。</summary>
    private TrayAdapterMenu BuildTrayAdapterMenu()
    {
        var items = new List<TrayAdapterItem>();

        foreach (AdapterInfo adapter in _adapterSwitcher.ListAdapters())
        {
            string label = adapter.Display;
            if (label.Length > 48)
            {
                label = label[..47] + "…";
            }

            items.Add(new TrayAdapterItem(adapter.Name, label));
        }

        return new TrayAdapterMenu(items, _adapterSwitcher.PreferredName);
    }

    /// <summary>托盘菜单切换上网网卡：只保存偏好，DNS 检测与应用在下次刷新时按新网卡解析。</summary>
    private void OnTrayAdapterSelected(string? name) => _adapterSwitcher.Select(name);

    /// <summary>托盘菜单「退出程序」：先摘图标，再关窗口，最后确保进程退出。</summary>
    private void ExitFromTray()
    {
        _exiting = true;
        DisposeTray();

        try
        {
            Close();
        }
        catch
        {
            // 关窗口失败时下面直接结束进程
        }

        Environment.Exit(0);
    }

    private void DisposeTray()
    {
        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 托盘释放失败不影响退出
        }

        try
        {
            if (_subclassProc is not null && _hwnd != 0)
            {
                RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            }
        }
        catch
        {
            // 窗口销毁后移除子类化可能失败，可忽略
        }

        _subclassProc = null;
    }

    /// <summary>不用子类化时也能拿到窗口句柄（供置前与子类化安装用）。</summary>
    private IntPtr GetWindowHandle()
    {
        try
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(this);
        }
        catch
        {
            return 0;
        }
    }

    private void MinimizeToTaskbar()
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

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        uint uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        uint uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        uint uIdSubclass);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
