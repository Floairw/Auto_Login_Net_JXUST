using System.Runtime.InteropServices;
using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.Pages;
using AutoLoginNet.App.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
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

    /// <summary>
    /// 把托盘图标设为任务栏常驻的重试间隔。系统是懒创建 NotifyIconSettings 条目的
    /// （用户第一次打开「^」之后才出现），所以只能轮询。
    /// </summary>
    private static readonly TimeSpan TrayPromotionRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>15 秒一轮的重试最多盯 10 分钟，之后改成 5 分钟一轮（用户可能过很久才点开「^」）。</summary>
    private const int TrayPromotionQuickAttempts = 40;

    /// <summary>「图标在哪」的提示每次运行只弹一次。</summary>
    private static bool _trayHintShown;

    /// <summary>提示对话框正在显示（防止连点最小化按钮弹出多个）。</summary>
    private bool _trayHintPending;

    private bool _hiddenToTray;
    private bool _exiting;
    private IntPtr _hwnd;
    private SubclassProc? _subclassProc;
    private bool _activateRetryHooked;
    private DispatcherQueueTimer? _trayPromotionTimer;
    private int _trayPromotionAttempts;
    private bool _trayPromotionSettled;

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

        // Windows 11 默认把新托盘图标折叠进「^」，这里尽力把它改成任务栏常驻显示
        StartTrayPromotionRetry();

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
            Log("托盘图标不可用，已改为普通最小化（进程可能处于低完整性级别，或资源管理器还未就绪）");
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
            return;
        }

        Log("已最小化到系统托盘");
    }

    /// <summary>
    /// 每次运行第一次最小化时，若图标还没确认显示在任务栏上，藏起来就等于「程序消失了」，
    /// 所以这次先弹一个说明框（藏在托盘里的窗口没法弹自己的对话框），用户确认后再收进托盘。
    /// </summary>
    private bool ShouldHintBeforeHiding()
    {
        if (_trayHintShown || _trayHintPending || _tray is null || !_tray.IsAvailable)
        {
            return false;
        }

        // 顺手再试一次：用户可能刚点开过「^」，系统这时才建出设置条目
        if (PromoteTrayIcon() == TrayPromotionState.Promoted)
        {
            // 图标已经显示在任务栏上，不需要再解释
            return false;
        }

        _trayHintShown = true;
        _trayHintPending = true;
        return true;
    }

    /// <summary>弹一次「图标在右下角」的说明，确认后再把窗口收进托盘。</summary>
    private async Task ShowTrayHintThenHideAsync()
    {
        try
        {
            await ShellHelper.ShowMessageAsync(
                RootGrid.XamlRoot,
                "已最小化到系统托盘",
                "程序图标在任务栏右下角的通知区域；如果没看到，请点「^」展开，"
                + "或在「设置 → 个性化 → 任务栏 → 其他系统托盘图标」里把本程序打开。\n\n"
                + "点「确定」后窗口就收进托盘，之后每次最小化都直接进托盘。");

            Log("已在首次最小化时提示托盘图标的位置");
        }
        catch (Exception ex)
        {
            Log($"托盘提示对话框打开失败：{ex.Message}");
        }
        finally
        {
            _trayHintPending = false;
            HideToTray();
        }
    }

    /// <summary>
    /// 让托盘图标尽量显示在任务栏上；返回尝试后的状态。
    /// <para>
    /// 定时器只在「系统还没记录过本图标」（<see cref="TrayPromotionState.NotListed"/>）时保留，
    /// 一旦确认显示或确认用户自己关掉过就停掉，避免无谓地读注册表。
    /// </para>
    /// </summary>
    private TrayPromotionState PromoteTrayIcon()
    {
        if (_tray is null)
        {
            return TrayPromotionState.NotListed;
        }

        TrayPromotionState state = _tray.TryPromoteToTaskbar();

        if (state == TrayPromotionState.Promoted && !_trayPromotionSettled)
        {
            _trayPromotionSettled = true;
            Log("已把托盘图标设为任务栏常驻显示（Windows 11 默认会把它折叠进右下角的「^」）");
        }
        else if (state == TrayPromotionState.Hidden && !_trayPromotionSettled)
        {
            // 值为 0 表示用户自己关掉过，尊重用户的系统设置
            _trayPromotionSettled = true;
            Log("托盘图标当前折叠在任务栏「^」里（用户关闭过常驻显示），不再自动改动");
        }

        return state;
    }

    private void StartTrayPromotionRetry()
    {
        if (_tray is null)
        {
            return;
        }

        if (PromoteTrayIcon() != TrayPromotionState.NotListed)
        {
            return;
        }

        DispatcherQueueTimer? timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (timer is null)
        {
            return;
        }

        timer.Interval = TrayPromotionRetryInterval;
        timer.IsRepeating = true;
        timer.Tick += OnTrayPromotionTick;
        _trayPromotionTimer = timer;
        timer.Start();
    }

    private void OnTrayPromotionTick(DispatcherQueueTimer sender, object args)
    {
        _trayPromotionAttempts++;

        if (PromoteTrayIcon() != TrayPromotionState.NotListed)
        {
            StopTrayPromotionRetry();
            return;
        }

        // 前 10 分钟勤快点；用户可能很久之后才第一次点开「^」，之后改成慢速轮询，不再停
        if (_trayPromotionAttempts == TrayPromotionQuickAttempts)
        {
            sender.Interval = TimeSpan.FromMinutes(5);
        }
    }

    private void StopTrayPromotionRetry()
    {
        if (_trayPromotionTimer is null)
        {
            return;
        }

        try
        {
            _trayPromotionTimer.Stop();
            _trayPromotionTimer.Tick -= OnTrayPromotionTick;
        }
        catch
        {
            // 定时器停不下来不影响退出流程
        }

        _trayPromotionTimer = null;
    }

    private static void Log(string message)
    {
        try
        {
            App.Services?.Log.Info(message, "界面");
        }
        catch
        {
            // 启动早期日志服务可能还没好
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

            // 用户很可能就是「找不到图标」才点托盘还原的，顺便再试一次常驻显示
            PromoteTrayIcon();

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
            // 第一次最小化且图标还没确认显示在任务栏上：先说明图标在哪，确认后再藏
            if (ShouldHintBeforeHiding())
            {
                _ = ShowTrayHintThenHideAsync();
                return 0;
            }

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
        StopTrayPromotionRetry();

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
