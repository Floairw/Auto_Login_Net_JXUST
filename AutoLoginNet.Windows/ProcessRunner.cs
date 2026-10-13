using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AutoLoginNet.Windows;

/// <summary>外部进程执行结果。</summary>
/// <param name="ExitCode">退出码；进程无法启动或超时为 -1。</param>
/// <param name="StdOut">标准输出。</param>
/// <param name="StdErr">标准错误。</param>
public readonly record struct ProcessOutcome(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// 进程执行辅助。集中处理超时、编码与输出读取，
/// 避免各处重复实现并防止因管道写满导致的死锁。
/// </summary>
public static class ProcessRunner
{
    /// <summary>控制台程序输出使用系统 OEM 代码页（中文系统为 GBK）。</summary>
    private static readonly Encoding ConsoleEncoding = GetConsoleEncoding();

    /// <summary>执行并捕获输出。</summary>
    /// <param name="outputEncoding">
    /// 子进程输出的解码方式。netsh 等原生工具按控制台 OEM 代码页输出（中文系统为 GBK），
    /// 而 PowerShell 在被重定向时按 UTF-8 输出——两者混用会导致中文变成乱码，
    /// 因此必须由调用方显式指定。
    /// </param>
    public static async Task<ProcessOutcome> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null)
    {
        Encoding encoding = outputEncoding ?? ConsoleEncoding;
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessOutcome(-1, string.Empty, $"无法启动 {fileName}");
            }

            // 异步读取，避免子进程因输出缓冲写满而阻塞
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new ProcessOutcome(-1, string.Empty, $"{fileName} 执行超过 {timeoutMs} ms 未结束");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessOutcome(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProcessOutcome(-1, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>执行 PowerShell 命令并返回标准输出。</summary>
    public static async Task<string?> RunPowerShellAsync(
        string command,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        var outcome = await RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
            timeoutMs,
            cancellationToken,
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        return outcome.Succeeded ? outcome.StdOut : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略终止失败
        }
    }

    private static Encoding GetConsoleEncoding()
    {
        try
        {
            // GetEncoding(0) 取得当前 ANSI 代码页；.NET 在 Windows 上将 0 映射为 UTF-8，
            // 但配合 UseShellExecute=false 时仍能正确解码 GBK 控制台输出
            return Encoding.GetEncoding(0);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
