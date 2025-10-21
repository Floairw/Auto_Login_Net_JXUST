using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

class Program
{
    // 配置文件路径
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetworkAuthenticator",
        "config.json");

    // 日志文件路径
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetworkAuthenticator",
        "autologin.log");

    static async Task Main(string[] args)
    {
        // 检查是否以开机自启方式启动
        bool isAutoStart = args.Length > 0 && args.Contains("--autostart");

        // 自动启动时记录日志
        if (isAutoStart)
        {
            LogMessage("程序以自动启动方式启动");
        }

        Console.WriteLine("网络认证客户端");
        Console.WriteLine("==============");

        // 检查是否有保存的凭据
        var savedCredentials = LoadCredentials();

        // 如果是开机自启且有保存的凭据，则自动登录
        if (isAutoStart && savedCredentials != null)
        {
            LogMessage($"检测到保存的凭据，用户: {savedCredentials.Username}");
            await AutoLoginProcess(savedCredentials);
            return;
        }
        else if (isAutoStart && savedCredentials == null)
        {
            LogMessage("自动启动但未找到保存的凭据，程序退出");
            Console.WriteLine("未找到保存的凭据，无法自动登录");
            Console.WriteLine("程序将在5秒后退出...");
            await Task.Delay(5000);
            return;
        }

        // 正常启动流程
        await NormalStartProcess(savedCredentials);
    }

    // 自动登录流程
    static async Task AutoLoginProcess(SavedCredentials savedCredentials)
    {
        try
        {
            Console.WriteLine("检测到开机自启，尝试自动登录...");

            // 自动启动时等待网络就绪
            LogMessage("等待网络就绪...");
            await WaitForNetworkReady();

            // 检测网络环境是否需要认证
            bool requiresAuth = await CheckNetworkAuthenticationRequired();
            LogMessage($"网络认证检测结果: {requiresAuth}");

            if (requiresAuth)
            {
                LogMessage("开始自动登录...");
                // 自动登录
                string autoLoginResponse = await Login(savedCredentials.Username, savedCredentials.Password, savedCredentials.Choice);
                LogMessage($"自动登录响应: {autoLoginResponse}");
                Console.WriteLine($"服务器响应: {autoLoginResponse}");
            }
            else
            {
                LogMessage("当前网络环境无需认证");
                Console.WriteLine("当前网络环境无需认证");
            }

            // 等待几秒让用户看到结果，然后退出
            LogMessage("自动登录流程完成");
            Console.WriteLine("自动登录完成，程序将在3秒后退出...");
            await Task.Delay(3000);
        }
        catch (Exception ex)
        {
            LogMessage($"自动登录过程出错: {ex.Message}");
            Console.WriteLine($"自动登录失败: {ex.Message}");
            Console.WriteLine("程序将在5秒后退出...");
            await Task.Delay(5000);
        }
    }

    // 正常启动流程
    static async Task NormalStartProcess(SavedCredentials savedCredentials)
    {
        string username = null;
        string password = null;
        string choice = null;
        bool autoLogin = false;

        // 首先检测网络环境是否需要认证
        bool requiresAuthNormal = await CheckNetworkAuthenticationRequired();
        if (!requiresAuthNormal)
        {
            Console.WriteLine("当前网络环境无需认证");

            // 如果检测到保存的凭据，提供凭据管理选项
            if (savedCredentials != null)
            {
                Console.WriteLine($"检测到保存的凭据: {savedCredentials.Username}");
                Console.WriteLine($"保存时间: {savedCredentials.SaveDate}");

                // 显示凭据管理菜单
                ShowCredentialsManagementMenu(ref savedCredentials);
            }
            else
            {
                Console.WriteLine("按任意键退出...");
                Console.ReadKey();
            }
            return;
        }

        // 如果检测到保存的凭据，提供凭据管理选项
        if (savedCredentials != null)
        {
            Console.WriteLine($"检测到保存的凭据: {savedCredentials.Username}");
            Console.WriteLine($"保存时间: {savedCredentials.SaveDate}");

            // 显示凭据管理菜单
            ShowCredentialsManagementMenu(ref savedCredentials);

            // 如果凭据未被清空，询问是否使用
            if (savedCredentials != null)
            {
                Console.Write("是否使用保存的凭据自动登录? (y/n, 默认n): ");
                string response = Console.ReadLine();

                if (!string.IsNullOrEmpty(response) && response.ToLower() == "y")
                {
                    username = savedCredentials.Username;
                    password = savedCredentials.Password;
                    choice = savedCredentials.Choice;
                    autoLogin = true;
                    Console.WriteLine("使用保存的凭据自动登录...");
                }
            }
        }

        // 如果没有自动登录，获取用户输入
        if (!autoLogin)
        {
            Console.Write("请输入用户名: ");
            username = Console.ReadLine();

            Console.Write("请输入密码: ");
            password = ReadPassword();

            Console.WriteLine("请选择登录方式:");
            Console.WriteLine("1. 电信");
            Console.WriteLine("2. 移动");
            Console.WriteLine("3. 联通");
            Console.WriteLine("4. 校园网");
            Console.Write("请输入数字选择: ");

            string choiceInput = Console.ReadLine();

            switch (choiceInput)
            {
                case "1":
                    choice = "@telecom";
                    break;
                case "2":
                    choice = "@cmcc";
                    break;
                case "3":
                    choice = "@unicom";
                    break;
                case "4":
                    choice = ""; // 校园网不需要后缀
                    break;
                default:
                    Console.WriteLine("错误: 请选择正确的登录方式");
                    return;
            }

            Console.WriteLine(); // 换行

            if (string.IsNullOrEmpty(username))
            {
                Console.WriteLine("错误: 用户名不能为空");
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                Console.WriteLine("错误: 密码不能为空");
                return;
            }

            // 询问是否保存凭据
            Console.Write("是否保存凭据以便下次自动登录? (y/n): ");
            string saveResponse = Console.ReadLine();

            if (saveResponse?.ToLower() == "y")
            {
                SaveCredentials(username, password, choice);
                Console.WriteLine("凭据已保存");

                // 询问是否设置开机自启
                Console.Write("是否设置开机自启? (y/n): ");
                string autoStartResponse = Console.ReadLine();

                if (autoStartResponse?.ToLower() == "y")
                {
                    SetAutoStart(true);
                    Console.WriteLine("开机自启已设置");
                }
            }
        }

        // 执行登录并显示响应
        string responseContent = await Login(username, password, choice);
        Console.WriteLine($"服务器响应: {responseContent}");

        // 显示综合管理菜单（凭据和开机自启）
        ShowComprehensiveManagementMenu();

        Console.WriteLine("\n按任意键退出...");
        Console.ReadKey();
    }

    // 等待网络就绪（针对自动启动）
    static async Task WaitForNetworkReady()
    {
        LogMessage("检查网络连接状态...");

        int maxRetries = 10; // 最多尝试10次
        int retryCount = 0;

        while (retryCount < maxRetries)
        {
            // 检查网络接口状态
            bool networkAvailable = NetworkInterface.GetIsNetworkAvailable();

            if (networkAvailable)
            {
                // 检查是否有可用的网络连接
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                bool hasValidInterface = interfaces.Any(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                if (hasValidInterface)
                {
                    LogMessage("网络连接就绪");
                    return;
                }
            }

            retryCount++;
            LogMessage($"网络未就绪，等待中... ({retryCount}/{maxRetries})");
            await Task.Delay(3000); // 等待3秒
        }

        LogMessage("网络连接等待超时，继续执行...");
    }

    // 记录日志
    static void LogMessage(string message)
    {
        try
        {
            string logDir = Path.GetDirectoryName(LogFilePath);
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, logEntry);
        }
        catch
        {
            // 忽略日志写入错误
        }
    }

    // 显示凭据管理菜单
    static void ShowCredentialsManagementMenu(ref SavedCredentials savedCredentials)
    {
        while (true)
        {
            Console.WriteLine("\n凭据管理");
            Console.WriteLine("========");
            Console.WriteLine("1. 清空保存的凭据");
            Console.WriteLine("2. 返回主菜单");
            Console.Write("请选择: ");

            string choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    ClearCredentials();
                    Console.WriteLine("凭据已清空");
                    savedCredentials = null; // 设置引用为null，表示凭据已被清空
                    return;
                case "2":
                    return;
                default:
                    Console.WriteLine("无效选择");
                    break;
            }
        }
    }

    // 显示综合管理菜单（凭据和开机自启）
    static void ShowComprehensiveManagementMenu()
    {
        while (true)
        {
            Console.WriteLine("\n综合管理菜单");
            Console.WriteLine("============");

            // 检查是否有保存的凭据
            var savedCredentials = LoadCredentials();
            if (savedCredentials != null)
            {
                Console.WriteLine($"当前保存的凭据: {savedCredentials.Username}");
                Console.WriteLine($"保存时间: {savedCredentials.SaveDate}");
            }
            else
            {
                Console.WriteLine("当前没有保存的凭据");
            }

            // 检查开机自启状态
            bool isAutoStartEnabled = IsAutoStartEnabled();
            Console.WriteLine($"开机自启状态: {(isAutoStartEnabled ? "已启用" : "未启用")}");

            Console.WriteLine("1. 清空保存的凭据");
            Console.WriteLine("2. 设置开机自启");
            Console.WriteLine("3. 取消开机自启");
            Console.WriteLine("4. 返回主菜单");
            Console.Write("请选择: ");

            string choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    if (savedCredentials != null)
                    {
                        ClearCredentials();
                        Console.WriteLine("凭据已清空");
                    }
                    else
                    {
                        Console.WriteLine("没有保存的凭据可清空");
                    }
                    break;
                case "2":
                    SetAutoStart(true);
                    Console.WriteLine("开机自启已设置");
                    break;
                case "3":
                    SetAutoStart(false);
                    Console.WriteLine("开机自启已取消");
                    break;
                case "4":
                    return;
                default:
                    Console.WriteLine("无效选择");
                    break;
            }

            Console.WriteLine("按任意键继续...");
            Console.ReadKey();
        }
    }

    // 设置或取消开机自启
    static void SetAutoStart(bool enable)
    {
        try
        {
            string appName = "NetworkAuthenticator";
            string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKey, true))
            {
                if (key == null)
                {
                    Console.WriteLine("无法访问注册表");
                    return;
                }

                if (enable)
                {
                    // 获取当前程序的路径
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;

                    // 添加开机启动项
                    key.SetValue(appName, $"\"{exePath}\" --autostart");
                    Console.WriteLine("开机自启设置成功");
                    LogMessage($"开机自启已设置，程序路径: {exePath}");
                }
                else
                {
                    // 删除开机启动项
                    if (key.GetValue(appName) != null)
                    {
                        key.DeleteValue(appName);
                        Console.WriteLine("开机自启已取消");
                        LogMessage("开机自启已取消");
                    }
                    else
                    {
                        Console.WriteLine("未找到开机自启项");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"设置开机自启失败: {ex.Message}");
            LogMessage($"设置开机自启失败: {ex.Message}");
        }
    }

    // 检查是否已设置开机自启
    static bool IsAutoStartEnabled()
    {
        try
        {
            string appName = "NetworkAuthenticator";
            string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKey, false))
            {
                if (key == null) return false;

                return key.GetValue(appName) != null;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 检测网络环境是否需要认证
    static async Task<bool> CheckNetworkAuthenticationRequired()
    {
        try
        {
            Console.WriteLine("正在检测网络环境...");

            // 检测认证服务器是否响应
            bool campusNetworkResponds = await CheckCampusNetwork();

            // 检测外部网络是否可达（使用Ping，5秒超时）
            bool externalNetworkReachable = await PingHostWithTimeout("baidu.com", 5000);

            Console.WriteLine($"校园网认证服务器响应: {campusNetworkResponds}");
            Console.WriteLine($"外部网络可达: {externalNetworkReachable}");

            // 如果校园网认证服务器响应但外部网络不可达，则需要登录
            if (campusNetworkResponds && !externalNetworkReachable)
            {
                Console.WriteLine("检测到网络需要认证：校园网认证服务器响应但外部网络不可达");
                return true;
            }
            // 如果校园网认证服务器响应，外部网络可达，说明账号已经登录，无需要认证
            else if (campusNetworkResponds)
            {
                Console.WriteLine("检测到校园网认证服务器响应，外部网络可达，账号已经登录");
                return false;
            }
            else
            {
                Console.WriteLine("网络环境正常，无需认证");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"网络检测失败: {ex.Message}");
            // 默认返回不需要认证，避免因检测失败而进行不必要的登录
            return false;
        }
    }

    // 检测校园网认证服务器是否响应
    static async Task<bool> CheckCampusNetwork()
    {
        try
        {
            // 直接访问认证页面，不依赖HTTP状态码
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);

                // 添加用户代理头，避免被服务器拒绝
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                // 直接访问认证页面
                string url = "http://10.17.8.18/";

                try
                {
                    var response = await client.GetAsync(url);
                    // 只要服务器响应，无论状态码是什么，都认为认证服务器可用
                    Console.WriteLine($"认证服务器 {url} 响应状态: {response.StatusCode}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"认证服务器 {url} 无响应: {ex.Message}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"校园网检测异常: {ex.Message}");
            return false;
        }
    }

    // Ping指定主机的方法，基于超时判断可达性
    static async Task<bool> PingHostWithTimeout(string host, int timeoutMs)
    {
        try
        {
            using (var ping = new Ping())
            {
                // 发送Ping请求并等待响应
                var reply = await ping.SendPingAsync(host, timeoutMs);

                // 如果响应状态是Success，表示主机可达
                // 如果超时（TimedOut），表示主机不可达
                return reply.Status == IPStatus.Success;
            }
        }
        catch (PingException)
        {
            // Ping异常，通常表示主机不可达
            return false;
        }
        catch (Exception)
        {
            // 其他异常，也视为不可达
            return false;
        }
    }

    // 执行登录操作并返回响应内容
    static async Task<string> Login(string username, string password, string choice)
    {
        try
        {
            Console.WriteLine("正在尝试登录...");

            // 处理choice为空的情况
            string userAccount = string.IsNullOrEmpty(choice) ? username : username + choice;

            // 构建URL（注意URL编码）
            string encodedUsername = Uri.EscapeDataString(userAccount);
            string encodedPassword = Uri.EscapeDataString(password);

            // 使用固定的登录URL
            string loginUrl = $"http://10.17.8.18:801/eportal/portal/login?" +
                             $"callback=dr1003&login_method=1&" +
                             $"user_account={encodedUsername}&" +
                             $"user_password={encodedPassword}&" +
                             $"jsVersion=4.1.3&terminal_type=1";

            using (HttpClient client = new HttpClient())
            {
                // 设置超时时间
                client.Timeout = TimeSpan.FromSeconds(30);

                // 添加用户代理头，避免被服务器拒绝
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                Console.WriteLine($"尝试登录URL: {loginUrl}");
                HttpResponseMessage response = await client.GetAsync(loginUrl);

                // 读取响应内容
                string responseBody = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"登录完成，响应状态: {response.StatusCode}");
                return responseBody;
            }
        }
        catch (Exception ex)
        {
            return $"登录失败: {ex.Message}";
        }
    }

    // 保存凭据到文件
    static void SaveCredentials(string username, string password, string choice)
    {
        try
        {
            // 确保目录存在
            string configDir = Path.GetDirectoryName(ConfigFilePath);
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            // 使用DPAPI加密密码
            string encryptedPassword = EncryptWithDPAPI(password);

            // 创建凭据对象
            var credentials = new SavedCredentials
            {
                Username = username,
                Password = encryptedPassword,
                Choice = choice,
                SaveDate = DateTime.Now
            };

            // 序列化并保存到文件
            string json = JsonSerializer.Serialize(credentials);
            File.WriteAllText(ConfigFilePath, json);

            LogMessage($"凭据已保存 - 用户: {username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存凭据时出错: {ex.Message}");
            LogMessage($"保存凭据失败: {ex.Message}");
        }
    }

    // 从文件加载凭据
    static SavedCredentials LoadCredentials()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return null;
            }

            // 读取并反序列化
            string json = File.ReadAllText(ConfigFilePath);
            var credentials = JsonSerializer.Deserialize<SavedCredentials>(json);

            if (credentials == null)
            {
                Console.WriteLine("凭据文件格式错误");
                return null;
            }

            // 使用DPAPI解密密码
            try
            {
                credentials.Password = DecryptWithDPAPI(credentials.Password);
            }
            catch (CryptographicException ex)
            {
                Console.WriteLine($"解密密码失败: {ex.Message}");
                LogMessage($"解密密码失败: {ex.Message}");
                return null; // 解密失败，视为无效凭据
            }

            return credentials;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载凭据时出错: {ex.Message}");
            LogMessage($"加载凭据失败: {ex.Message}");
            return null;
        }
    }

    // 清空凭据
    static void ClearCredentials()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                File.Delete(ConfigFilePath);
                Console.WriteLine("凭据已成功清空");
                LogMessage("凭据已清空");
            }
            else
            {
                Console.WriteLine("没有找到保存的凭据");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清空凭据时出错: {ex.Message}");
            LogMessage($"清空凭据失败: {ex.Message}");
        }
    }

    // 安全读取密码（不显示在屏幕上）
    static string ReadPassword()
    {
        string password = "";
        ConsoleKeyInfo key;

        do
        {
            key = Console.ReadKey(true);

            // 不显示输入的字符
            if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
            {
                password += key.KeyChar;
                Console.Write("*");
            }
            else if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password = password.Substring(0, password.Length - 1);
                Console.Write("\b \b");
            }
        }
        while (key.Key != ConsoleKey.Enter);

        Console.WriteLine(); // 换行
        return password;
    }

    // 使用Windows数据保护API (DPAPI) 加密字符串
    static string EncryptWithDPAPI(string plainText)
    {
        try
        {
            // 将字符串转换为字节数组
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

            // 使用DPAPI加密数据，使用当前用户范围
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            // 将加密后的字节数组转换为Base64字符串
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DPAPI加密失败: {ex.Message}");
            throw new CryptographicException("加密失败", ex);
        }
    }

    // 使用Windows数据保护API (DPAPI) 解密字符串
    static string DecryptWithDPAPI(string encryptedText)
    {
        try
        {
            // 将Base64字符串转换为字节数组
            byte[] encryptedBytes = Convert.FromBase64String(encryptedText);

            // 使用DPAPI解密数据，使用当前用户范围
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);

            // 将解密后的字节数组转换为字符串
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException)
        {
            Console.WriteLine("解密失败: 无效的Base64格式");
            throw new CryptographicException("解密失败: 无效的Base64格式");
        }
        catch (CryptographicException ex)
        {
            Console.WriteLine($"DPAPI解密失败: {ex.Message}");
            throw new CryptographicException("解密失败: 可能是数据损坏或在不同用户/计算机上解密", ex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解密失败: {ex.Message}");
            throw new CryptographicException("解密失败", ex);
        }
    }
}

// 保存的凭据类
public class SavedCredentials
{
    public string Username { get; set; }
    public string Password { get; set; }
    public string Choice { get; set; }
    public DateTime SaveDate { get; set; }
}
