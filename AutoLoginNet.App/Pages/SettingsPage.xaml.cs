using AutoLoginNet.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoLoginNet.App.Pages;

/// <summary>设置页：外观、认证服务器与数据位置。（开机自启已移至首页）</summary>
public sealed partial class SettingsPage : Page
{
    private bool _initializing = true;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();

        ThemeCombo.SelectedIndex = ViewModel.ThemeIndex;
        _initializing = false;
    }

    public SettingsViewModel ViewModel { get; }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            ViewModel.ThemeIndex = ThemeCombo.SelectedIndex;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.SetBusy(true);
        try
        {
            await ViewModel.SaveAsync();
        }
        finally
        {
            App.MainWindow?.SetBusy(false);
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await Infrastructure.ShellHelper.ConfirmAsync(
            XamlRoot,
            "恢复默认配置",
            "将把认证服务器、工具地址等配置恢复为默认值（不会删除已保存的凭据）。确定继续吗？",
            "恢复默认");

        if (!confirmed)
        {
            return;
        }

        await ViewModel.ResetToDefaultsAsync();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        => ViewModel.OpenDataFolderCommand.Execute(null);

    private void OpenConfigFile_Click(object sender, RoutedEventArgs e)
        => ViewModel.OpenConfigFileCommand.Execute(null);
}
