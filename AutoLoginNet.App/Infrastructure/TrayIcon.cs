using System.Runtime.InteropServices;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>托盘右键菜单「上网网卡」中的一项。</summary>
/// <param name="Name">网卡名称；null 表示「自动选择」。</param>
/// <param name="Label">菜单显示文字。</param>
public sealed record TrayAdapterItem(string? Name, string Label);

/// <summary>「上网网卡」子菜单的内容。</summary>
/// <param name="Items">候选网卡。</param>
/// <param name="SelectedName">当前选中的网卡名称；空表示自动选择。</param>
public sealed record TrayAdapterMenu(IReadOnlyList<TrayAdapterItem> Items, string? SelectedName);

/// <summary>
/// 系统托盘图标（Win32 <c>Shell_NotifyIcon</c> 实现）。
/// <para>
/// WinUI 3 没有托盘 API，这里用一个隐藏的原生窗口接收托盘回调：
/// 左键单击或双击还原主窗口，右键弹出「显示主窗口 / 退出程序」菜单。
/// 图标取自本程序 exe 内嵌的 <c>Assets\app.ico</c>，取不到时退回系统默认图标。
/// </para>
/// <para>
/// 资源管理器重启后托盘图标会消失，这里监听 <c>TaskbarCreated</c> 广播自动补回；
/// 也正因为要收广播，窗口刻意做成「隐藏的顶层窗口」而不是 message-only 窗口
/// —— message-only 窗口收不到广播消息。
/// </para>
/// <para>
/// 整个类刻意「失败即静默」：托盘起不来只是少一个入口，不能影响主程序，
/// 因此 <see cref="IsAvailable"/> 为 false 时调用方应退回普通最小化。
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_BYCOMMAND = 0x00000000;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint MenuShowWindow = 1;
    private const uint MenuExit = 2;

    /// <summary>「上网网卡」子菜单里「自动选择」的命令 id。</summary>
    private const uint MenuAdapterAutomatic = 10;

    /// <summary>「上网网卡」子菜单里第一个网卡的命令 id，后续按顺序递增。</summary>
    private const uint MenuAdapterFirst = 100;

    /// <summary><c>IDI_APPLICATION</c>：系统默认应用图标。</summary>
    private const int IDI_APPLICATION = 32512;

    /// <summary>NOTIFYICONDATA.szTip 的可用长度（含结尾 0 共 128 个字符）。</summary>
    private const int TipLength = 127;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly string _defaultTip;
    private readonly Action _showRequested;
    private readonly Action _exitRequested;
    private readonly Func<TrayAdapterMenu>? _adapterMenu;
    private readonly Action<string?>? _adapterSelected;

    private WndProcDelegate? _wndProc;
    private string? _className;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconOwned;
    private bool _added;
    private bool _disposed;
    private uint _taskbarCreatedMessage;

    /// <param name="toolTip">鼠标悬停时的提示文字（过长会自动截断）。</param>
    /// <param name="showRequested">用户要求显示主窗口（左键 / 双击 / 菜单第一项）。</param>
    /// <param name="exitRequested">用户在托盘菜单里选择「退出程序」。</param>
    /// <param name="adapterMenu">右键菜单「上网网卡」子菜单的内容；为 null 时不显示该子菜单。</param>
    /// <param name="adapterSelected">用户选择上网网卡；参数为 null 表示「自动选择」。</param>
    public TrayIcon(
        string toolTip,
        Action showRequested,
        Action exitRequested,
        Func<TrayAdapterMenu>? adapterMenu = null,
        Action<string?>? adapterSelected = null)
    {
        _defaultTip = toolTip;
        _showRequested = showRequested;
        _exitRequested = exitRequested;
        _adapterMenu = adapterMenu;
        _adapterSelected = adapterSelected;

        try
        {
            CreateMessageWindow();
            _hIcon = LoadAppIcon();
            _added = AddIcon();
        }
        catch
        {
            // 托盘不可用时保持静默：调用方会退回普通最小化
            _added = false;
        }
    }

    /// <summary>托盘图标是否已经出现在通知区域。</summary>
    public bool IsAvailable => _added;

    /// <summary>
    /// 尽力把托盘图标设为「在任务栏上显示」，并返回尝试后的状态。
    /// <para>
    /// Windows 11 默认把新图标折叠进「^」溢出面板，用户会以为没有托盘。系统没提供申请常驻的 API，
    /// 只能改 <c>HKCU\Control Panel\NotifyIconSettings</c> 里的 <c>IsPromoted</c>（见
    /// <see cref="TrayIconPromotion"/>）。该条目是懒创建的，返回 <see cref="TrayPromotionState.NotListed"/>
    /// 时调用方应稍后再试。
    /// </para>
    /// </summary>
    public TrayPromotionState TryPromoteToTaskbar()
    {
        if (_disposed || !_added)
        {
            return TrayPromotionState.NotListed;
        }

        TrayPromotionResult result;
        try
        {
            result = TrayIconPromotion.EnsurePromoted();
        }
        catch
        {
            return TrayPromotionState.NotListed;
        }

        if (result.Changed)
        {
            // 资源管理器是在添加图标时读这个值的，改完必须重新添加一次才会生效
            ReAddIcon();
        }

        return result.State;
    }

    /// <summary>删掉再重新添加图标（资源管理器会重新读取托盘设置）。</summary>
    private void ReAddIcon()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var data = CreateNotificationData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = AddIcon();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_added && _hwnd != 0)
            {
                var data = CreateNotificationData(0);
                Shell_NotifyIconW(NIM_DELETE, ref data);
            }
        }
        catch
        {
            // 图标移除失败时进程退出后系统也会清理
        }

        _added = false;

        try
        {
            if (_hwnd != 0)
            {
                DestroyWindow(_hwnd);
            }
        }
        catch
        {
            // 窗口销毁失败不影响退出流程
        }

        _hwnd = 0;

        try
        {
            if (_iconOwned && _hIcon != 0)
            {
                DestroyIcon(_hIcon);
            }
        }
        catch
        {
            // 图标句柄释放失败可忽略
        }

        _hIcon = 0;
        _iconOwned = false;

        try
        {
            if (_className is not null)
            {
                UnregisterClassW(_className, GetModuleHandleW(null));
            }
        }
        catch
        {
            // 窗口类注销失败可忽略
        }

        _className = null;
        _wndProc = null;
    }

    private void CreateMessageWindow()
    {
        _className = "AutoLoginNetTrayWindow";
        _wndProc = WindowProc;

        IntPtr hInstance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };

        RegisterClassExW(ref windowClass);

        // 隐藏的顶层窗口（无 WS_VISIBLE）：用于接收托盘回调与 TaskbarCreated 广播
        _hwnd = CreateWindowExW(
            0,
            _className,
            "AutoLoginNetTray",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            hInstance,
            0);

        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    }

    private IntPtr LoadAppIcon()
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "AutoLoginNet.exe");

            if (ExtractIconExW(exePath, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (small != 0)
                {
                    if (large != 0)
                    {
                        DestroyIcon(large);
                    }

                    _iconOwned = true;
                    return small;
                }

                if (large != 0)
                {
                    _iconOwned = true;
                    return large;
                }
            }
        }
        catch
        {
            // 取 exe 内嵌图标失败时退回系统默认图标
        }

        return LoadIconW(0, IDI_APPLICATION);
    }

    private bool AddIcon()
    {
        if (_disposed || _hwnd == 0)
        {
            return false;
        }

        var data = CreateNotificationData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (Shell_NotifyIconW(NIM_ADD, ref data))
        {
            return true;
        }

        // 图标已在时（例如资源管理器重启后重复广播）用修改语义再补一次
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW CreateNotificationData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _hIcon,
        szTip = TruncateTip(_defaultTip),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string TruncateTip(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "校园网认证客户端";
        }

        return text.Length <= TipLength ? text : text[..TipLength];
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAYICON)
            {
                OnTrayMessage(lParam);
                return 0;
            }

            // 资源管理器重启后会广播 TaskbarCreated，此时需要重新添加图标
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                _added = AddIcon();
                return 0;
            }
        }
        catch
        {
            // 回调里的异常不能冒到消息循环，否则整个界面线程会崩
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnTrayMessage(IntPtr lParam)
    {
        int notification = (int)(lParam.ToInt64() & 0xFFFF);

        switch (notification)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                SafeInvoke(_showRequested);
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            TrayAdapterMenu? adapterMenu = GetAdapterMenu();

            AppendMenuW(menu, MF_STRING, MenuShowWindow, "显示主窗口");

            IntPtr adapterSubmenu = CreateAdapterSubmenu(adapterMenu);
            if (adapterSubmenu != 0)
            {
                AppendMenuW(menu, MF_POPUP | MF_STRING, ToMenuHandle(adapterSubmenu), "上网网卡");
            }

            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, MenuExit, "退出程序");
            SetMenuDefaultItem(menu, MenuShowWindow, 0);

            GetCursorPos(out POINT point);

            // 经典要求：先把承载托盘图标的窗口设为前台，菜单才会在点击别处时关闭；
            // 菜单返回后补一条空消息，避免菜单残留在最上层。
            SetForegroundWindow(_hwnd);
            uint command = TrackPopupMenuEx(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                _hwnd,
                0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);

            if (command >= MenuAdapterFirst)
            {
                int index = (int)(command - MenuAdapterFirst);
                if (adapterMenu is not null && index < adapterMenu.Items.Count)
                {
                    SafeInvokeAdapterSelected(adapterMenu.Items[index].Name);
                }

                return;
            }

            switch (command)
            {
                case MenuShowWindow:
                    SafeInvoke(_showRequested);
                    break;

                case MenuAdapterAutomatic:
                    SafeInvokeAdapterSelected(null);
                    break;

                case MenuExit:
                    SafeInvoke(_exitRequested);
                    break;
            }
        }
        catch
        {
            // 菜单弹不出来时不影响托盘图标继续使用
        }
        finally
        {
            // DestroyMenu 会连同子菜单一起销毁
            DestroyMenu(menu);
        }
    }

    /// <summary>构建「上网网卡」子菜单；请求失败或没有候选网卡时仍返回可用菜单（只有「自动选择」）。</summary>
    private static IntPtr CreateAdapterSubmenu(TrayAdapterMenu? model)
    {
        IntPtr submenu = CreatePopupMenu();
        if (submenu == 0)
        {
            return 0;
        }

        AppendMenuW(submenu, MF_STRING, MenuAdapterAutomatic, "自动选择");

        if (model is null)
        {
            return submenu;
        }

        uint checkedId = MenuAdapterAutomatic;
        uint lastAdapterId = MenuAdapterAutomatic;

        IReadOnlyList<TrayAdapterItem> items = model.Items;
        for (int i = 0; i < items.Count; i++)
        {
            uint id = MenuAdapterFirst + (uint)i;
            AppendMenuW(submenu, MF_STRING, id, items[i].Label);
            lastAdapterId = id;

            if (items[i].Name is { Length: > 0 } name
                && string.Equals(name, model.SelectedName, StringComparison.OrdinalIgnoreCase))
            {
                checkedId = id;
            }
        }

        if (items.Count == 0)
        {
            AppendMenuW(submenu, MF_STRING | MF_GRAYED, MenuAdapterFirst, "（未检测到可用网卡）");
            lastAdapterId = MenuAdapterAutomatic;
        }

        CheckMenuRadioItem(submenu, MenuAdapterAutomatic, lastAdapterId, checkedId, MF_BYCOMMAND);
        return submenu;
    }

    private TrayAdapterMenu? GetAdapterMenu()
    {
        if (_adapterMenu is null)
        {
            return null;
        }

        try
        {
            return _adapterMenu();
        }
        catch
        {
            // 枚举网卡失败时退化为「只有自动选择」的子菜单
            return null;
        }
    }

    private static UIntPtr ToMenuHandle(IntPtr handle) => (UIntPtr)(ulong)handle.ToInt64();

    private void SafeInvokeAdapterSelected(string? name)
    {
        if (_adapterSelected is null)
        {
            return;
        }

        try
        {
            _adapterSelected(name);
        }
        catch
        {
            // 切换网卡失败不能冒到消息循环
        }
    }

    private static void SafeInvoke(Action? callback)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch
        {
            // 回调失败不能冒到消息循环
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern bool CheckMenuRadioItem(IntPtr hMenu, uint first, uint last, uint check, uint flags);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
