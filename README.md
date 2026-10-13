# Auto_Login_Net_JXUST

江西理工大学（JXUST）校园网 **Dr.COM ePortal** 认证客户端。
基于 **WinUI 3（Windows App SDK）** 的现代化桌面应用，支持一键认证与开机自动登录。

> v2.0 为完全重构版本：界面重写为 WinUI 3，核心逻辑重新分层，并修正了若干安全与可用性缺陷。
> 详见 [CHANGELOG.md](CHANGELOG.md)。

## 功能

| 模块 | 说明 |
| --- | --- |
| **一键登录** | 自动探测网络环境 → 提交认证 → 分类反馈结果（成功 / 已在线 / 密码错误 / 欠费停机 / 网络异常） |
| **自动登录** | 可开启「启动时自动登录」，打开程序即用已保存凭据登录；瞬时失败按配置自动重试 |
| **注销断网** | 主动断开校园网认证会话（等价于门户上的「注销」）；仅在外网可达时可用，带二次确认 |
| **网络状态** | 并行探测校园网认证服务器与外网连通性，明确告知「需要认证 / 无需认证 / 网络未知」 |
| **开机自启** | 首页开关即可启用，以「登录时 + 最高权限」计划任务静默认证，无窗口、不打断使用 |
| **凭据安全** | Windows DPAPI（当前用户 + 附加熵）加密，文件复制到其他机器或用户下均无法解密 |
| **DNS 优选** | 用**真实 DNS 查询**测量各公共 DNS 的解析延迟（而非 ICMP Ping），逐项显示首选/备选延迟并给出推荐组合，只改当前上网所用网卡 |
| **校内域名解析** | 一键把 JXUST 校内站点写入 hosts，解决「改用公共 DNS 后教务、办事大厅、校园卡等访问不了」的问题；只维护带标记的区块，不动你的手写条目，写入前自动备份 |
| **连通性诊断** | Ping 失败自动回退 TCP 443，避免对端丢弃 ICMP 时误判 |
| **运行日志** | 实时查看、按级别/关键字过滤，密码等敏感字段写入前已脱敏；增量渲染，长日志不卡顿 |
| **主题** | 跟随系统 / 浅色 / 深色，全部使用系统 Fluent 主题资源 |

## 项目结构

```
AutoLoginNet.Core      # 领域逻辑（net10.0，无 Windows 依赖，可跨平台测试）
├─ Auth/               # Dr.COM ePortal 认证与响应解析
├─ Configuration/      # 配置模型与校验
├─ Credentials/        # 凭据存储（加密算法通过接口注入）
├─ Diagnostics/        # 日志与脱敏
├─ Network/            # 网络探测、认证需求判定
└─ Services/           # 配置读写、登录编排、开机自启、Ping

AutoLoginNet.Windows   # 平台实现（net10.0-windows）
├─ DpapiCredentialProtector      # DPAPI 加解密
├─ WindowsNetworkAdapterResolver # 按路由选择上网网卡
├─ WindowsDnsService             # netsh 读写 DNS
└─ ProcessRunner                 # 外部进程执行（含编码处理）

AutoLoginNet.App       # WinUI 3 界面（net10.0-windows10.0.19041.0）
├─ Pages/              # 首页 / 网络工具 / 运行日志 / 设置
├─ ViewModels/         # MVVM 视图模型
├─ Infrastructure/     # 主题、界面设置、Shell 辅助
└─ Mvvm/               # 轻量 MVVM 基类（无第三方依赖）

AutoLoginNet.Tests     # 单元测试（181 个用例）
```

依赖方向严格单向：`App → Windows → Core`。Core 不引用任何 Windows API，
因此认证解析、凭据格式、日志脱敏等关键逻辑都能在测试中直接验证。

## 安装

提供两种分发形式，每种都有「自包含」和「框架依赖」两版（**同一份产品定义，只是载荷与文件名后缀不同，不能同时安装**）：

| 形式 | 适用场景 |
| --- | --- |
| `AutoLoginNet-<版本>-x64.msi` / `-portable-x64.zip` | **自包含**：`.NET 10` 运行时打进包，约 31.4 MB / 99.6 MB 解包，目标机**只需**预装 Windows App Runtime 1.8 |
| `AutoLoginNet-<版本>-fd-x64.msi` / `-portable-fd-x64.zip` | **框架依赖**（`-fd`）：包内不含运行时，约 7.4 MB / 34.6 MB 解包，目标机需预装 **两个**运行时 |

两种都：装到 `Program Files`、自动创建开始菜单快捷方式、支持升级安装（自动卸旧版），**安装向导与许可协议均为简体中文**；
ZIP 版解压到任意目录直接运行 `AutoLoginNet.exe`。

> **框架依赖版（-fd）装之前必须先装两个运行时**，否则程序启动即闪退（`0xC000027B` / “找不到指定的模块”，没有任何界面提示）：
> 自包含版则只需要下面表格里的 **Windows App Runtime 1.8**（`.NET` 已随包提供）。
>
> | 运行时 | 版本 | 下载 |
> | --- | --- | --- |
> | Windows App Runtime | **1.8**（对应 `Microsoft.WindowsAppSDK 1.8`） | <https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe> |
> | .NET 桌面运行时 | **10.x**（仅 `-fd` 版需要） | <https://dotnet.microsoft.com/download/dotnet/10.0> |
>
> 两条命令即可装好，缺哪个装哪个、都装好了则什么都不做：
>
> ```powershell
> # 把两个安装器放到 Setup\Runtime\ 后（见 Setup\Runtime\README.md）
> pwsh -NoProfile -File .\Setup\Install-Runtime.ps1
> ```
>
> 想让交付物「离线也能装」，用 `-BundleRuntime` 打包（把两个安装器一起放进 `dist\Runtime\`）：
>
> ```powershell
> pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -BundleRuntime
> ```
>
> 注意 Windows App Runtime 是**按主版本并行**的产品线：装了 2.x 不等于满足 1.8 的依赖。
>
> 安装包未签名，首次运行/安装时 Windows SmartScreen 会提示"未知发布者"，属预期行为。
> 安装向导的许可协议页是**第三方非官方免责声明**（正文维护入口 `免责声明.txt`，同步到 `Setup\LicenseText.txt`），必须勾选同意才能继续。

## 构建

要求：**Windows 10 1809+**、**.NET 10 SDK**。

```powershell
dotnet build Auto_Login_Net_JXUST.sln -c Release
```

产物：`AutoLoginNet.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\AutoLoginNet.exe`

> 默认发布为 **非打包 + 框架依赖**：不携带 .NET 运行时，也不携带 Windows App Runtime，
> 所以发布目录只有约 37 MB、裁剪后约 **34.6 MB / 25 个文件**。代价是目标机必须先装上面那两个运行时，
> 且 **Windows App Runtime 的版本必须与 `Microsoft.WindowsAppSDK` 的引用版本对齐**。
> 命令行上的 `--self-contained` 会覆盖 csproj 的 `<SelfContained>`，两种产物都靠打包脚本显式传参区分。

## 打包安装包

```powershell
# 需要 WiX v5（一次性准备）
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2

# 自包含版（默认，含 .NET 运行时）
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -SelfContained -Desktop

# 框架依赖版（文件名带 -fd）
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -FrameworkDependent -Desktop

# 两版一起导出
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -SelfContained -Desktop
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -FrameworkDependent -Desktop

# 只需要 MSI/ZIP、已有发布目录时（-FrameworkDependent 需与发布形态一致）
pwsh -NoProfile -File .\Setup\Build-Installer.ps1 -SkipPublish -FrameworkDependent
```

`Export-Installer.ps1` 一条命令完成：编译原生自定义动作 → `dotnet publish`（按形态决定是否自包含）→
按「运行时不会被加载」的证据裁剪（`Setup\PublishPruneRules.ps1`）→ 裁剪后自动跑
`Setup\Verify-PrunedPublish.ps1`（13 项检查，任一不过就中止）→ 生成 MSI 与 ZIP → 打印大小与 SHA256。
常用参数还有 `-Version 2.1.0`、`-SkipPrune`（排查用）、`-BundleRuntime`（连带运行时安装器）。
两种形态共用同一份 `UpgradeCode`/`ProductCode` 规则，属于同一产品的两种分发方式，**不能同时装**。

实测体积（2.0.8）：

| 形态 | 发布目录 | 裁剪后 | MSI | ZIP |
| --- | --- | --- | --- | --- |
| 自包含（默认，含 .NET 运行时） | 102.5 MB / 229 个文件 | **99.6 MB / 203 个文件** | **31.4 MB** | **39.5 MB** |
| 框架依赖（`-fd`） | 36.8 MB / 51 个文件 | **34.6 MB / 25 个文件** | **7.4 MB** | **9.3 MB** |

框架依赖版裁剪后仍有 24.9 MB 是 `Microsoft.Windows.SDK.NET.dll`——它是 WinRT 的托管投影，
再省无可省。两版都**不含** Windows App Runtime 的原生层（`Microsoft.ui.xaml.dll` 等，约 50 MB），
这是非打包应用按设计从系统运行时获取的；真要连它一并内置，需要把 `WindowsAppSDKSelfContained` 设为 `true`，
发布目录会涨回 ~152 MB，目前没有采用。

程序自身的 XAML/资源目录 `AutoLoginNet.pri` 与 `App.xbf` 必须存在——
缺了程序会在加载 XAML 时崩溃，所以构建阶段直接报错而不是产出坏包。

MSI 打完后还会自动读安装包的表做中文化断言（`Setup\Test-InstallerLocalization.ps1`）：
10 个关键界面串、许可协议正文逐行比对 `Setup\LicenseText.txt`、`ProductLanguage=2052`，
任何一项不过就中止打包。安装界面文案在 `Setup\WixUI_zh-CN.wxl`，协议正文在
`免责声明.txt`（同步副本 `Setup\LicenseText.txt`；改完跑 `Setup\New-LicenseRtf.ps1` 重新生成 RTF，
再重新打包即可）。

> `.wxl` 不会被 WiX 自动发现，只有 `-loc` 传进去才生效，这一点别改。

## 运行

```powershell
# 直接运行界面
.\AutoLoginNet.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\AutoLoginNet.exe
```

### 命令行参数

| 参数 | 说明 |
| --- | --- |
| （无） | 正常启动图形界面 |
| `--autostart` | 静默登录后退出（由开机自启计划任务调用，无窗口） |
| `--register-autostart 0\|1` | 内部参数：提权进程注册/移除自启任务 |
| `--apply-dns apply\|automatic` | 内部参数：提权进程应用 DNS |

退出码：`0` 成功 / `1` 无凭据 / `2` 认证失败 / `3` 异常 / `4` 超时。

## 测试

```powershell
dotnet test AutoLoginNet.Tests\AutoLoginNet.Tests.csproj
```

## 配置

配置文件：`%AppData%\AutologinForDrcom\appsettings.json`（首次运行后可在设置页保存生成）。

```jsonc
{
  "auth": {                          // 认证服务器
    "host": "10.17.8.18",
    "port": 801,
    "loginPath": "/eportal/portal/login",
    "loginCallback": "dr1003",
    "jsVersion": "4.1.3",
    "httpTimeoutSeconds": 12
  },
  "probes": {                        // 网络探测
    "campusProbeUrl": "http://10.17.8.18/",
    "campusProbeTimeoutMs": 3000,
    "externalProbeHost": "baidu.com",
    "externalProbeTimeoutMs": 1500,
    "externalFallbackTcpPort": 443
  },
  "logging": { "maxFileSizeMb": 5, "retainedFileCount": 5, "inMemoryCapacity": 1000 },
  "startup": {                       // 启动与重试
    "networkReadyMaxRetries": 12,
    "networkReadyRetryDelayMs": 1000,
    "loginRetryCount": 2,
    "loginRetryDelaySeconds": 3
  },
  "tools": {                         // 工具页外链与 hosts
    "campusSpeedTestUrl": "http://speedtest.jxust.edu.cn",
    "internetSpeedTestUrl": "http://wangsuceshi.bmcx.com",
    "pingTargets": [ "10.17.8.18", "baidu.com", "223.5.5.5", "www.jxust.edu.cn" ],
    "enableHostsFeature": true,      // 是否启用 hosts 域名解析功能
    "hostsEntries": [                // 注入到 hosts 的校内域名映射
      { "ipAddress": "219.229.232.50", "hostName": "jxust.edu.cn", "comment": "官网" },
      { "ipAddress": "2001:250:6c0b:6010::10:50", "hostName": "jxust.edu.cn", "comment": "" },
      { "ipAddress": "10.16.17.37", "hostName": "jw.jxust.edu.cn", "comment": "教务系统" }
      // …共 14 条，完整列表见「网络工具」页
    ]
  }
}
```

配置损坏或字段缺失时会自动回退到内置默认值，不会导致程序无法启动。

## 数据与安全

| 路径 | 内容 |
| --- | --- |
| `%AppData%\AutologinForDrcom\credentials.json` | 凭据（密码为 DPAPI 密文，含 SchemaVersion 校验） |
| `%AppData%\AutologinForDrcom\appsettings.json` | 运行配置 |
| `%AppData%\AutologinForDrcom\ui-settings.json` | 界面偏好（主题等） |
| `%AppData%\AutologinForDrcom\autologin.log` | 日志（按大小滚动，自动脱敏） |

安全设计要点：

- **日志双重脱敏**：写入前用正则把 `password` / `token` / `secret` 等字段值替换为 `***`，
  且日志中账号只保留前 2 位与末 1 位。
- **凭据格式带版本号**：遇到不受支持的旧格式会明确提示重新登录，而不是静默降级使用。
- 提权操作（计划任务、DNS、hosts）单独以管理员身份执行并立即退出，主程序始终以普通权限运行。

> **关于密码传输**：认证服务器 `/eportal/portal/login` **只从 URL 查询串读取账号与密码**，
> 完全不解析 POST body（实测：凭据放在 body 中会返回「无法获取用户认证账号！」）。
> 因此密码必须出现在 URL 中——这是服务器端的既有约定，客户端没有替代方案。
> 风险由两点收敛：请求只发往校园网内网的明文 HTTP 门户（`http://10.17.8.18:801`），
> 且日志在写入前会把 `user_password` 脱敏。

## 故障排查

| 现象 | 处理 |
| --- | --- |
| 提示「网络异常 / 无法连接认证服务器」 | 确认已连接校园网；在设置页核对认证服务器地址与端口 |
| 提示「账号不可用」 | 账号欠费或停机，需前往网络中心处理 |
| 点了「注销」后上不了网 | 这是预期行为（注销会主动断开会话）。重新点「立即登录」即可恢复 |
| 注销按钮是灰的 | 只在检测到外网可达时才可注销；先点「重新检测」，或本来就没在线 |
| 开机后没有自动认证 | 在「设置 → 启动」确认开机自启已开启（需要管理员授权） |
| 换了地方上网无法认证 | 打开首页的「跳过网络检测，直接认证」再试 |
| DNS 测速中部分服务器显示「—」 | 属正常结果：这些 DNS 在当前网络下不提供解析服务（校园网通常只放行国内解析器，腾讯 / Google / Cloudflare 的 53 端口会被拦截）。能测出延迟的即为可用，直接选延迟最低的即可 |
| 需要详细排查 | 「运行日志」页可实时查看并过滤，或点击「打开日志目录」 |

## 说明

各版本改动记录见 [CHANGELOG.md](CHANGELOG.md)。
