using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoLoginNet.App.Pages;

/// <summary>网络工具页：DNS 优选、连通性诊断与测速。</summary>
public sealed partial class NetworkPage : Page
{
    public NetworkPage()
    {
        ViewModel = new NetworkToolsViewModel(App.Services);
        InitializeComponent();

        PingTargetCombo.ItemsSource = ViewModel.PingTargets;

        // 切走再切回时复用同一实例，避免重复订阅与重复初始化
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        Loaded += OnLoaded;
    }

    public NetworkToolsViewModel ViewModel { get; }

    /// <summary>抑制标记：程序性回写下拉选中项时不要触发「切换网卡」。</summary>
    private bool _suppressAdapterSelection;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        SyncAdapterSelection();
    }

    private async void RefreshAdapter_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        SyncAdapterSelection();
    }

    /// <summary>把下拉选中项对齐到当前偏好（初始化、刷新、托盘切换后都要对齐一次）。</summary>
    private void SyncAdapterSelection()
    {
        _suppressAdapterSelection = true;
        try
        {
            string preferred = ViewModel.PreferredAdapterName;
            AdapterCombo.SelectedItem = ViewModel.AdapterOptions
                .FirstOrDefault(c => string.Equals(c.Name ?? string.Empty, preferred, StringComparison.OrdinalIgnoreCase))
                ?? ViewModel.AdapterOptions.FirstOrDefault();
        }
        finally
        {
            _suppressAdapterSelection = false;
        }
    }

    private async void AdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAdapterSelection || AdapterCombo.SelectedItem is not AdapterChoice choice)
        {
            return;
        }

        await ViewModel.SelectAdapterAsync(choice.Name);
        SyncAdapterSelection();
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.SetBusy(true);
        try
        {
            await ViewModel.MeasureAsync();
        }
        finally
        {
            App.MainWindow?.SetBusy(false);
        }
    }

    private async void ApplyDns_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.SetBusy(true);
        try
        {
            await ViewModel.ApplyRecommendedAsync();
        }
        finally
        {
            App.MainWindow?.SetBusy(false);
        }
    }

    private async void Automatic_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.SetBusy(true);
        try
        {
            await ViewModel.SetAutomaticAsync();
        }
        finally
        {
            App.MainWindow?.SetBusy(false);
        }
    }

    // ---------------- 系统首选网卡（IPv4 接口跃点） ----------------

    /// <summary>
    /// 把当前网卡设为系统首选。
    /// <para>
    /// 确认框必须讲清三件事：会改变系统选路、需要 UAC、可一键恢复——
    /// 用户知道要发生什么、失败了怎么还原，才不会把网络异常误当成程序故障。
    /// </para>
    /// </summary>
    private async void SetSystemPreferred_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await ShellHelper.ConfirmAsync(
            XamlRoot,
            "设为系统首选网卡",
            "将修改系统 IPv4 接口跃点：选定的上网网卡改为 10，其余已连接网卡改为 9000，"
            + "让系统优先从这张网卡上网。\n\n"
            + "这会改变系统选路（可能影响其它网卡上的网络），但不会禁用任何网卡。\n"
            + "需要管理员权限，点击「确定」后会弹出 UAC 授权窗口；之后可随时点「恢复系统自动跃点」还原。",
            "确定并提权");

        if (!confirmed)
        {
            return;
        }

        App.MainWindow?.SetBusy(true);
        try
        {
            await ViewModel.SetSystemPreferredAsync();
        }
        finally
        {
            App.MainWindow?.SetBusy(false);
        }
    }

    /// <summary>按上次应用前保存的快照恢复原跃点。</summary>
    private async void RestoreAdapterPriority_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await ShellHelper.ConfirmAsync(
            XamlRoot,
            "恢复系统自动跃点",
            "将按上次应用前保存的快照，把各网卡的 IPv4 接口跃点恢复成原值，系统重新按默认跃点自动选路。\n\n"
            + "需要管理员权限，点击「确定」后会弹出 UAC 授权窗口。是否继续？",
            "确定并提权");

        if (!confirmed)
        {
            return;
        }

        App.MainWindow?.SetBusy(true);
        try
        {
            await ViewModel.RestoreAdapterPriorityAsync();
        }
        finally
        {
            App.MainWindow?.SetBusy(false);
        }
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PingAsync();
    }

    private async void CampusSpeedTest_Click(object sender, RoutedEventArgs e)
    {
        string url = App.Services.Options.Tools.CampusSpeedTestUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            await ShellHelper.ShowMessageAsync(
                XamlRoot,
                "尚未配置内网测速地址",
                "校园内网测速页地址因校而异，请在「设置 → 工具」中填写后再试。");
            return;
        }

        ShellHelper.OpenUrl(url);
    }

    private void InternetSpeedTest_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl(App.Services.Options.Tools.InternetSpeedTestUrl);

    // ---------------- 校内域名解析（hosts） ----------------

    /// <summary>
    /// 抑制标记。<c>IsOn</c> 是 OneWay 绑定，视图模型回读状态时会再次触发 Toggled，
    /// 没有这个标志就会形成「回写 → 事件 → 再操作」的回环。
    /// </summary>
    private bool _suppressHostsToggle;

    private async void HostsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHostsToggle || sender is not ToggleSwitch toggle)
        {
            return;
        }

        App.MainWindow?.SetBusy(true);
        _suppressHostsToggle = true;
        try
        {
            await ViewModel.SetHostsEnabledAsync(toggle.IsOn);
        }
        finally
        {
            // 用真实状态回写开关，避免界面与实际情况不一致
            toggle.IsOn = ViewModel.HostsIsOn;
            _suppressHostsToggle = false;
            App.MainWindow?.SetBusy(false);
        }
    }

    private async void RefreshHosts_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshHostsAsync();

        _suppressHostsToggle = true;
        HostsToggle.IsOn = ViewModel.HostsIsOn;
        _suppressHostsToggle = false;
    }

    /// <summary>
    /// 以管理员权限用记事本打开 hosts 文件。
    /// hosts 位于系统目录，普通权限下记事本无法保存修改，因此这里直接提权打开。
    /// </summary>
    private async void EditHosts_Click(object sender, RoutedEventArgs e)
    {
        if (ShellHelper.OpenFileAsAdministrator(App.Services.HostsFile.FilePath))
        {
            return;
        }

        await ShellHelper.ShowMessageAsync(
            XamlRoot,
            "未能打开 hosts 文件",
            "未获得管理员授权，hosts 文件未被打开。\n\n"
            + "如需手动修改，请在开始菜单搜索「记事本」，右键选择「以管理员身份运行」，"
            + "再通过「文件 → 打开」定位到：\n"
            + App.Services.HostsFile.FilePath);
    }
}
