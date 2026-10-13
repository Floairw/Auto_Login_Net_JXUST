using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoLoginNet.App.Infrastructure;

/// <summary>界面通用辅助：消息条、对话框、外部程序调用与提权。</summary>
public static class ShellHelper
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前可执行文件路径。</summary>
    public static string ExecutablePath
        => Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    /// <summary>用资源管理器打开目录。</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开目录失败无需打扰用户
        }
    }

    /// <summary>在默认浏览器中打开链接。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 忽略打开失败
        }
    }

    /// <summary>用系统默认程序打开文件。</summary>
    public static void OpenFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch
        {
            // 忽略打开失败
        }
    }

    /// <summary>
    /// 以管理员权限用记事本打开文件（hosts 需要管理员权限才能保存修改）。
    /// </summary>
    /// <returns>true 表示已成功拉起编辑器；false 表示用户取消了 UAC 或启动失败。</returns>
    public static bool OpenFileAsAdministrator(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // 直接对 notepad.exe 提权：不依赖 .txt 之外文件类型的 edit 动词注册情况，
        // 提权进程只拿到文件路径，不会额外启动本应用。
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
                Verb = "runas",
            });

            return true;
        }
        catch
        {
            // 用户取消 UAC 或提权失败
            return false;
        }
    }

    /// <summary>
    /// 以管理员权限重新启动自身并传入参数（触发 UAC）。
    /// 返回非 null 表示提权进程已成功拉起（调用方可据此读取其结果文件）；
    /// 返回 null 表示用户取消了 UAC 授权或提权失败。
    /// </summary>
    public static Process? RunElevated(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = true,
                Verb = "runas",
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return Process.Start(startInfo);
        }
        catch
        {
            // 用户取消 UAC 或提权失败
            return null;
        }
    }

    /// <summary>弹出提示对话框。</summary>
    public static async Task ShowMessageAsync(XamlRoot? xamlRoot, string title, string message)
    {
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
        };

        await dialog.ShowAsync();
    }

    /// <summary>弹出确认对话框。</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? xamlRoot, string title, string message, string confirmText = "确定")
    {
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>在界面线程上执行动作。</summary>
    public static void OnUiThread(Action action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        dispatcher.TryEnqueue(() => action());
    }
}
