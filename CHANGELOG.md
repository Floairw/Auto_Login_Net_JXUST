# 更新日志

本文件记录 Auto_Login_Net_JXUST 的版本变更。
v2.0 是一次彻底重构，v1.x 的历史记录保留在文件末尾。

---

## 2026-10-18 — DNS 优选只测、只改「指定网卡」

**用户要求：「DNS优选功能只修改和测试指定网卡的DNS」。核对代码发现三处都与这个要求不符，
最要命的是两处静默换卡和一处不绑源地址的探测。**

- **根因 1（改错网卡）**：`AdapterSwitcher.ResolveAsync` 在指定网卡（如已被拔掉的「以太网 2」）
  找不到时，直接回退成「按认证服务器路由解析」，界面显示的「当前网卡」和随后写入的 DNS
  都变成了另一张卡（WLAN / 新的手机共享网卡）；提权子进程 `ElevatedCommands.ResolveAdapterAsync`
  同样按名找不到就换路由解析——两处都只记一行日志，用户看到的却是「改成功了」。
- **根因 2（测错网卡）**：`UdpDnsLatencyProbe` 的 `UdpClient` 不绑定本地地址，
  探测包按系统路由表出网；界面上写着「测的是这张卡」，实际测到的可能是另一张卡。
- **修复（Core）**：`IDnsLatencyProbe.MeasureAsync` 增加 `sourceAddress` 参数，
  实现里用 `new UdpClient(new IPEndPoint(local, 0))` / `new TcpClient(new IPEndPoint(local, 0))`
  绑定该网卡地址后再发查询（TCP 53 回退同样绑定）；地址缺失或非法时返回明确说明而不是改用默认路由，
  绑定失败（地址不在本机）也会以「从网卡地址 X 发出查询失败」结束，不会静默落到别的网卡。
- **修复（App）**：`AdapterSwitcher.ResolveAsync` 改为指定网卡找不到就返回 `null`
  （日志 Warning「已跳过（不会改用其他网卡）」），只有「自动选择」才按路由解析；
  `NetworkToolsViewModel` 增加 `EnsureAdapterAsync`（缓存网卡已拔掉时重新解析），
  测试前/应用前都先确认目标卡还在，不可用就只提示「指定的上网网卡「X」不存在或未连接…」并中止；
  `MeasureSlotAsync` 把网卡 IPv4 地址作为源地址传入探测；
  测试状态文本与结果带上「（经由「X」）」「正在从「X」（IP）测试…」，让用户一眼看出测的是哪张卡；
  `ElevatedCommands` 按名找不到时返回失败结果文件「指定的上网网卡「X」不存在或未连接，DNS 未修改（不会改用其他网卡）」。
- **顺手修掉的两个界面问题**：「改为自动获取」按钮原来绑 `CanApply`（要等测试出推荐才可用），
  改为独立的 `CanResetDns`；`MeasureCommand` 的可执行条件原来写反（没有网卡时反而可点），
  改为 `_adapter.HasValue && !IsMeasuring`，按钮在指定网卡不可用时是灰的。
- **界面文案**：网络工具页 DNS 卡片说明改为「测试与修改都只针对下面选中的上网网卡：探测包从这张网卡自己的地址发出，
  DNS 也只写这张网卡；指定网卡不在线时会提示不可用，不会改用其他网卡，也不会动虚拟网卡」。
- **测试**：新增 3 个用例（源地址非法、源地址不在本机必须失败而不是回退、本机回环源地址可正常发起探测并超时）；
  全量 **277/277** 通过（原 274 + 3）。App 用临时 `OutputPath` 编译验证 0 警告 0 错误。
- **README**：功能表 DNS 优选 / 上网网卡选择两行改为「绑定指定网卡 IP 发出」「只认这张卡」；
  故障排查新增「提示指定的上网网卡不存在」「测出的延迟像是别的网卡的」两行。

## 2026-10-18 — 跃点改完立刻生效：清空系统目标缓存

**用户问「如何让跃点数的修改对现有软件生效？」——真机复现发现根因在 Windows 目标缓存（destination cache），
不是跃点没改对。**

- **真机现象**：手机 USB 共享恢复后（网卡从「以太网 2」变成「以太网 3」），
  `Get-NetRoute 0.0.0.0/0` 显示以太网 3 有效跃点 25、WLAN 9035，
  `Find-NetRoute -RemoteIPAddress 8.8.8.8` 已返回以太网 3（说明排序生效了），
  但 `Test-NetConnection 1.1.1.1 -Port 443` 的 SourceAddress 仍是 WLAN 的 10.99.233.207；
  `netsh interface ipv4 show destinationcache` 里 WLAN 接口下的 8.8.8.8 / 1.1.1.1 条目
  下一跳仍是 WLAN 网关——**旧下一跳被目标缓存钉住了**。
- **结论**：跃点改动对**新建连接**立即生效；已建立的连接不会迁移；
  目标缓存可以手动清（`netsh interface ipv4 delete destinationcache`，需管理员），
  不清也会随条目过期自行恢复。
- **修复**：`WindowsAdapterPriorityService` 在 apply/restore **成功后**调用
  `TryFlushDestinationCacheAsync`（`netsh interface ipv4 delete destinationcache`），
  把缓存里的旧下一跳清掉，点完按钮后新连接立刻走新网卡；这一步是「尽力而为」，
  失败只记 `Warning`「清空系统目标缓存失败（不影响跃点设置）」，不影响「跃点已改好」的结论。
- **测试**：新增 3 个用例（apply 后清缓存、restore 后清缓存、清缓存失败仍算成功）；
  假 netsh 增加 `DestinationCacheFails` 开关；全量 **274/274** 通过（原 271 + 3）。
- **界面**：网络工具页的说明补一句「点完会顺带清空系统目标缓存，新建的连接立即按新跃点选路；
  已经建立的连接不会迁移，需要重启对应软件才会换路径。」
- **重启后仍需重新点一次**：默认路由跃点只写到 `store=active`，重启由系统重建。

## 2026-10-18 — 修复：中文系统上「读取不到系统默认路由」

**上一节的功能一上真机就失败：用户点「设为系统首选网卡」，弹窗报「读取不到系统默认路由，无法设置系统首选网卡」，
日志里是「没有从 netsh 输出里解析到 IPv4 默认路由（0.0.0.0/0）」。根因不是权限，是解析器只认英文表。**

- **根因**：`netsh interface ipv4 show route` 的前两列（Publish / Type）是**本地化文本**，
  中文系统输出是 `否 手动 0 0.0.0.0/0 4 10.22.128.1`，而解析器用的正则写死了
  `(?:No|Yes|Age)\s+\w+`，于是**一行都匹配不上**；接口表是「数字开头」的硬约束所以没受影响，
  只有路由表全空，apply 直接失败（幸好失败在动系统之前，系统没被改坏）。
- **修复**（`AutoLoginNet.Windows\WindowsAdapterPriorityService.cs` 的 `ParseRouteTable`）：
  不再依赖 Publish/Type 的取值，改成「按空白切词 → 定位前缀列 `0.0.0.0/0`」，
  前缀前一个词是 Met、后两个词是 Idx 与网关；列语义与语言无关，中英文系统都能解析。
  网关不是 IPv4 地址（On-link / 在链路上）的行仍然跳过。
- **测试**：新增「中文系统真实输出」与「默认路由网关是 On-link」两个解析用例；
  服务层假 netsh 的路由表渲染也改成**中文表头 + 否/手动**，让所有 apply/restore 用例都在
  中文输出下跑一遍；全量 **271/271** 通过（原 269 + 2）。
- **真机验证**：非提权只读探针直接调用服务读路由，解析出 3 条默认路由
  （以太网 0 / WLAN 0 / 以太网 2 25），与 `Get-NetRoute` 完全一致。

## 2026-10-18 — 首选网卡真正影响选路：连默认路由跃点一起改

**只改接口跃点还不够：这台机器的默认路由自带显式跃点，Windows 依旧按路由选路，认证照样从 WLAN 走。**

- **真机根因**：接口跃点已改（以太网 2 = 10、WLAN = 9000），但
  `Get-NetRoute -DestinationPrefix 0.0.0.0/0` 显示 WLAN 的路由跃点仍是 0、
  以太网 2 仍是 25，`Find-NetRoute -RemoteIPAddress 8.8.8.8` 与认证服务器地址都返回
  **InterfaceAlias WLAN**。Windows 选默认路由比的是「路由自身跃点 + 接口跃点」，
  而这两条是带显式跃点的 Manual 路由，接口跃点 10/9000 抵消不了 0 vs 25 的差距。
- **新增默认路由跃点调整**（`AutoLoginNet.Core\Network\AdapterPriority.cs` 新增
  `AdapterRouteState` / `AdapterRouteChange` / `AdapterRoutePlan`、
  `AdapterPriorityPlanner.CreateRouteApplyPlan`）：首选网卡的 `0.0.0.0/0` 路由跃点设 0，
  其余网卡的默认路由设 9000；首选网卡没有默认路由时明确报错「没有默认路由（没有可用网关）」。
  **断开网卡的默认路由一律不写**：真机上断开网卡也会留在路由表里，但在断开接口上执行
  `set route` 不可靠，报错会导致整单回滚；而它不写也不会抢路——选路比的是
  「路由跃点 + 接口跃点」，断开网卡 0+25=25，首选网卡 0+10=10，仍首选优先。
  命令用 `netsh interface ipv4 set route 0.0.0.0/0 <接口名> <下一跳网关> metric=<n> store=active`，
  **store=active** 让改动只到下次重启——重启后 DHCP 会重建路由，不留持久化垃圾；
  接口跃点仍保持持久化，两者都记进快照。
- **快照与恢复同时覆盖路由**（`AdapterPrioritySnapshot` 新增可空 `Routes`，
  旧版快照文件仍可读）：恢复时按「网卡名 + 下一跳」匹配当前路由写回原跃点；
  网关都变了的旧路由不硬写，避免写回一条已经失效的网关。
- **重复应用不会覆盖原值**（新增 `AdapterPriorityPlanner.CreateSnapshot`）：
  已有旧快照时保留旧的原跃点（按网卡名、网卡名+下一跳匹配），
  否则「设置 → 再设置 → 恢复」会把中间状态当成出厂状态，真机上已经踩到。
  系统里已消失的网卡/路由从新快照里丢弃（无法恢复），新出现的按当前值记录。
- **失败回滚**先回滚默认路由、再回滚接口跃点（两者都倒序、用 `CancellationToken.None`），
  回滚完整才删快照；回读校验同样覆盖两张表。
- **界面文案**（`AutoLoginNet.App\Pages\NetworkPage.xaml`、`NetworkToolsViewModel.cs`）
  改成「同时改两处 IPv4 跃点」，并说明默认路由改动只保留到下次重启。
- **验证**：计划器新增 13 个路由/快照用例（含「重复 apply 保留最早原值」），
  服务层用假 netsh 内存表覆盖成功、失败回滚、恢复、旧快照、首选无路由等场景；
  真机复测用 `Find-NetRoute` 确认 `InterfaceAlias` 从 WLAN 翻到以太网 2。

## 2026-10-17 — 上网网卡真正生效：改系统 IPv4 跃点

**以前「上网网卡」只是程序自己按名字选路，系统并不知情；现在它能真的改系统首选，而且改得动也退得回。**

- **只改 IPv4 接口跃点**（`AutoLoginNet.Windows\WindowsAdapterPriorityService.cs`）：首选网卡设 10，
  其余**已连接**网卡设 9000，用 `netsh interface ipv4 set interface <名称> metric=<n>` 落地。
  选择它的原因是可逆且影响面小：不禁用网卡、不动 IPv6（校园认证与 DNS 查询都走 IPv4）、
  不碰路由表；断开中的网卡一律不写，避免把「插上就用」的备用网卡锁成低优先级。
- **先存快照再动系统**（`AutoLoginNet.Core\AdapterPrioritySnapshotStore.cs`，
  `%AppData%\AutologinForDrcom\adapter-priority.json`）：写不进快照就不改系统；
  逐条 set 之后**回读两张表校验**，任何一条失败或不生效就倒序回滚（回滚用
  `CancellationToken.None`，不能被取消打断），回滚完整才删快照，否则保留现场供排查。
- **回环接口强制排除**：实测本机 `netsh` 把 `Loopback Pseudo-Interface 1` 也标成「已连接」，
  早期实现会给它写 9000。现在按名称含 `Loopback` / `Pseudo-Interface` / 「回环」判定并跳过；
  若用户直接拿回环当首选，直接拒绝（「是回环接口，不能作为上网网卡」）。
  快照里仍记录全部网卡的原值，但恢复时值没变就不会被写。
- **提权沿用「自身 exe 子命令 + 结果文件」**（`AutoLoginNet.App\ElevatedCommands.cs` 新增
  `--adapter-priority apply|restore`、`--adapter-name`、`--adapter-priority-result`）：
  未提权时以管理员身份重启自己、把 `AdapterPriorityOperationResult` 写进
  `adapter-priority-result-<32hex>.json` 回传；已提权则直调服务。
- **界面**（`AutoLoginNet.App\Pages\NetworkPage.xaml` 新增「系统首选网卡」卡片）：
  「设为系统首选网卡」「恢复系统自动跃点」两个按钮 + 状态文本，都会先弹确认框
  （讲清改跃点、改变系统选路、不禁用网卡、需要 UAC、可以恢复）；恢复按钮只在存在快照时可用。
- **验证**：单元测试 **243/243** 通过（新增 17 个计划器用例、24 个服务用例含 netsh 假替身、
  2 个回环用例）；真机读回 9 个接口的跃点表与 netsh 完全一致；非提权跑一次
  `set` 被系统拒绝 → 服务完整回滚、不残留快照，行为符合预期。

## 2026-10-17 — 版本号改为 2.0.9.1.dev

**文件名能看出这是开发版，「应用和功能」里仍得是纯数字——版本号从此分成两张面孔。**

- **版本号拆成「数字 + 标签」**（`AutoLoginNet.App\AutoLoginNet.App.csproj`）：`Version` / `AssemblyVersion` / `FileVersion`
  统一为 `2.0.9.1`，另设 `InformationalVersion` 为 `2.0.9.1.dev`，并关掉
  `IncludeSourceRevisionInInformationalVersion`（否则源版本号会被追加成 `2.0.9.1.dev+<sha>`）。
  两条硬约束逼出这个拆法：Windows Installer 的 `ProductVersion` 只接受 1~4 段纯数字，
  .NET 的 `Version` 也不接受「4 段 + 文本标签」（`-p:Version=2.0.9.1.dev` 在还原阶段就会失败）。
- **新增 `AutoLoginNet.App\Infrastructure\AppVersion.cs`**：从 `AssemblyInformationalVersionAttribute`
  读完整标签（缺失时回退三段版本、再兜底「未知」）。启动日志与设置页都用它，
  于是界面显示 `2.0.9.1.dev`，而安装包登记版本仍是 `2.0.9.1`。
- **打包脚本新增 `-VersionLabel`**（`Setup\Export-Installer.ps1`、`Setup\Build-Installer.ps1`）：
  不传时从 csproj 的 `Version` + `InformationalVersion` 解析，并校验「版本必须是 1~4 段数字、
  标签必须以版本开头」，不合规直接抛中文错误。MSI/ZIP 文件名、裁剪报告头、publish 的
  `-p:InformationalVersion` 用完整标签，MSI 里的 `{{VERSION}}` 继续用纯数字版本。
- **修复框架依赖包的一个启动崩溃**：`Setup\PublishPruneRules.ps1` 原先把
  `Microsoft.Windows.AppLifecycle.Projection.dll` 当成「没人引用」删掉，而单实例守卫用
  `AppInstance.FindOrRegisterForKey` 需要它；框架依赖包删掉后启动即 `0xC000027B`
  （combase 报 0x80070002 找不到文件），全内置包因为投影由包内 `.winmd` 提供而幸免。
  该文件已移入 `$MustKeep`。
- **验证脚本改为隔离数据目录**：`Setup\Verify-PrunedPublish.ps1` 通过新增的
  `AUTOLOGIN_DATA_DIR` 环境变量（见 `AutoLoginNet.Core\AppPaths.cs`）把应用指向临时目录，
  并塞一份 24 条、多分类的种子日志。既验证了「日志页真的从磁盘读文件」，
  也不再污染真实 `%AppData%`；顺带绕开一个环境陷阱：真实日志积累到上千条时，
  日志页元素数量会让 UIA 的一次 `FindAll` 超时，验证假失败（本次排查中真实日志已有 818 条）。
- **验证**：两形态各出一条命令跑通「发布 → 裁剪 → 实机验证 → MSI/ZIP」——
  全内置（发布 498 文件 / 159.6 MB，裁剪后 288 文件 / 151.6 MB，MSI 46.70 MB，ZIP 59.79 MB）、
  框架依赖（裁剪后 26 文件 / 34.7 MB，MSI 7.45 MB，ZIP 9.35 MB）；
  两个 MSI 的 `ProductVersion` 均为 `2.0.9.1`（ARP 只显示数字），文件名与 ZIP 名带 `2.0.9.1.dev`；
  单元测试 200/200 通过。

## 2026-10-17 — 自启常驻托盘、关闭行为可选、上网网卡可切换、注销修复

**四件事一起改：开机自启不再「闪一下就走」、关闭按钮可以选「收进托盘」、认证与 DNS 用哪张网卡可手动指定、自启登录后注销按钮不再点不动。**

- **开机自启 / 最小化（替代原「登录成功后最小化」）**：删掉 `UiSettings.MinimizeAfterLogin`、`SettingsViewModel.MinimizeAfterLogin`、
  设置页对应复选框和首页登录成功后的 `MinimizeIfRequested()`——这个开关语义一直别扭（它管的是登录成功、而不是开机自启）。
  换成两项更明确的设置：`UiSettings.StartMinimizedToTray`（开关在首页「开机自启」卡片里，勾选后自启只在托盘中完成登录）
  与 `UiSettings.CloseToTray`（设置页「外观」卡片）。
- **`--autostart` 从「静默登录后退出」改为「静默登录后常驻」**：`App.RunAutoStartWithInterface` 完成静默登录后
  `MarkStartupLoginCompleted(exitCode == 0)`，再按 `StartMinimizedToTray` 用 `new MainWindow(enableTray: true, startHidden)`
  常驻；不再 `Environment.Exit`。这样托盘入口始终可用，用户随时能注销或切换上网网卡，不必重启程序重新检测。
  `AppServices.StartupLoginSucceeded` + `DashboardViewModel.ApplyStartupLoginOutcome` 把自启结果回填成「已自动登录」，
  同时 `TryClaimStartupAutoLogin()` 拦掉界面上的第二次登录。
- **关闭主窗口行为可选（`UiSettings.CloseToTray`，默认 false）**：`MainWindow` 订阅 `AppWindow.Closing`，
  勾选时 `args.Cancel = true` 后走 `HideToTray()`；`ExitFromTray` 先置 `_exiting` 避免被自己拦住。
  托盘图标没加成功（`IsAvailable == false`）时照常退出——否则关闭按钮会把窗口藏起来又点不回来。
- **上网网卡手动指定**：`INetworkAdapterResolver` 增加同步的 `ListAdapters()`（枚举内存数据，与路由表无关），
  `WindowsNetworkAdapterResolver` 抽出 `Describe(NetworkInterface)` 复用给 `ResolveByGateway`；
  新增 `AutoLoginNet.App\Infrastructure\AdapterSwitcher.cs` 统一「列网卡 / 存偏好 / 按名解析（找不到回退自动并记日志）」。
  `UiSettings.NetworkAdapterName` 存**网卡名**而不是索引（索引会随插拔/重启变化）。界面：网络工具页 DNS 卡新增「上网网卡」下拉框，
  托盘右键菜单新增「上网网卡 ▸」子菜单（`MF_POPUP` + `CheckMenuRadioItem` 单选勾选，第一项「自动选择」）。
  `--apply-dns` 支持 `--adapter-name`：提权子进程按名字解析目标网卡，保证改的就是界面上选中的那张，而不是重新按路由猜一张。
- **修复「自启登录后打开界面无法注销」**：`ConnectivityViewModel.MarkOnline()` 原先在没有快照时直接 return（自启静默登录不经过界面检测，
  快照一直是 null），导致 `IsOnline` 恒为 false、注销按钮始终禁用。现在无快照时合成一份在线快照；
  `DashboardViewModel.CanLogout` 放宽为 `!IsBusy && (IsOnline || !HasResult)`——状态未知也允许尝试，
  服务器对未在线会话只会返回 NotOnline，无副作用。
- 验证：App / Tests 工程编译 0 警告 0 错误；MSTest（EnableMSTestRunner 方式跑）200/200 通过；
  实机验证了自启隐藏进托盘、托盘左键/双击还原、`WM_CLOSE` 收进托盘、托盘菜单「上网网卡 ▸」、网卡下拉列出
  「以太网 / WLAN / vEthernet」并把 `networkAdapterName` 落盘、仪表盘显示「已自动登录」且注销按钮可用。

---

## 2026-10-17 — 最小化收进系统托盘

**主窗口「最小化」不再缩进任务栏，而是收进系统托盘。** 这是常驻类工具最省心的用法：登录完成后窗口不必占着
任务栏也不占用屏幕，需要看日志或改设置时从托盘图标点回来即可。

- **新增 `AutoLoginNet.App\Infrastructure\TrayIcon.cs`（P/Invoke 自研，不引入 WinForms / 第三方托盘库）**：
  自建一个隐藏的顶层原生窗口接收回调消息（`WM_TRAYICON = WM_APP + 1`），用 `Shell_NotifyIconW` 增删改图标；
  左键单击 / 双击 → 显示主窗口，右键 → `TrackPopupMenuEx` 弹出「显示主窗口」/「退出程序」（选单前 `SetForegroundWindow`、
  选单后补一条 `WM_NULL`，否则菜单会卡住不消失）；图标从 exe 内嵌的 `Assets\app.ico` 取（`ExtractIconExW`），失败才回退系统默认图标。
  不用 WinForms 的 `NotifyIcon` 是因为它会把整个 WinForms 拖进交付包，且其消息泵与 XAML 组件的消息循环容易互相干扰。
- **`AutoLoginNet.App\MainWindow.xaml.cs`**：`SetWindowSubclass` 拦截 `WM_SYSCOMMAND`/`SC_MINIMIZE`（覆盖标题栏按钮、
  Alt+Space 菜单、Win+↓），改为 `AppWindow.Hide()`——不用 `WindowState.Minimized`，那样会留在任务栏；
  子类化没生效时由 `AppWindow.Changed` 感知到最小化后兜底隐藏。**托盘图标没加成功时自动退回普通最小化**，
  避免出现「窗口藏起来又点不回来」。关闭主窗口仍是退出程序（`Closed` 事件里移除托盘图标）。
- **开机自启路径（`--autostart` 静默登录）用 `new MainWindow(enableTray: false)`**：那条路径窗口只闪一下，
  留个托盘图标反而是死图标。（已被同日后续条目取代：自启现在登录后常驻，按「开机自启时最小化到系统托盘」决定只留托盘还是显示窗口。）
- 设置页「登录成功后最小化窗口」文案改为「登录成功后最小化到系统托盘」，`UiSettings.MinimizeAfterLogin` 注释同步。
- 已知环境限制：如果 exe 被打上低完整性标签（某些沙箱目录会继承 Low IL），进程无法与资源管理器通信，
  `Shell_NotifyIconW` 会返回失败且 `GetLastWin32Error()` 为 0——此时按上面的规则退化为普通最小化，功能不受影响。

---

## 2026-10-15 — 交付形态收敛为两种（全内置 / 框架依赖）

**「自包含 .NET」版（31.3 MB：只带 .NET、但仍要求目标机装 Windows App Runtime 1.8）停止交付。**
它省下的 16 MB 换来的是「目标机没装 Windows App Runtime 就闪退」，而全内置版已经能一次拷到任何
Windows 10 1809+ 机器上跑；留着只在一个中间场景才用得上的包，只会让「这次该发哪个」成为每次都要解释的问题。

- `Setup\Export-Installer.ps1` / `Setup\Build-Installer.ps1`：**不加形态开关即全内置**（`-was-fd`），
  `-FrameworkDependent` 仍是 `-fd`。`-SelfContained` 开关保留，但传了直接报错——给出明确提示，
  而不是悄悄按全内置导出一个名字和内容都容易搞混的包。
- 产物命名随之固定为 `AutoLoginNet-<版本>-was-fd-x64.msi` 与 `AutoLoginNet-<版本>-fd-x64.msi`
  （及对应的 `-portable-*.zip`、`prune-report-<版本>-*.txt`）。
- `Setup\AutoLoginNet.Setup.wixproj`：`SetupFlavor` 默认值 `SelfContained` → `AllBundled`；
  `-p:SetupFlavor=SelfContained` 会把 `-SelfContained` 传给脚本，由脚本报错。
- 载荷形态自检（`Assert-PayloadFlavor`）改为两种：全内置要求 `coreclr.dll` 与 `Microsoft.ui.xaml.dll`
  都在，框架依赖要求两者都不在。
- README 的下载表、体积表与打包示例同步收敛为两行。

---

## v2.0.8 — 2026-10-13

> 相比 2.0.7 的变化：**同一版本提供三种载荷**（全内置 46.7 MB / 自包含 31.3 MB / 框架依赖 7.4 MB）、
> **安装向导与许可协议中文化**（协议正文换成新的第三方免责声明，含 MIT 开源条款）、
> 界面问题修复与一键导出脚本。
> 升级安装沿用 2.0.7 的做法：自动卸旧版并装回旧版所在目录。

### 安装包：三种交付形态（全内置 / 自包含 / 框架依赖）

> 注：下表是 v2.0.8 发布时的形态。其中「自包含 .NET」一行已于 2026-10-15 停止交付（见文件开头条目），
> 现在只出「全内置」与「框架依赖」两种。

打包脚本支持三种载荷，文件名用 `-was-fd` / `-fd` 后缀区分，可同时躺在 `dist\` 里，
共用同一 `UpgradeCode`（属同一产品的三种分发方式，不能同时安装）：

| 形态 | 发布目录 | 裁剪后 | MSI | ZIP | 目标机需预装 |
| --- | --- | --- | --- | --- | --- |
| **全内置（`-was-fd`）** | 159.6 MB / 498 个文件 | 151.5 MB / 287 个文件 | **46.7 MB** | **59.8 MB** | 无 |
| 自包含 .NET（默认） | 102.5 MB / 229 个文件 | 99.6 MB / 203 个文件 | **31.3 MB** | **39.5 MB** | Windows App Runtime 1.8 |
| 框架依赖（`-fd`） | 36.8 MB / 51 个文件 | 34.6 MB / 25 个文件 | **7.4 MB** | **9.3 MB** | 上面的 + .NET 10 运行时 |

- **`Setup\Export-Installer.ps1` / `Build-Installer.ps1` 新增 `-WindowsAppSDKSelfContained`**：
  把 Windows App Runtime 的原生层（`Microsoft.ui.xaml.dll` 14.4 MB 等，约 52 MB）也打进包，
  目标机零运行时依赖。
- **关键陷阱**：`-p:WindowsAppSDKSelfContained=true` **必须与 `--self-contained true` 同时传**。
  只传前者时，命令行/`csproj` 里的 `<SelfContained>false</SelfContained>` 会让它退化成
  「有 Windows App Runtime、没有 .NET」的中间形态（发布目录 93.88 MB），装到干净机器上照样闪退。
- 全内置版实测零依赖：复制到含中文与 `#` 的路径后启动，宿主日志为
  `Using app-local location ...` → `Executing as a self-contained app as per config file`，
  只读应用目录里的 `coreclr.dll`，界面正常、认证成功。
- **`Build-Installer.ps1` 增加形态自检**：`-SkipPublish` 复用已有发布目录时，用
  `coreclr.dll` / `Microsoft.ui.xaml.dll` 是否存在来比对形态，不一致就报错——
  避免把上一次导出的形态套上本次的文件名（例如把中间形态印成「全内置」）。
- 三种形态都**内含** `AutoLoginNet.pri` 与 `App.xbf`；`-fd` 版裁剪后仍有 24.9 MB 是
  `Microsoft.Windows.SDK.NET.dll`（WinRT 托管投影，再省无可省）。

### 打包：接入 Visual Studio / MSBuild（生成即出包）

- **新增 SDK 式 WiX 工程 `Setup\AutoLoginNet.Setup.wixproj`，并加入 `Auto_Login_Net_JXUST.sln`**：
  在 VS 里点「生成解决方案」，或命令行 `dotnet build Setup\AutoLoginNet.Setup.wixproj -c Release`，
  就能直接产出 MSI 与免安装 ZIP。它**不重写打包规则**，只是把命令行那几步挂到 MSBuild 上：
  生成前 `Export-Installer.ps1 -PrepareOnly`（编译原生自定义动作 → `dotnet publish` → 裁剪 →
  生成 `Setup\obj\Product.wxs` / `Install.wxs`）→ WiX SDK 编译 → 生成后 `Build-Installer.ps1 -FinalizeMsi`
  （形态自检 → 中文化 13 项断言 → 落 `dist\` 规范命名 → 免安装 ZIP）。**不需要任何 WiX 的 VS 扩展**
  （靠 NuGet 还原 `WixToolset.Sdk` / `WixToolset.UI.wixext` 即可）；反过来说，装成 WiX **v3** 的
  Votive 扩展对本项目没有用——它按 v3 语法提供 IntelliSense，编译不了 v4/v5 语法的
  `Setup\Product.wxs.template`。
- 交付形态用属性切换：`-p:SetupFlavor=SelfContained`（默认，约 31 MB）/ `FrameworkDependent`（`-fd`，约 7 MB）/
  `AllBundled`（`-was-fd`，约 47 MB）；`-p:SetupSkipPublish=true` 复用已有载荷。
  形态一变会强制重新准备载荷，且 `-FinalizeMsi` 会比对载荷是否真与该形态相符，不符直接报错。
- **`Build-NativeCustomActions.ps1` 修掉一个只在「被 MSBuild / IDE 拉起」时才暴露的失败**：
  调用方进程的 `PATH` 里混进了不可用条目（本机实测 6 个：含 `"`、`%` 的畸形项与已不存在的目录），
  `vcvars64.bat` 在这样的 `PATH` 下会直接报 `此时不应有 \common。`，`cl.exe` 随之以 255 退出。
  现在先把 `PATH` 过滤成「存在、且不含 `"` 与 `%` 的目录」，再写一个临时 `.cmd` 依次执行
  `call vcvars64.bat` 与 `cl.exe`（避免外面再套一层引号），最后统一检查退出码；
  不可用条目数量被剔除的项都会打印出来。
- **原生自定义动作改为编译到暂存目录再复制出来**：以前直接往 `bin\AutoLoginNet.SetupActions.Native.dll`
  上覆盖，上一次的原始输出（`*.lib` / `*.exp` / `*.obj`）与 `selftest` 中间产物会残留在 `bin\` 里；
  现在编译进 `bin\staging\`，成功后才复制成正式产物并清掉暂存目录。
- **`.wixproj` 记住本次形态**：换 `SetupFlavor` 会在 `Setup\obj\setup-flavor.txt` 留戳并强制重跑准备步骤，
  避免「增量跳过发布」把上一次形态的载荷配上这一次的文件名。实测：无改动时重复生成 **2.08 秒**
  （跳过发布/裁剪/编译），换形态则自动重新发布。
- **载荷固定按 Release 编译**（`SetupPayloadConfiguration`，默认 `Release`）：解决方案当前配置只决定
  程序本体的编译配置，安装包内容永远是 Release 载荷。这条是踩出来的——在 VS 里用 Debug 生成时，原本
  会 `dotnet publish -c Debug` 把 Debug 载荷灌进 `dist\AutoLoginNet`，再用它生成 MSI，并盖上与之前
  Release 那一对同名的 `dist\AutoLoginNet-2.0.8-x64.msi` / `-portable-x64.zip`，于是 dist 里留下两份
  互不相配的产物。要故意出 Debug 载荷的包就传 `-p:SetupPayloadConfiguration=Debug`。
- **三个新脚本开关**（给 MSBuild 工程复用，手跑一般用不到）：`Export-Installer.ps1 -PrepareOnly`、
  `Build-Installer.ps1 -GenerateOnly -SkipPublish`、`Build-Installer.ps1 -FinalizeMsi <MSI 路径>`。
  命令行导出流程本身没变：`Export-Installer.ps1` 仍然默认跑「裁剪后启动验证」；
  MSBuild 那条路默认跳过它（`-p:SetupSkipVerify=false` 可打开），因为它会真的启动应用做 UI 探测，
  IDE 每次生成都跑太重。
- 前提是机器上有 **PowerShell 7**（脚本是无 BOM 的 UTF-8 且用了 PS7 语法，**不能用 Windows PowerShell 5.1**）；
  没有 `pwsh` 时 `.wixproj` 会先探测再给出明确报错，也可以用 `-p:PowerShellHost=<pwsh.exe 路径>` 指定。
- 实测（Release / x64）：单独生成 `.wixproj` 55.1 秒，生成整个解决方案 47.0 秒；
  默认自包含形态产出 `dist\AutoLoginNet-2.0.8-x64.msi`（32,832,588 B）与
  `dist\AutoLoginNet-2.0.8-portable-x64.zip`（41,404,899 B），13 项中文化断言全过。

### 协议正文换成新版免责声明

- **协议来源改为仓库根目录的 `免责声明.txt`**（64 行，用户提供的最新全文），同步一份到
  `Setup\LicenseText.txt` 供打包脚本使用；相比旧正文多了「开源许可与 MIT 协议」一节，
  并补上了作者、本地加密存储、修改版分发义务等条款。
- **`Setup\Test-InstallerLocalization.ps1` 的「协议正文逐行包含」断言此前形同虚设**：
  它拿裸文本去 RTF 里找，而正文在 MSI 的 `Control` 表里形如 `\ql\f1\fs20 \u26412?...`
  （带段落样式前缀 + Unicode 转义），永远匹配不到。现已改为连样式前缀一起探测；
  `Setup\New-LicenseRtf.ps1` 也加了同规则的自检，任何一行没进 RTF 就直接抛错。
  修正后：**45 行正文逐行命中，缺失 0 行**，RTF 长度 18343。

### 体积：改为框架依赖发布（安装包缩小到约 1/6）

自包含发布把 .NET 运行时与 Windows App Runtime 一起打进了包，这是体积的大头：
解包 151.58 MB 里，真正属于程序自己的只有约 **2.2 MB**，其余是运行时（.NET 约 63 MB）、
WinUI 原生组件（约 50 MB）和 WinRT 投影（约 33 MB）。

- **`AutoLoginNet.App.csproj`**：`SelfContained` 与 `WindowsAppSDKSelfContained` 都改为 `false`，
  并写明代价（目标机必须预装两个运行时，Windows App Runtime 版本要与 SDK 引用对齐）。
- **实测对比**：解包 151.58 MB / 291 个文件 → **34.62 MB / 25 个文件**；
  MSI 46.8 MB → **7.4 MB**；免安装 ZIP 59.8 MB → **9.3 MB**。
  减少的正是随包分发的运行时本体。裁剪后体积的最大单项变成 `Microsoft.Windows.SDK.NET.dll`
  （24.9 MB，WinRT 托管投影），框架依赖下已无法再省——运行时只提供 Windows App Runtime 本身。
  （后来用户要求「连 Windows App Runtime 也内置」，于是有了本文档开头的**全内置**形态；
  这里说的「默认自包含」指只含 .NET、仍需系统 Windows App Runtime 的那一版，现在约 31.3 MB / 99.6 MB 解包。）
- **代价是要先装运行时**，否则程序启动即闪退（`0xC000027B` / “找不到指定的模块”，
  没有任何界面提示）：
  - Windows App Runtime **1.8**（对应 `Microsoft.WindowsAppSDK 1.8`）
  - .NET 桌面运行时 **10.x**
  - 新增 `Setup\Install-Runtime.ps1`：先检测再安装，缺哪个装哪个、两个都装好时不做任何改动，
    需要时自动提权；`-WhatIf` 只打印计划。
  - `Setup\Runtime\README.md` 说明安装器获取方式（两个安装器共约 160 MB，**不入库**）。
- **`Setup\Export-Installer.ps1` 新增 `-BundleRuntime`**：把 `Setup\Runtime\` 下的两个安装器
  一并复制到 `dist\Runtime\`，产出「离线也能装」的交付物；目录里没有安装器时给出明确警告，
  而不是悄悄产出一个装完打不开的包。
- `Setup\PublishPruneRules.ps1` 的裁剪规则保留，但框架依赖下命中的条目会少很多——
  大部分被裁掉的文件本来就属于运行时。

### 安装程序中文化

- **安装向导全中文**：`Setup\WixUI_zh-CN.wxl` 覆盖 WiX 内置的全部对话框文案
  （欢迎、许可协议、目标文件夹、就绪、进度、完成、错误提示与各按钮），
  `Product.wxs` 的 `Language` 早已是 `2052`，但真正让界面变中文的是打包时显式传入
  `-loc`；`Setup\Build-Installer.ps1` 已固定带上它。**`.wxl` 不会被自动发现**——
  把 `.wxl` 放在 `.wxs` 同目录里 WiX 连看都不看（同目录放一个故意写坏的 `.wxl`，
  编译照样成功），只有 `-loc` 才生效，所以这一步不能省。
- **许可协议页换成第三方非官方版免责声明**：正文来源是仓库根目录的 `免责声明.txt`
  （用户提供的全文，逐字保存；包内使用同步副本 `Setup\LicenseText.txt`），
  由 `Setup\New-LicenseRtf.ps1` 生成 `Setup\license.rtf`，
  再经 `WixUILicenseRtf` 属性注入 `LicenseAgreementDlg` 的正文框——不自己改对话框，
  也不把几千个 RTF 字符塞进 `.wxl` 字符串里。`license.rtf` 属于生成物，需要时重新生成即可。
- **中文化现在是打包的硬断言**：新增 `Setup\Test-InstallerLocalization.ps1`，
  `Build-Installer.ps1` 在编译完 MSI 后直接读 MSI 的表校验 13 项
  （10 个关键界面串、协议正文控件非空、**协议正文逐行包含** `LicenseText.txt`、
  `ProductLanguage=2052`），任何一项不过就中止打包并列出失败项。
  之前只靠「编译成功」判断，很容易在忘记 `-loc` 时悄悄退回英文安装界面。

### 修复

| 问题 | 根因 | 修复 |
| --- | --- | --- |
| 从别的页面返回首页后，已填好的表单内容全丢 | `MainWindow` 每次导航都新建页面实例（`Content = new DashboardPage()`）；WinUI 的 `NavigationCacheMode` 默认 `Disabled` | 改为按页面类型缓存实例并置 `NavigationCacheMode="Required"`；同时给「加载」加幂等保护，缓存命中时不再重复拉取网络状态、不再清空用户已填内容 |
| 改 hosts 只能手动编辑 | 没有一键写入入口 | 网络工具页新增 hosts 区块：读现状、列出待写入的校园站点、一键写入（自动提权）、写入后回读比对 |
| 点「应用推荐 DNS / 修改 hosts」后没有任何反馈 | 提权子进程执行完把结果丢了，主进程无从判断 | 提权子进程把结果写入约定文件，主进程等待其退出后读取；**只有回读确认真实生效才报告成功**，失败会带出具体原因 |
| **hosts 一键注入永远提示「没有修改 hosts 文件的权限，需要管理员授权」，即使已通过 UAC 提权** | 写入用的是「先写 `drivers\etc\hosts.aln.tmp`，再 `Move(..., overwrite: true)` 同目录改名替换」。**安全软件（实测火绒）的 HIPS 驱动会拦截对 hosts 的同目录改名并返回 `Access denied`**；而同一个临时文件写到 `%TEMP%` 再覆盖 `hosts`、以及直接原地写入，都被放行。也就是说提权本身没问题，日志里「应用启动」与失败记录成对出现就是提权子进程自己在写、自己失败 | 改成**原地截断写入**：备份 → 用 `FileStream(FileMode.Create)` 覆盖 `hosts` → **读回逐字节校验**，不一致立刻用写入前内容回滚；写入成功后顺手清理旧版本遗留的 `hosts.aln.tmp`。失败文案也不再笼统说「没权限」，而是带出失败在哪一步、以及是否是安全软件拦截 |
| 运行日志页只显示登录与凭据两类，与日志文件不符 | 界面读的是内存环形缓冲；`FileLogService` 从磁盘读全文并解析后合并进列表 | 日志页合并「磁盘历史 + 内存最近」两路来源，并在过滤后显示「显示 N / 共 M 条」，避免误判为丢日志 |
| 输入框弹出的下拉列表是白色不透明的 | WinUI 默认 `ComboBoxDropDownBackgroundBrush` 是半透明亚克力，叠在 Mica 背景上会透出后面内容 | 在 `AppStyles.xaml` 的 `ThemeDictionaries` 里把该画刷连同 PointerOver / Pressed 三个状态全部换成实心主题色（三个都要给，否则悬停时会短暂变回半透明） |
| 启动时先白屏一段时间，窗口也不出现在最前 | 主窗口 `Activate()` 之前先做了服务初始化与静默登录；且 `Activate()` 不保证抢到前台 | 静默登录改由 `--autostart` 分支执行，GUI 启动路径不再阻塞；`Activate()` 之后调用 `SetForegroundWindow` 前置窗口 |
| 1.x 的数据与桌面快捷方式在新版上失效 | 数据目录、凭据文件名、启动参数都变了 | 首次运行时迁移：旧凭据文件转换到新格式，旧版 `--silent` 参数作为 `--autostart` 的别名继续可用 |
| 历史遗留兼容代码无人清理 | 换架构时留下的死分支 | 删除旧 WPF / 控制台路径与已无引用的兼容分支，详见下方「打包」小节 |

### 打包

- **新增一键导出脚本 `Setup\Export-Installer.ps1`**：检查并（必要时）编译原生自定义动作 →
  `dotnet publish` → 裁剪 → 运行可用性验证 → 生成 MSI 与免安装 ZIP → 打印大小与 SHA256，
  可选 `-Desktop` 直接复制到桌面；`-SkipPrune` 用于出问题时对照排查。
- **裁剪规则改为实测驱动**：`Setup\PublishPruneRules.ps1` 里的每一条删除规则，都先用
  「复制一份完整发布目录 → 只删这一个文件 → 启动并等主窗口出现」逐文件验证过。
  44 个候选里 43 个可安全删除，**唯一不能删的是 `Microsoft.UI.Xaml.Phone.dll`**——
  静态看没有任何程序集引用它，删掉后应用却在启动阶段以 `0xC000027B` 崩溃在
  `Microsoft.UI.Xaml.dll`（`combase.dll` 报 `0x8007007E` 找不到模块）。
  该文件已列入 `$MustKeep` 并写明原因，避免以后有人「优化」掉它。
- **裁剪量**：解包 159.59 MB → **151.58 MB**（498 → 291 个文件，移除 125 个文件 / 83 个目录 / 8.01 MB），
  MSI 48.5 MB → **46.8 MB**，免安装包 63.5 MB → **59.8 MB**。
  移除的是 82 个非中文语言资源目录（3.26 MB）与 43 个实测无用的文件（4.75 MB）。
  每次导出都会在 `dist\prune-report-<版本>.txt` 留下「删了什么、为什么、保留了什么」的清单。
- **程序只保留简体中文，但 `en-us` 目录必须留**：发布目录里的语言资源目录现在只剩
  `zh-CN` 与 `en-us` 两个（原先还有 `zh-TW`、`en-GB`）。`en-us` 虽然一个中文字都没有，
  却是 WinUI 自己按目录名加载资源用的，**删掉后应用启动即崩（`0xC000027B`，找不到模块）**，
  已经实测确认，别再当无用目录清理掉。
- **不为了体积做危险优化**：`*.winmd` 共 45 个 / 2.57 MB 默认保留
  （`Microsoft.WindowsAppRuntime.dll` 内有 `RoGetMetaDataFile`，可能按命名空间查找元数据），
  不省这点空间去冒「打不开」的风险。
- `Setup\Verify-PrunedPublish.ps1` 修好了一个自身缺陷：辅助函数会把 `$true/$false`
  混进输出流，导致调用方拿到的不是结果对象而是对象数组（读 `.Checks` 直接报
  「找不到属性」），裁剪后验证会因此**静默失效**。现在验证内容包括：进程存活、主窗口出现、
  四个页面可切换且页面含预期文案、日志页真的从磁盘读到多类记录、可触发网络检测、全程无崩溃、
  事件日志无新增应用错误。

---

## 2026-10-13 — 安装包：就地升级覆盖、原生自定义动作与体积精简

### 修复

- **装新版会另起一个目录，旧目录里的东西留在原地**：以前无论旧版装在哪里，升级都装到
  `C:\Program Files\AutoLoginNet`（默认目录），旧目录不会被覆盖，用户看到的是「两份安装」。
  例如本机旧版装在自定义的 `D:\autologin`，升级后新文件却跑到了 Program Files。
  现在安装时会先定位旧版实际所在目录，再把它作为本次安装目录，做到原地覆盖。

  实现要点（结论都来自本机实测，不是推断）：

  - **不能靠读注册表里的 `InstallLocation`**。本产品历史版本的安装包从未声明
    `ARPINSTALLLOCATION`，因此 ARP 里该值一直是空的 —— 本机 429 个已装产品键中
    **没有一个**带 `InstallLocation`。早期设想的 `AppSearch`/`RegistrySearch` 方案
    在这里永远搜不到东西。
  - **目录名也不能用来猜**。`2.0.7` 的 MSI 里记录的目录名是 `AutoLoginNet`，
    而用户实际装在 `D:\autologin`；`1.0.0` 记录的是 `AutoLoginNet App`。
    按名字匹配必然落空。
  - **改为逐产品询问组件路径**：枚举本机已装产品，读它缓存 MSI 的 `File` 表确认
    确实包含本产品主程序，再用 `MsiGetComponentPath` 取到该产品**真实的**安装路径。
    这条路径不受用户改目录、目录改名、注册表缺值的影响。
  - **必须跨代防串台**：本机同时装着 `1.0.0`（`C:\Program Files\AutoLoginNet App`，
    主程序 `AutoLoginNet.App.exe`）与 `2.0.7`（`D:\autologin`，主程序 `AutoLoginNet.exe`）。
    两条产品线的 `UpgradeCode` **不同**（`1.0.0` 是 `{6F5D0F1B-…}`，`2.x` 是 `{80D113DF-…}`），
    所以从 `1.0.0` 升级时 `WIX_UPGRADE_DETECTED` 里根本没有同族产品。
    探测逻辑因此按「主程序文件名」区分代次：`2.x` 的包只认 `2.x` 的目录，
    绝不会把新版装进 `1.x` 的目录（反之亦然）；只有确实检测到旧版时才启用全量枚举兜底，
    全新安装不会去占用机器上另一条产品线的目录。
  - **顺带修好「同一版本号重装」**：`MajorUpgrade` 默认的版本范围上界是**独占**的，
    所以「已装 2.0.7、又装 2.0.7」不被视为升级，会并排装上第二份。
    现启用 `AllowSameVersionUpgrades="yes"`，开发期反复用同一版本号自测也能原地覆盖。
  - **从此开始记录自己的安装目录**：新增 `ARPINSTALLLOCATION`（由 `CostFinalize`
    之后的一条 type 51 动作写入，直接写 `<Property>` 会存进字面量 `[INSTALLFOLDER]`）。
    历史版本缺失这个值正是上面第一个问题的根因，补上之后未来版本有据可查。

  自定义动作用**原生 C++ DLL** 而非托管动作：WiX 4/5 的 SDK 已不再分发 DTF 的
  `WindowsInstaller` 互操作程序集，托管自定义动作在打包环境里无法编译；
  原生 DLL 只依赖 `msi.dll`。

### 打包：原生自定义动作

- 新增 `Setup\CustomActions.Native\UpgradeLocation.cpp` 与 `Setup\Build-NativeCustomActions.ps1`，
  MSI 打包流程会自动编译并嵌入该动作。
- 新增 `Setup\Test-NativeDetection.ps1`：在普通权限下直接加载**编译产物**跑自检，
  验证的就是即将交付的那份代码（早期夹具另写了一份等价逻辑，结果夹具全绿而功能从未生效）。

### 打包：体积再精简

- **安装包体积精简约 9%**：MSI 52.8 MB → **48.3 MB**，免安装包 69.6 MB → **63.5 MB**
  （解包后 174.3 MB → 159.6 MB，498 个文件）。移除的是发布包里永远不会被加载的文件：
  调试符号与调试基础设施（`*.pdb`、`mscordaccore*`、`mscordbi`、`DiaSymReader`）、
  未被引用的框架程序集（`System.Private.DataContractSerialization`、`System.Data.Common`、
  `System.Data.DataSetExtensions`、`System.Formats.Tar`、`System.Xml.XPath.XDocument`）、
  `Microsoft.Windows.Widgets.dll` 与 `Microsoft.UI.Xaml.Controls.pri`。
- **刻意不做 IL 裁剪**：试过 `PublishTrimmed=true` + `TrimMode=partial`（解包可降到 76 MB），
  程序能启动、网络检测也正常，但**切到任意带 `x:Bind` 的页面即崩溃**——
  裁剪把 `Microsoft.Windows.SDK.NET.dll` 从 23.7 MB 砍到 0.11 MB，而 WinUI/CsWinRT 是
  **按名字反射**解析 WinRT 投影类型的（`WinRT.TypeExtensions.GetAbiToProjectionVftblPtr`
  返回 null）。补 `TrimmerRootDescriptor` 显式保留投影程序集后仍然崩溃。
  **一个 76 MB 但会崩的包，不如一个 160 MB 能用的包**，因此这里只做文件级删除，不碰 IL。
- **`WinUIEdit.dll` 必须保留**（3.3 MB）：实测删掉后程序无法启动，是 WinUI 输入栈的硬依赖。

### 修复（打包流程）

- **安装包版本落后于应用**：应用已到 v2.0.7，而 `Build-Installer.ps1` 里另写着默认版本 `2.0.5`，
  导致打出来的安装包仍是没有自动登录、没有重试、跳过检测不被记忆的旧版本。
  包内 `AutoLoginNet.exe` / `.dll` 的文件版本会如实显示 `2.0.5.0`。
  现已改为**只认 csproj 里的 `<Version>`**（未显式传 `-Version` 时自动读取），消除这处重复声明。

## v2.0.7 — 自动登录、记忆跳过检测、开机自启重试

### 新增

- **启动时自动登录**：首页新增开关，勾选后打开程序即用已保存的凭据登录，无需手动点「立即登录」。
  用一次性标记保证每次运行只触发一次——否则切回首页会反复登录，用户手动注销后也会被立刻登回去。
- **登录失败自动重试**：对「瞬时故障」（请求超时、连不上认证服务器）按配置重试。
  手动登录与开机自启共用同一套 `LoginRetryCount` / `LoginRetryDelaySeconds` 策略；
  密码错误、欠费停机等**非瞬时**故障不重试，避免白等。

### 修复

- **「跳过网络检测」现在会被记住**：此前只存在于内存，每次启动都要重新勾选。
- **`LoginRetryCount` / `LoginRetryDelaySeconds` 是死配置**：只在 `AppOptions` 里定义与收敛，
  从未被任何代码读取（README 却宣称可用）。现已真正接入登录流程。
- **开机自启只尝试一次就放弃**：日志实测
  `09:38:19 [自启] 自动登录结束：NetworkError（耗时 51 ms）`——开机瞬间认证服务器尚未就绪，
  程序失败即退出，用户开机后仍处于未认证状态且没有任何提示。现已改为带重试。

### 重构

- 把静默登录流程从 `StartupRouter`（App 层）抽到 Core 层的 `SilentLoginRunner`：
  这段代码只在开机那一刻跑一次，出问题极难复现，因此必须能被单元测试覆盖。
  延迟函数可注入，测试无需真的等待。
- 新增 **10 个** `SilentLoginRunner` 用例：无凭据 / 已在线短路 / 首次即成功 /
  瞬时失败后重试成功 / 重试耗尽 / 非瞬时故障不重试 / 欠费不重试 / 重试次数为 0 /
  `HttpRequestException` 按瞬时处理 / 静默登录必须跳过重复网络检测。测试总数 **177**。

### 排查结论：开机自启功能本身是正常的

逐项核对后确认机制无问题：

| 检查项 | 结果 |
| --- | --- |
| 计划任务存在 | ✓ `\AutologinForDrcom`，状态 Ready |
| 动作 | ✓ `"D:\autologin\AutoLoginNet.exe" --autostart` |
| 触发方式 | ✓ 登录时（At logon time） |
| 权限 | ✓ 最高权限（`/RL HIGHEST`） |
| 目标文件 | ✓ 存在，512 个文件，含 `App.xbf` 与 `AutoLoginNet.pri` |
| 凭据 | ✓ `credentials.json` 存在（无凭据则无法自动登录） |
| 实测 `--autostart` | ✓ 退出码 0，日志正确 |

**两点注意事项**：

1. 任务指向 `D:\autologin\` 下的一份**副本**，而非本仓库构建产物。改完代码需重新发布到该目录，
   否则开机执行的仍是旧版本。
2. 该任务带有「使用电池时不启动」（`Stop On Battery Mode, No Start On Batteries`，schtasks 默认值），
   若希望插拔电源都能自启需调整任务设置。

### 界面

- **ComboBox 下拉列表改为完全不透明**：WinUI 默认给下拉列表用的是带透明的亚克力画刷
  （`ComboBoxDropDownBackgroundBrush`），叠在 Mica 背景上会透出后面的内容，深色主题下尤其影响阅读。
  已在 `AppStyles.xaml` 的 `ThemeDictionaries` 中把该画刷及其 PointerOver / Pressed 三个状态
  全部替换为实心主题色——三个状态都要给，否则悬停时会短暂变回半透明
  （WinUI 对这些画刷做颜色动画）。取色仍用系统主题令牌，浅色/深色/高对比度依然自动跟随。

> 备注：ComboBox 下拉的透明效果最初未被发现，是因为代码里确实**没有任何**透明度覆盖——
> 那是 WinUI 默认样式。经确认指的是 ComboBox 下拉列表后，已按上节改为不透明。

---

## v2.0.0 — 完全重构（WinUI 3）

### 架构

- **重写为三项目分层**，依赖方向严格单向 `App → Windows → Core`：
  - `AutoLoginNet.Core`（net10.0）：领域逻辑，**不含任何 Windows API 调用**，可跨平台测试。
  - `AutoLoginNet.Windows`（net10.0-windows）：DPAPI、netsh、路由选卡、外部进程执行。
  - `AutoLoginNet.App`（WinUI 3）：界面与视图模型。
- **删除冗余项目**：
  - 根目录控制台程序（`Program.cs` / `App/ApplicationRunner.cs` / `ConsoleAdapter`）——与 GUI 存在
    两套重复的编排逻辑；自启改由 GUI 进程静默执行，能力无损失。
  - `BenchmarkSuite1`——只有 BenchmarkDotNet 启动桩，没有任何基准测试代码。
  - 旧 WPF 界面（`Themes/*.xaml`、`Ui.cs`、`ThemeManager`、`UserSettings`、`AutostartHelper`、`DnsService`）。
  - `Setup/*.wxs` 与 `dist/` 旧安装包与产物——其清单指向已不存在的目录结构。
- 主题与控件样式全部改用 **WinUI 系统主题资源**（`CardBackgroundFillColorDefaultBrush` 等），
  不再维护自绘双色板；顺带获得高对比度主题与无障碍支持。

### 界面（全新设计）

- 采用 `NavigationView` 外壳 + Mica 材质背景，四个页面：**首页 / 网络工具 / 运行日志 / 设置**。
- 首页：网络状态卡片（校园网与外网分别展示延迟与判定结论）、账号表单、大号主操作按钮、
  结果 `InfoBar`、可展开的「认证服务器原始响应」。
- 网络工具：DNS 优选（表格化展示各提供商首选/备选延迟与推荐标记）、连通性诊断、测速入口。
- 运行日志：实时滚动、级别与关键字过滤、自动滚动开关、条数与文件路径状态栏。
- 设置：外观、启动行为、认证服务器、工具外链、数据位置，以及一键恢复默认。
- 侧栏底部实时显示连接状态，与首页共享**同一个** `ConnectivityViewModel`，
  不再出现「两处状态互相矛盾」的问题。
- 引入轻量 MVVM 基类（`ObservableObject` / `AsyncRelayCommand`），不引入第三方 MVVM 库。

### 修复的缺陷

| 级别 | 问题 | 修复 |
| --- | --- | --- |
| **安全** | 密码通过 GET 查询串传输，会进入日志、代理与浏览器历史 | 改为 **POST 表单**提交，URL 中不再携带任何业务参数 |
| **功能** | `--autostart` 从未生效：非打包 WinUI 应用的 `LaunchActivatedEventArgs.Arguments` 恒为空 | 改为读取 `Environment.GetCommandLineArgs()`；并把静默登录改为同步等待，确保退出前不创建窗口 |
| **功能** | 开机自启在非管理员下静默失败，界面却显示成功 | 提权子命令流程；并用标记文件记录用户意图，避免状态回跳 |
| **功能** | DNS 优选会改到 Hyper-V / WSL / VPN 虚拟网卡，可能直接断网 | 按**目标地址**（校园网认证服务器）而非默认网关选卡；`netsh` 改用不受语言影响的 `idx=` 定位 |
| **功能** | 登录结果判定依赖返回码，`result=1` 且消息为「已在线」时误判为首次成功 | 文本判定优先于返回码，并补齐欠费/停机/密码错误等关键词 |
| **功能** | Ping 失败即判定网络不可达（对端丢弃 ICMP 时误报） | 增加 **TCP 443 回退探测** |
| **功能** | 日志脱敏正则 `[^&s]` 会在字母 `s` 处截断，导致密码后半段泄漏 | 重写脱敏规则，覆盖 `key=value`、`key: "value"`、JSON 三种形态 |
| **功能** | 内存日志无上限，长时间运行会持续增长 | 环形缓冲上限（默认 1000 条），界面列表同样设限 |
| **功能** | 旧格式凭据因缺少 `userName` 字段被报为「内容无效」，提示无指导意义 | 先判版本再判内容，明确提示「请重新登录以保存新格式」 |
| **功能** | 网卡中文名显示为乱码（PowerShell 按 UTF-8 输出，却按 OEM 代码页解码） | `ProcessRunner` 支持显式指定解码方式；PowerShell 调用强制 UTF-8 |
| **健壮性** | 配置文件损坏会导致启动异常 | 分层回退默认值 + 数值范围收敛（`Normalize()`） |
| **健壮性** | 凭据/配置写入中途失败会留下半截 JSON | 改为临时文件 + 原子替换 |
| **健壮性** | `schtasks` / `netsh` 输出未读取可能导致子进程阻塞 | 统一 `ProcessRunner`：异步读流 + 超时终止 |

### 其他改进

- 登录成功后自动保存凭据（用户可关闭），失败时绝不保存。
- 配置项合并为分组结构（`auth` / `probes` / `logging` / `startup` / `tools`），集中到单一配置对象，
  避免原先配置散落两处。
- 修改「HTTP 超时」后重建 `HttpClient`，使该设置即时生效。
- 测试从 6 个扩充到 **90 个用例**，改用假实现（而非真实网络 / DPAPI）保证可重复执行；
  新增覆盖：密码不入 URL、脱敏规则、凭据版本迁移、`netsh` 中英文本地化解析、日志滚动、配置回退。

## v2.0.1 — 修复实测反馈的四个问题

| 问题 | 根因 | 修复 |
| --- | --- | --- |
| DNS 状态显示不正确（明明有 DNS 却显示「自动获取」） | `netsh interface ipv4 show dnsservers` **只接受位置参数形式的接口名，不接受 `idx=`**，命令直接语法报错，于是永远读不到值 | 改用名称参数；同时补上「通过 DHCP 配置的 DNS 服务器」小节的解析——改回自动获取后不再误报为无 DNS。实机验证：正确显示 `手动设置：114.114.114.114 / 223.5.5.5` |
| 已授权管理员但 DNS 未修改 | 写入命令同样用了 `idx=`，同样语法报错；且提权子进程没有回传结果，主界面无从判断 | 修正写入参数；子进程把结果写入约定文件，主进程等待其退出后读取并回读网卡状态；写入后立即回读比对，**只有真正生效才报告成功**；另增加 `Set-DnsClientServerAddress`（按接口索引）作为独立备用路径 |
| 运行日志页加载慢、卡顿 | `LogsPage` 订阅了 `Entries.CollectionChanged`，而处理器内部又对 `ObservableCollection` 调用 `Clear()`/`Add()`，**重入自触发**，每次加载产生 O(n²) 次集合事件；且每次 `Add` 都调用一次 `ScrollIntoView` | 改为「快照 + 尾部增量」渲染，同一批消息合并为一次界面刷新；自动滚动在一批结束后只做一次，并由 `ScrollViewer.ViewChanged` 感知用户是否停在底部（查看历史时不再被拽回） |
| 测速地址被改成了通用站点 | 重构时误判为「校内地址不可知」而留空 | 恢复原项目地址：内网 `http://speedtest.jxust.edu.cn`、外网 `http://wangsuceshi.bmcx.com` |
| DNS 测速只显示部分结果 / 大量显示 `—` | 用 **ICMP Ping** 判断 DNS 可用性：公共 DNS 普遍丢弃 ICMP（实测 8.8.8.8、119.29.29.29、114.114.114.114 均 ping 不通，却都能正常解析），10 个候选里有 8 个被误判为不可达；而且「ping 得通」根本不等价于「解析快」 | 改为发出**真实 UDP DNS 查询**并计时（构造标准 A 记录报文、校验应答 ID/QR/RCODE），UDP 53 被拦时回退 TCP 53。列表沿用原有排版，在每个地址下方内联显示其解析延迟；底部汇总可达数量与最快/最慢，并列出推荐的首选与备选 |

测试用例增至 **114 个**，新增覆盖：DHCP 地址解析、静态优先于 DHCP、地址比对容错、DNS 结果文件往返、网卡名不得夹带 netsh 语法，以及 DNS 查询报文构造、应答校验（ID 不匹配 / QR=0 / SERVFAIL / 截断包）、测量结果的延迟与占位显示。

## v2.0.6 — 导出安装包（并修复发布包缺文件）

### 安装包

- 新增 `Setup/Build-Installer.ps1`：一条命令完成「自包含发布 → 生成 WiX 清单 → 编译 MSI」。
  文件清单由脚本自动收集（512 个文件、92 个目录），组件 GUID 由文件路径推导为确定值，
  保证重复构建结果稳定、升级安装可正确识别。
- 产出两种分发形式：
  - `AutoLoginNet-2.0.5-x64.msi`（约 53 MB）——按机器安装到 `Program Files`，
    带开始菜单快捷方式，支持同产品升级（自动卸旧版）；
  - `AutoLoginNet-2.0.5-portable-x64.zip`（约 70 MB）——解压即用，免安装。
- 两者均为**自包含**：目标机无需预装 .NET 运行时，也无需 Windows App Runtime。
- `dist/` 与 `*.wixpdb` 已加入 `.gitignore`（发布产物约 170 MB+，不应入库）。

### 修复：发布包缺少编译后的 XAML，装出来无法启动

排查安装包时发现 **`dotnet publish` 不会收集 WinUI 的 `*.xbf`（编译后的 XAML）与
`AutoLoginNet.pri`（资源索引）**。缺失时程序能启动、也会写几行日志，
但随即以 **`0xC000027B`（WinRT 致命错误）** 崩溃 —— 也就是说这样打出来的安装包**装完根本用不了**。

- 旧版本之所以没暴露这个问题，是因为当时的 WiX 清单（`Setup/Files.wxs`）**逐文件列出**了产物，
  恰好把 `xbf`/`pri` 也带上；改用 `dotnet publish` 后就没有这层保护了。
- 现在在 `AutoLoginNet.App.csproj` 里加了 `CopyWinUIResourcesToPublish` 目标，
  在 `ComputeFilesToPublish` 之后把这些文件补进发布目录；
  构建脚本还会**主动校验** `AutoLoginNet.pri` 与 `App.xbf` 是否存在，缺失就直接报错，
  避免再产出装完打不开的包。
- 验证方式：用 `msiexec /a` 管理安装解包（不改系统、不需管理员），
  再直接运行解包出来的 exe —— 窗口正常打开，版本 2.0.5.0，`--autostart` 退出码 0。

### 其他

- 版本号从 2.0.0 同步到 2.0.5（此前五轮改动都没有更新过程序集版本，安装包里会一直显示旧版本号）。
- 安装包未签名，安装时 Windows 会弹出 SmartScreen 提示，属预期行为。

## v2.0.5 — 开机自启移到首页

- 把「开机自启」开关从**设置页**移到**首页**的账号信息区，紧邻「记住账号密码」等登录选项：
  它属于日常登录操作，放在首页比埋在设置里更顺手。
- 设置页移除「启动」卡片，原两列布局改为「外观」独占整行，避免留下半张空卡片；
  页面副标题与说明文案同步更新（不再提"启动行为"）。
- 首页每次显示时回读真实状态：计划任务可能被「任务计划程序」在外部改动。
- 顺带清理：`SettingsViewModel` 中随开关一起失效的 `_suppressPersist` 字段
  已无任何写入点（该判断恒为真），一并删除。

## v2.0.4 — 新增注销（断开校园网认证）

- **新增「注销（断开认证）」按钮**（首页操作区）：调用门户注销接口主动断开会话。
  - 端点与参数取自门户自己声明的配置：`/eportal/?c=ACSetting&a=Logout&ver=1.0`。
  - 只在**明确检测到外网可达**时才可用——注销会断网，不能让用户在离线状态下误点而丢掉恢复能力。
  - 点击前有二次确认，注销后立即重新探测网络状态。
- **实测得出的结果判定规则**（与直觉相反，已写成回归测试）：
  门户注销返回 HTML，成功与失败的 `Dr.COMWebLoginID_n.htm` 标记**都是 `_2`**，
  标记对注销没有区分能力（配置里的 `authsuccess/_3`、`authfail/_2` 是针对登录页的）。
  唯一可靠的信号是正文文案：成功为 `Logout succeed.`，本就不在线为 `Logout failed.`。
  因此实现以正文为准，标记仅作兜底。
  > 我最初按门户配置把 `_2` 当作失败标记，**实机验证时发现注销其实成功了却被报成失败**，
  > 才做了「在线注销 vs 离线注销」的对照实验纠正过来。

### 顺带修复：开机自启状态判断被默认值掩盖

排查过程中发现 `AutoStartMarker`（嵌套类 + 属性初始化器）存在真实缺陷：
反序列化键名不匹配时 `TaskName` 会**静默保留默认值 `AutologinForDrcom`**，
于是 `IsEnabled` 会去查询宿主机上真实存在的同名任务，把「本机从未启用」误报为「已启用」。

- 该类型提为顶层并加显式 `[JsonPropertyName]`，且 `TaskName` 不再设初始化器，
  反序列化失败时即为 `null`，调用方必须显式兜底，问题不再被掩盖。
- 两个相关单测原本依赖宿主机真实状态（在本机开启自启后随即失败），已改为使用唯一任务名与显式标记，
  不再受开发机环境影响。

测试用例增至 **167 个**，新增覆盖：注销响应解析（成功文案 / 失败文案 / 相同标记不同文案的对照 /
`Logout failed` 不被误判为成功 / 标记兜底 / JSONP 形态）、注销端点与参数、注销服务异常处理，
以及开机自启标记的反序列化与状态判定。

## v2.0.3 — 修复登录失败（账号未被服务器读取）

**症状**：登录提示 `dr1003({"result":0,"msg":"无法获取用户认证账号！","ret_code":""})`。

**根因**：我此前把凭据从 URL 改到了 POST body（当时是出于安全考量）。实测证明这个门户
**完全不解析 POST body**——逐个隔离验证如下：

| 账号位置 | 密码位置 | 服务器响应 |
| --- | --- | --- |
| body | body | `无法获取用户认证账号！` ← 之前的实现 |
| query | query | `IP: 10.22.187.75 已经在线！` |
| query | body | `无法获取用户认证密码！`（说明账号已被读到） |
| body | — | `无法获取用户认证账号！` |

结论：`/eportal/portal/login` **只从查询串读取账号**，且密码也必须出现在查询串中。

**修复**：账号与密码都编码进查询串（请求仍用 POST，body 为空）。
新增 `AppOptions.BuildLoginQuery` 集中负责参数拼装与转义，并加注释说明这是服务器端的既有约定。

**顺带修复**：
- **`ret_code=2` 未被识别**：本校园网把「IP 已在线」表达为 `result=0` + `ret_code=2` + 消息「已经在线！」。
  旧实现只看 `result`，会把这种情况误判为登录失败。现在解析 `ret_code` 并映射为「已在线」。
- **URL 转义缺陷**（被新单测抓出）：用 `UriBuilder.Query` 赋值会对已编码内容二次转义，
  把 `%20` 变成裸空格，从而破坏密码。改为直接拼接查询串，并让测试断言 `AbsoluteUri`
  （`Uri.ToString()` 会把 `%20` 还原成空格，容易误判）。

**注意（安全性取舍）**：密码回到 URL 中是**服务器强制要求**，客户端没有别的选择。
风险由以下措施收敛：日志写入前把 `user_password` 脱敏（已有测试守护）、
请求仅发往校园网内网的明文 HTTP 门户。
若将来门户升级为支持 body 传参，应改回 body 并同步修改单测。

实机验证：用假账号请求真实门户，服务器已能读到账号并返回「IP 已经在线」，
不再返回「无法获取用户认证账号」。

测试用例增至 **150 个**，新增覆盖：查询串凭据编码（含 `&`/`=`/空格/`#` 等特殊字符）、
`ret_code=2` 判定为「已在线」、服务器拒绝时如实回显原始消息，以及真实响应体的解析。

## v2.0.2 — 新增 hosts 域名解析功能

- **新增「校内域名解析（hosts）」功能**：把 14 条 JXUST 校内站点的静态解析写入系统 hosts，
  解决「改用公共 DNS 后代理、办事大厅、教务系统等校内站点解析不到」的问题。
  - 覆盖官网（含 IPv4 与 IPv6 两条）、资源列表、办事大厅、系统认证、教务系统、校园卡、
    UAM、数据门户、数据中心、智能填报、研究生系统、仪器预约等。
  - **只维护一个带哨兵注释的托管区块**：幂等（重复应用不累积重复行）、可精确撤销
    （只删自己的块，绝不触碰用户手写条目）、用户可读（打开文件就知道哪些是程序写的）。
  - 写入前**自动备份**原文件到应用数据目录（文件名带时间戳）；先写临时文件再原子替换；
    保留原换行风格且不写 BOM。
  - 通过已有的提权通道执行，结果经约定文件回传，界面显示真实结论而非「已请求授权」。
  - 新增「已被用户手工维护」检测：若 hosts 中已存在同名映射，会明确提示无需重复注入
    （用户条目位于托管块之前，Windows 取首个匹配，因此优先级更高）。
  - 配置项 `tools.hostsEntries` / `tools.enableHostsFeature` 可自定义规则或关闭该功能。

## v1.x 历史（WPF / 控制台，已移除）

- v1.5：UI 主题改为 Material 风格，按钮水波纹与卡片阴影。
- v1.4：主界面开机自启即时生效；修复 `schtasks` 引号问题与自启无法关闭的问题。
- v1.3：抽取 `AutoLoginNet.Core` 共享核心库；控制台与 WPF 双入口。
- v1.2：浅色主题、暗黑模式、DNS 优选与 Ping 诊断。
- v1.1：WinUI 3 界面改为自包含部署，解决运行时版本不匹配。
- v1.0：初始版本，控制台自动登录 + 计划任务开机自启。
