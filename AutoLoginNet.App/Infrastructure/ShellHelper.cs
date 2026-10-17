using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Security.Principal;

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

    /// <summary>
    /// 同一时刻只允许一个 <see cref="ContentDialog"/>。
    /// <para>
    /// WinUI 3 的硬限制是「只能有一个 ContentDialog 处于打开状态」，第二个
    /// <c>ShowAsync</c> 会抛 <c>COMException 0x80000019</c>（「某个异步操作没有正常启动」）。
    /// 双击按钮、连按回车，或在旧对话框还没真正显示时又点一次，都会撞上它。
    /// 用一个信号量把「打开对话框」串行化：拿不到就直接当作用户取消，绝不把异常抛给调用方。
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    /// <summary>弹出提示对话框；已有对话框打开时静默跳过（提示不是必需操作，不值得弹第二个）。</summary>
    public static async Task ShowMessageAsync(XamlRoot? xamlRoot, string title, string message)
    {
        if (xamlRoot is null || !DialogGate.Wait(0))
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            LogDialogFailure(ex);
        }
        finally
        {
            DialogGate.Release();
        }
    }

    /// <summary>
    /// 弹出确认对话框。
    /// 已有对话框打开或对话框自身打开失败时返回 false（按「用户没确认」处理），
    /// 让误触/连点变成「什么也没发生」，而不是让程序崩在按钮点击里。
    /// </summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? xamlRoot, string title, string message, string confirmText = "确定")
    {
        if (xamlRoot is null || !DialogGate.Wait(0))
        {
            return false;
        }

        try
        {
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
        catch (Exception ex)
        {
            LogDialogFailure(ex);
            return false;
        }
        finally
        {
            DialogGate.Release();
        }
    }

    /// <summary>对话框打开失败只记日志：不能因为「弹窗弹不出来」而让整个操作抛异常。</summary>
    private static void LogDialogFailure(Exception ex)
    {
        try
        {
            App.Services?.Log.Error($"对话框打开失败：{ex.Message}", "界面");
        }
        catch
        {
            // 日志还没就绪（极早期启动）时忽略
        }
    }

}
