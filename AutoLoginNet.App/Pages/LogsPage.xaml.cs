using AutoLoginNet.App.Infrastructure;
using AutoLoginNet.App.ViewModels;
using AutoLoginNet.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoLoginNet.App.Pages;

/// <summary>运行日志页。</summary>
public sealed partial class LogsPage : Page
{
    /// <summary>距底部多少像素以内视为“停留在最新”，超出则暂停自动滚动。</summary>
    private const double PinnedThreshold = 48;

    public LogsPage()
    {
        ViewModel = new LogsViewModel(App.Services);
        InitializeComponent();

        LevelCombo.ItemsSource = new List<string> { "全部", "调试及以上", "信息及以上", "警告及以上", "仅错误" };
        LevelCombo.SelectedIndex = 0;

        // 切走再切回时复用同一实例，避免重复订阅 EntryWritten/Flushed 造成重复刷新
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        // 由视图模型在「一批日志已推送到界面」后通知，而不是每条日志都滚动一次。
        // 不在这里退订：页面被缓存后 Unloaded 会先于下次 Loaded 触发，
        // 退订后再回到本页就会失去自动滚动。视图模型与页面的生命周期本就一致。
        ViewModel.Flushed += OnFlushed;
        Loaded += OnLoaded;

        ViewModel.Refresh();
    }

    public LogsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Refresh();
        ScrollToLatestIfPinned();
    }

    private void OnFlushed(object? sender, EventArgs e) => ScrollToLatestIfPinned();

    /// <summary>跟踪用户是否停留在列表底部，避免查看历史时被强行拽回。</summary>
    private void LogScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        double distanceFromBottom = LogScrollViewer.ScrollableHeight - LogScrollViewer.VerticalOffset;
        ViewModel.SetPinnedToBottom(distanceFromBottom <= PinnedThreshold);
    }

    private void ScrollToLatestIfPinned()
    {
        if (!ViewModel.ShouldAutoScroll || ViewModel.Entries.Count == 0)
        {
            return;
        }

        // 直接滚动内部 ScrollViewer：比 ScrollIntoView 更轻，也不会再触发集合变更
        LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void LevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Filter.MinimumLevel = LevelCombo.SelectedIndex switch
        {
            1 => LogLevel.Debug,
            2 => LogLevel.Info,
            3 => LogLevel.Warning,
            4 => LogLevel.Error,
            _ => null,
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Refresh();
        ScrollToLatestIfPinned();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ClearAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppPaths.DataDirectory);
}
