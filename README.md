# Auto_Login_Net_JXUST

纯ai编写，工具为Deepseek Harness,模型为deepseek v4.1f
原本我写了个C#脚本自用，只有单纯的CLI界面，最近没事就用ai重写了，所以changelog有包含旧版修改信息，无需在意，我没公开发表过。

江西理工大学（JXUST）校园网 **Dr.COM ePortal** 认证客户端。基于 **WinUI 3（Windows App SDK）** 的桌面应用，一键认证、开机自动登录、DNS 优选与校内域名解析。

> **第三方非官方软件**：与学校及学校网络中心、信息中心无隶属或授权关系。完整条款见仓库根目录 [免责声明.txt](免责声明.txt)（安装向导的许可协议页即此全文）。

---

## 下载与安装

安装包都是**简体中文向导**，装到 `Program Files`、自动创建开始菜单快捷方式，升级时自动卸载已安装的版本并原地覆盖。两种版本的界面和功能完全相同，**只有携带的运行时不同**，按目标机器情况选一个即可（同一 `UpgradeCode`，不能同时装）。

**日常只出两种版本：全内置（`-was-fd`）与框架依赖（`-fd`）。** 早先那个「自包含 .NET」版（只带 .NET、仍要求系统装 Windows App Runtime，31.3 MB）已停止交付——它省下的 16 MB 换来的是「装不上就闪退」，不划算。

| 选它 | 文件 | 大小 | 目标机需预装 |
| --- | --- | --- | --- |
| **不确定目标机装过什么（默认出这个）** | `AutoLoginNet-<版本>-was-fd-x64.msi`<br>`…-portable-was-fd-x64.zip` | 46.7 MB | **什么都不用装** |
| 两个运行时都已装、只要最小包 | `AutoLoginNet-<版本>-fd-x64.msi`<br>`…-portable-fd-x64.zip` | 7.4 MB | Windows App Runtime 1.8 + .NET 10 |

- **`-was-fd`（全内置，默认）**：`.NET 10` 与 Windows App Runtime 原生层都在包里，目标是「拷到任何 Windows 10 1809+ 机器上都能跑」。
- **`-fd`（框架依赖）**：两个运行时都不带，体积最小。

`.zip` 是免安装版：解压到任意目录直接运行 `AutoLoginNet.exe`，不写注册表。

> **两种形态二选一，不能同时安装。** `-was-fd` 与 `-fd` 是**同一个产品、两种载荷**：`UpgradeCode` 相同（同一条升级链），`ProductCode` 由 WiX 每次构建自动生成。所以先装了其中一种、再装另一种时，Windows Installer 会把它当成「同版本升级」——自动卸载旧包、装上新包（仍装回原来的目录），不会并排出现两份，ARP 里也始终只有一条记录。要换形态，卸载当前版本再装另一个即可；`.zip` 免安装版不写注册表，不受此限制，可以和 MSI 版并存。

### 缺运行时会怎样

程序**启动即闪退，退出码 `0xC000027B` 或提示「找不到指定的模块」，界面上没有任何提示**。所以拿不准目标机状态时，直接用全内置版。

两个运行时都装一下只要两条命令（缺哪个装哪个，都装好了则什么都不做）：

| 运行时 | 版本要求 | 下载 |
| --- | --- | --- |
| Windows App Runtime | **1.8**（须与 `Microsoft.WindowsAppSDK` 引用版本对齐） | <https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe> |
| .NET 桌面运行时 | **10.x**（仅 `-fd` 版需要） | <https://dotnet.microsoft.com/download/dotnet/10.0> |

```powershell
# 先把上面两个安装器放进 Setup\Runtime\（获取方式见 Setup\Runtime\README.md）
pwsh -NoProfile -File .\Setup\Install-Runtime.ps1

# 只想看会做什么、不动系统
pwsh -NoProfile -File .\Setup\Install-Runtime.ps1 -WhatIf
```

> Windows App Runtime 是**按主版本并行**的产品线：装了 2.x **不等于**满足 1.8 的依赖。

### 离线分发

框架依赖版要发给别人，用 `-BundleRuntime` 把两个运行时安装器一起放进 `dist\Runtime\`，得到一份完全离线的交付物（安装器共约 160 MB，不入库）：

```powershell
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -FrameworkDependent -BundleRuntime
```

> 安装包未签名，首次安装时 Windows SmartScreen 会提示「未知发布者」，属预期行为。

---

## 功能

| 模块 | 说明 |
| --- | --- |
| **一键登录** | 自动探测网络环境 → 提交认证 → 分类反馈结果（成功 / 已在线 / 密码错误 / 欠费停机 / 网络异常） |
| **自动登录** | 「启动时自动登录」打开后，启动程序即用已保存凭据登录；瞬时失败按配置自动重试 |
| **注销断网** | 主动断开校园网认证会话（等价于门户上的「注销」）；仅在外网可达时可用，带二次确认 |
| **网络状态** | 首页与侧栏共享同一份探测结果，并行探测认证服务器与外网连通性，明确告知「需要认证 / 无需认证 / 网络未知」 |
| **开机自启** | 首页开关即可启用，以「登录时 + 最高权限」计划任务静默认证；可勾选「开机自启时最小化到系统托盘」，开机后只在托盘完成登录、不弹窗口 |
| **凭据安全** | Windows DPAPI（当前用户 + 附加熵）加密；文件复制到其他机器或用户账户下均无法解密 |
| **DNS 优选** | 用**真实 DNS 查询**测量各公共 DNS 的解析延迟（而非 ICMP Ping），逐项显示首选/备选延迟并给出推荐组合，只改当前上网所用网卡 |
| **上网网卡选择** | 认证与 DNS 都以选中的网卡为准：默认「自动选择（按默认路由）」，也可在网络工具页或托盘右键菜单里手动指定（按网卡名持久化） |
| **校内域名解析** | 「网络工具 → 修改 hosts 文件」把 16 条 JXUST 校内站点映射写入 hosts，解决「改用公共 DNS 后教务、办事大厅、校园卡等访问不了」；只维护带标记的区块，不动手写条目，写入前自动备份 |
| **连通性诊断** | Ping 失败自动回退 TCP 443，避免对端丢弃 ICMP 时误判为断网 |
| **运行日志** | 实时查看、按级别/关键字过滤；密码等敏感字段写入前已脱敏；增量渲染，长日志不卡顿 |
| **最小化到托盘** | 点「最小化」时窗口收进系统托盘而不是任务栏；可在设置里让「关闭主窗口」也收进托盘；左键/双击图标还原，右键菜单提供「显示主窗口」/「上网网卡」/「退出程序」 |
| **主题** | 跟随系统 / 浅色 / 深色，全部使用系统 Fluent 主题资源 |

---

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
├─ WindowsHostsFileService       # hosts 托管块写入（原地写 + 回读校验 + 失败回滚）
└─ ProcessRunner                 # 外部进程执行（含编码处理）

AutoLoginNet.App       # WinUI 3 界面（net10.0-windows10.0.19041.0）
├─ Pages/              # 首页 / 网络工具 / 运行日志 / 设置
├─ ViewModels/         # MVVM 视图模型
├─ Infrastructure/     # 主题、界面设置、托盘图标、网卡切换、单实例守卫、Shell 辅助
└─ Mvvm/               # 轻量 MVVM 基类（无第三方依赖）

AutoLoginNet.Tests     # 单元测试（MSTest，200 个用例）

Setup/                 # 打包与安装脚本 + WiX 工程（见「构建」）
                       #   AutoLoginNet.Setup.wixproj：接进解决方案，VS / 命令行一键出 MSI
```

依赖方向严格单向：`App → Windows → Core`。Core 不引用任何 Windows API，因此认证解析、凭据格式、日志脱敏等关键逻辑都能在测试中直接验证。

---

## 构建

要求：**Windows 10 1809+**、**.NET 10 SDK**。

```powershell
dotnet build Auto_Login_Net_JXUST.sln -c Release
```

产物：`AutoLoginNet.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\AutoLoginNet.exe`

> 解决方案里还挂着 `Setup\AutoLoginNet.Setup.wixproj`，所以这条命令会**顺带产出安装包**（发布 → 裁剪 → 出 MSI/ZIP，约 50 秒）。只想编译程序、不打包就单独构建 `AutoLoginNet.App`。

> 该产物是**非打包 + 框架依赖**形态（csproj 里 `SelfContained` 与 `WindowsAppSDKSelfContained` 均为 `false`），本机开发跑得动是因为开发机装了运行时。命令行上的 `--self-contained` 会覆盖 csproj 的 `<SelfContained>`，所以两种交付形态全靠打包脚本显式传参区分。

### 打包安装包

一次性准备 WiX v5：

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
```

一条命令完成「发布 → 裁剪 → 验证 → 出 MSI/ZIP」，两种形态各一条（**不加形态开关就是全内置**）：

```powershell
# 全内置（默认，含 .NET 与 Windows App Runtime，约 47 MB，文件名带 -was-fd）
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -Desktop

# 框架依赖（文件名带 -fd）
pwsh -NoProfile -File .\Setup\Export-Installer.ps1 -FrameworkDependent -Desktop

# 已有发布目录、只要 MSI/ZIP 时（形态必须与发布目录一致，脚本会自检）
pwsh -NoProfile -File .\Setup\Build-Installer.ps1 -SkipPublish -FrameworkDependent
```

> 早先的第三种形态「自包含 .NET」（只带 .NET，仍需系统 Windows App Runtime）已停止交付：`-SelfContained` 现在直接报错，不会再导出一个名字和内容都容易搞混的中间包。

`Export-Installer.ps1` 依次做：编译原生自定义动作（升级时定位已安装目录）→ `dotnet publish`（按形态决定是否自包含）→ 按「运行时不会被加载」的证据裁剪（`Setup\PublishPruneRules.ps1`）→ 跑裁剪后可用性验证（`Setup\Verify-PrunedPublish.ps1`）→ 生成 MSI 与 ZIP → 打印大小与 SHA256。

常用参数：`-Version 2.1.0`（默认读 csproj）、`-SkipPrune` / `-SkipVerify`（排查用）、`-BundleRuntime`（连带运行时安装器）、`-Desktop`（复制到桌面）。

另有三个开关是给 MSBuild 工程复用的（见下一节），直接手跑一般用不到：`Export-Installer.ps1 -PrepareOnly`（只准备载荷与 WiX 源文件）、`Build-Installer.ps1 -GenerateOnly -SkipPublish`（只生成 WiX 源文件）、`Build-Installer.ps1 -FinalizeMsi <MSI 路径>`（定稿并落盘一个外部编译好的 MSI）。

**实测体积（2.0.8）**：

| 形态 | 发布目录 | 裁剪后 | MSI | ZIP |
| --- | --- | --- | --- | --- |
| 全内置（`-was-fd`，默认） | 159.6 MB / 498 个文件 | **151.5 MB / 287 个文件** | **46.7 MB** | **59.8 MB** |
| 框架依赖（`-fd`） | 36.8 MB / 51 个文件 | **34.6 MB / 25 个文件** | **7.4 MB** | **9.3 MB** |

体积主要来自三块，与程序自身代码无关（程序自身约 2.2 MB）：`.NET 10` 运行时 63 MB、WinUI 原生层 50 MB、WinRT 托管投影 33 MB（其中 `Microsoft.Windows.SDK.NET.dll` 单个 23.7 MB，且**不能裁剪**——WinUI 3 + CsWinRT 按名字反射解析投影类型，`PublishTrimmed` 会让带 `x:Bind` 的页面直接崩）。

体积优化的坑记在这里，改打包脚本前先读：

- `-p:WindowsAppSDKSelfContained=true` **必须与 `--self-contained true` 同时传**。只传前者时，csproj 的 `<SelfContained>false</SelfContained>` 会让它退化成「有 Windows App Runtime、没有 .NET」的中间形态（发布目录 93.88 MB），装到干净机器上照样闪退。
- `Microsoft.UI.Xaml.Phone.dll`、`WinUIEdit.dll`、`marshal.dll`、`MRU.dll`、`*.winmd` 等**不能删**，删了启动即失败；`en-us` 语言目录也不能删（WinUI 按目录名加载，删了崩溃）。
- 裁剪只做有证据的删除（程序集引用闭包、内嵌 Reg-Free WinRT 清单、源码 grep），规则与理由见 `Setup\PublishPruneRules.ps1`。
- `AutoLoginNet.pri` 与 `App.xbf` 缺失会导致加载 XAML 时崩溃，构建阶段直接报错，不会产出坏包。

### 在 Visual Studio / MSBuild 里一键出包

`Setup\AutoLoginNet.Setup.wixproj` 是 SDK 式 WiX v5 工程，已经加进 `Auto_Login_Net_JXUST.sln`。
它**不重写打包规则**，只把命令行那几步挂到 MSBuild 上：生成前跑 `Export-Installer.ps1 -PrepareOnly`（编译原生自定义动作 → `dotnet publish` → 裁剪 → 生成 `Setup\obj\Product.wxs` / `Install.wxs`），WiX SDK 编译出 MSI，生成后再跑 `Build-Installer.ps1 -FinalizeMsi`（形态自检 → 中文化断言 → 落 `dist\` 规范命名 → 免安装 ZIP）。于是 Visual Studio 的「生成解决方案」、`dotnet build`、VS MCP 的 `build_solution` 与命令行脚本产出的是同一套产物。

```powershell
# 在 VS 里直接「生成解决方案」即可；命令行等价于：
dotnet build Setup\AutoLoginNet.Setup.wixproj -c Release                                   # 全内置（默认）
dotnet build Setup\AutoLoginNet.Setup.wixproj -c Release -p:SetupFlavor=FrameworkDependent # -fd
```

- **交付形态**：`-p:SetupFlavor=` 取 `AllBundled`（默认，全内置）/ `FrameworkDependent`（`-fd`）。形态一变会强制重新准备载荷；万一载荷与所要求的形态不符，`-FinalizeMsi` 会直接报错，绝不会印出名字和内容对不上的包。`SelfContained` 已于 2026-10-15 停用，传了会报错。
- **载荷固定按 Release 编译**：解决方案当前配置只影响程序自身，安装包内容永远是 Release 载荷。否则在 VS 里用 Debug 生成，会把 Debug 载荷灌进 `dist\AutoLoginNet`、并盖上与之前 Release 那一对同名的 MSI/ZIP（实测踩到过）。真要出 Debug 载荷的包，用 `-p:SetupPayloadConfiguration=Debug`。
- **复用已有载荷**：`-p:SetupSkipPublish=true`（跳过发布与裁剪，只用现有 `dist\AutoLoginNet`）。
- **默认跳过「裁剪后可用性验证」**：那一步会真的启动应用做 UI 自动化探测，IDE 里每次生成都跑太重；想跑就加 `-p:SetupSkipVerify=false`。命令行直接跑 `Export-Installer.ps1` 时它仍然是默认开启的。
- **增量**：应用源码或打包输入没变就整段跳过（不重新发布/裁剪/编译），实测重复生成 2 秒；改了源码或换了形态才重跑。
- **前提**：机器上有 PowerShell 7（`pwsh`）。脚本是无 BOM 的 UTF-8 且用了 PowerShell 7 语法，**不能用 Windows PowerShell 5.1 代替**；没有 `pwsh` 时工程会先探测再给出明确报错，也可以用 `-p:PowerShellHost=D:\path\pwsh.exe` 指定。
- **不需要任何 WiX 的 VS 扩展**：SDK 式 `.wixproj` 靠 NuGet 还原（`WixToolset.Sdk` / `WixToolset.UI.wixext`），`dotnet build` 就能出 MSI。反过来，装成 WiX **v3** 的 Votive 扩展对本项目没用（它按 v3 语法提供 IntelliSense，编译不了 v4/v5 语法的 `Product.wxs.template`）。
- 新加入解决方案的工程，要先在 VS 里**重新加载解决方案**才会出现。
- WiX 的直接产物是 `Setup\bin\<配置>\AutoLoginNet.Setup.msi`，`-FinalizeMsi` 再把它校验、复制成 `dist\AutoLoginNet-<版本>[-fd|-was-fd]-x64.msi` 并配套生成免安装 ZIP。

### 安装包中文化是硬断言

MSI 打完后会读安装包的表逐项校验（`Setup\Test-InstallerLocalization.ps1`）：10 个关键界面串、许可协议正文**逐行**比对 `Setup\LicenseText.txt`、`ProductLanguage=2052`，任何一项不过就中止打包。

- 界面文案：`Setup\WixUI_zh-CN.wxl`。注意 **`.wxl` 不会被 WiX 自动发现**，只有打包时显式传 `-loc` 才生效，这一步不能省。
- 协议正文：维护入口是仓库根目录 `免责声明.txt`，同步副本 `Setup\LicenseText.txt`；改完跑 `pwsh -File .\Setup\New-LicenseRtf.ps1` 重新生成 RTF，再重新打包。

---

## 运行

```powershell
.\AutoLoginNet.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\AutoLoginNet.exe
```

### 托盘图标与退出

程序常驻系统托盘：主窗口隐藏 / 最小化后从托盘图标左键（或双击）还原，右键菜单提供「显示主窗口」「上网网卡 ▸」「退出程序」。
关闭主窗口的行为由「设置 → 外观 → 关闭主窗口时最小化到系统托盘」决定：默认关闭即退出程序（托盘图标随之移除）；
勾选后关闭按钮只把窗口收进托盘，退出请用托盘右键菜单的「退出程序」。
开机自启（`--autostart`）同样常驻：勾选「开机自启时最小化到系统托盘」时登录完成后只留托盘图标，否则显示主窗口。
托盘图标由自己 P/Invoke `Shell_NotifyIconW` 实现（WinUI 3 没有托盘 API）；图标创建失败时自动退化为普通最小化，
不会出现「窗口藏起来却点不回来」的情况。

> 注：从本机命令行直接运行构建产物时，如果 exe 所在目录被打过低完整性标签（例如某些沙箱环境），
> 进程会是低 IL 而无法与资源管理器通信，托盘图标会加不上并退化为普通最小化。

### 命令行参数

| 参数 | 说明 |
| --- | --- |
| （无） | 正常启动图形界面 |
| `--autostart` | 由开机自启计划任务调用：先静默完成登录，再按设置只留托盘图标或显示主窗口（进程常驻） |
| `--hosts apply` / `--hosts restore` | 提权进程写入 / 还原 hosts 托管块（另有 `--hosts-file` 供测试覆盖路径） |
| `--register-autostart 0\|1` | 内部参数：提权进程注册 / 移除自启任务 |
| `--apply-dns apply\|automatic` | 内部参数：提权进程应用 DNS；可带 `--adapter-name <网卡名>` 指定目标网卡，缺失则按路由自动选择 |

自启登录结果码：`0` 成功 / `1` 无凭据 / `2` 认证失败 / `3` 异常 / `4` 超时（用于决定界面初始状态，进程不再据此退出）；提权子命令仍以 `0`/`1` 表示成功 / 失败。

---

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
      // …共 16 条，完整列表见「网络工具」页
    ]
  }
}
```

配置损坏或字段缺失时自动回退到内置默认值，不会导致程序无法启动。

---

## 数据与安全

| 路径 | 内容 |
| --- | --- |
| `%AppData%\AutologinForDrcom\credentials.json` | 凭据（密码为 DPAPI 密文，含 SchemaVersion 校验） |
| `%AppData%\AutologinForDrcom\appsettings.json` | 运行配置 |
| `%AppData%\AutologinForDrcom\ui-settings.json` | 界面偏好（主题等） |
| `%AppData%\AutologinForDrcom\autologin.log` | 日志（按大小滚动，自动脱敏） |
| `C:\Windows\System32\drivers\etc\hosts.bak.*` | 写入 hosts 前的自动备份 |

安全设计要点：

- **日志双重脱敏**：写入前用正则把 `password` / `token` / `secret` 等字段值替换为 `***`，且账号只保留前 2 位与末 1 位。
- **凭据格式带版本号**：遇到不受支持的凭据格式会明确提示重新登录，而不是静默降级使用。
- **提权操作隔离**（计划任务、DNS、hosts）单独以管理员身份执行并立即退出，主程序始终以普通权限运行。
- **hosts 写入可靠**：原地写入而非「临时文件改名替换」（部分安全软件的 HIPS 会拦截对 hosts 的改名覆盖），写完立即回读比对，不一致就用写入前的内容回滚。

> **关于密码传输**：认证服务器 `/eportal/portal/login` **只从 URL 查询串读取账号与密码**，完全不解析 POST body（实测：凭据放在 body 中会返回「无法获取用户认证账号！」）。因此密码必须出现在 URL 中——这是服务器端的既有约定，客户端没有替代方案。风险由两点收敛：请求只发往校园网内网的明文 HTTP 门户（`http://10.17.8.18:801`），且日志在写入前会把 `user_password` 脱敏。

---

## 测试

```powershell
dotnet test Auto_Login_Net_JXUST.sln -c Release
```

181 个用例，覆盖认证响应解析、凭据加解密、日志脱敏、配置校验与回退、网络判定、hosts 托管块生成与写入（含「原地写入不替换文件」的回归断言）。

---

## 故障排查

| 现象 | 处理 |
| --- | --- |
| 程序启动即闪退、没有任何界面 | 缺运行时。装 Windows App Runtime 1.8（`-fd` 版还要 .NET 10），或直接用全内置版 `-was-fd` |
| 提示「网络异常 / 无法连接认证服务器」 | 确认已连接校园网；在设置页核对认证服务器地址与端口 |
| 提示「账号不可用」 | 账号欠费或停机，需前往网络中心处理 |
| 点了「注销」后上不了网 | 这是预期行为（注销会主动断开会话）。重新点「立即登录」即可恢复 |
| 注销按钮是灰的 | 只在检测到外网可达时才可注销；先点「重新检测」，或本来就没在线 |
| 开机后没有自动认证 | 在「设置 → 启动」确认开机自启已开启（需要管理员授权） |
| hosts 写入失败，提示被安全软件拦截 | 火绒等 HIPS 会拦截对 `drivers\etc` 目录的改名/新建；程序已改为原地写入，若仍失败请在该安全软件里放行本程序，或临时退出后重试 |
| 校内站点访问不了 | 「网络工具 → 修改 hosts 文件」写一次 hosts；只维护带标记的区块，不动手写条目 |
| 换了地方上网无法认证 | 打开首页的「跳过网络检测，直接认证」再试 |
| DNS 测速中部分服务器显示「—」 | 属正常：这些 DNS 在当前网络下不提供解析服务（校园网通常只放行国内解析器，腾讯 / Google / Cloudflare 的 53 端口会被拦截）。能测出延迟的即为可用，选延迟最低的即可 |
| 需要详细排查 | 「运行日志」页可实时查看并过滤，或点「打开日志目录」 |

---

## 许可

[MIT](LICENSE.txt)
