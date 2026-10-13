<#
.SYNOPSIS
    定义发布目录中「运行时不会被加载」的文件/目录规则，供 Export-Installer.ps1 裁剪使用。

.DESCRIPTION
    规则不是靠静态分析「猜」出来的，而是逐文件实测出来的：
    对每个候选文件，复制一份完整发布目录、只删这一个文件、启动应用并等待主窗口出现，
    能正常打开才算「可安全删除」。测试用脚本是 Setup\tmp-bisect2.ps1（临时文件，用完即删）。

    实测结论（x64 自包含发布目录，共 44 个候选）：
      * 43 个删除后应用照常启动；
      * 唯一的例外是 Microsoft.UI.Xaml.Phone.dll —— 删掉后应用在启动阶段以
        0xC000027B (STATUS_STOWED_EXCEPTION) 崩溃在 Microsoft.UI.Xaml.dll，故障模块
        combase.dll，NTSTATUS 0x8007007E（找不到模块）。该文件被 WinUI 按名字动态加载，
        静态看「没人引用它」，实测却是硬依赖。已列入 $MustKeep。

    因此本文件刻意不用白名单式裁剪：WinUI 3 自包含运行时里有大量按名字 LoadLibrary
    的原生 DLL，静态分析无法证明其未被使用，误删的后果是「应用打不开」。

.NOTES
    每个规则项都是一个 hashtable：
      Name       规则名（用于日志）
      Wildcard   文件名通配（Wildcard 语义），支持子目录前缀如 NpuDetect\NPUDetect.dll
      Reason     为什么可以删（实测结论 + 为什么用不到）
      Evidence   证据来源
#>

Set-StrictMode -Version Latest

# ---------------------------------------------------------------- 语言资源目录
# WindowsAppSDK / WinUI 的框架 .mui 字符串。应用自身零 .resw / .resx，
# 界面文案全部硬编码简体中文，且 csproj 未设置 SatelliteResourceLanguages
# （用户要求：程序去除多语言支持，仅保留简体中文）。
# 发布目录里的语言目录每个只有 2 个文件：Microsoft.ui.xaml.dll.mui 与 Microsoft.UI.Xaml.Phone.dll.mui。
#
# 保留项按大小写不敏感比较（发布目录里的真实名字是 zh-CN / en-us / en-GB，大小写不可想当然）。
# 特别注意 en-US 不能删：实测删掉它、应用立刻以 0xC000027B 在启动阶段崩溃——
# 框架在「中性区域性」下会去 en-us 找资源。
#
# 保留名单已从 5 个（zh-CN / zh-TW / en / en-US / en-GB）收到 2 个：
# 只有 zh-CN 与 en-US 真的存在对应 .mui，其余名字是历史上按「常见区域性」保守加的，从来没匹配到东西。
# 裁剪后必须重新启动发布目录验证（Export-Installer.ps1 的校验步骤会做这件事）。
$KeepCultures = @('zh-CN', 'en-US')

# 明确不保留的语言目录不在这里硬编码：Remove-UnusedPublishFiles.ps1 直接枚举发布根下
# 「名字形如区域性、且只含 .mui」的目录，并跳过 $KeepCultures 里的名字。
# 这样框架将来新增/改名语言目录时无需改脚本。

# ---------------------------------------------------------------- 实测可安全删除的文件
$StripFileRules = @(
    # --- 手机版 XAML 运行时（唯一实测不能删的候选，见 $MustKeep） ---

    # --- NPU / AI / 视觉：源码零引用 ---
    @{ Name = 'NPU 检测模块'; Wildcard = 'NpuDetect\NPUDetect.dll'
       Reason = '源码 grep NPUDetect|NoiseAsset 零命中；删除后实测启动正常'
       Evidence = '实测 + 源码 grep' }
    @{ Name = 'Windows AI 运行时'; Wildcard = 'Microsoft.Windows.AI*.dll'
       Reason = '未使用 Windows AI API；删除后实测启动正常'
       Evidence = '实测' }
    @{ Name = '图像互操作'; Wildcard = 'Microsoft.Graphics.Imaging*.dll'
       Reason = '未使用；删除后实测启动正常'
       Evidence = '实测' }

    # --- 工作负载 / 语义搜索 / 洞察资源 ---
    @{ Name = '工作负载'; Wildcard = 'Microsoft.Windows.Workloads*.dll'
       Reason = '未使用工作负载机制；删除后实测启动正常'
       Evidence = '实测' }
    @{ Name = '工作负载清单'; Wildcard = 'workloads*.json'
       Reason = '发布目录里的 5 个 workloads.*.json 未被读取；删除后实测启动正常'
       Evidence = '实测' }
    @{ Name = '未引用资源'; Wildcard = 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'
       Evidence = '实测' }

    # --- 打包应用（已安装）才需要的部署/重启/推送辅助 ---
    # 本产品是 unpackaged（WindowsPackageType=None），这些路径永远不会走到。
    @{ Name = '重启代理'; Wildcard = 'RestartAgent.exe'
       Reason = '仅打包应用的 Restart 机制使用；删除后实测启动正常'
       Evidence = '实测' }
    @{ Name = '通用后台任务'; Wildcard = 'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll'
       Reason = '仅打包应用的后台任务注册使用；删除后实测启动正常'
       Evidence = '实测' }
    @{ Name = '会话 IPC 代理'; Wildcard = 'SessionHandleIPCProxyStub.dll'
       Reason = '仅打包应用间会话共享使用；删除后实测启动正常'
       Evidence = '实测' }
    @{ Name = '推送通知投影'; Wildcard = 'Microsoft.Windows.PushNotifications.Projection.dll'
       Reason = '未使用推送通知；删除后实测启动正常'
       Evidence = '实测' }

    # --- 应用程序集闭包未引用的托管投影（WinRT projection） ---
    # 判据：这些 Projection.dll 不在 AutoLoginNet*.dll / Microsoft.WinUI.dll 的 AssemblyRef 里。
    # 每一个都单独做过「删掉能否启动」实测。
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Security.Authentication.OAuth.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.Media.Capture.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.Storage.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.Storage.Pickers.Projection.dll'
       Reason = 'AssemblyRef 闭包外，文件选择用 Win32 对话框；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.System.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.System.Power.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.AppNotifications*.Projection.dll'
       Reason = 'AssemblyRef 闭包外，应用无通知功能；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.BadgeNotifications.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.Foundation.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.AppLifecycle.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.ApplicationModel.Background.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.Management.Deployment.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.ApplicationModel.DynamicDependency.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.ApplicationModel.WindowsAppRuntime.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = '未引用投影'; Wildcard = 'Microsoft.Windows.Widgets.Projection.dll'
       Reason = 'AssemblyRef 闭包外；删除后实测启动正常'; Evidence = '实测' }

    # --- WebView2：应用没有浏览器控件 ---
    @{ Name = 'WebView2 原生加载器'; Wildcard = 'WebView2Loader.dll'
       Reason = '源码无 WebView2 引用；删除后实测启动正常'; Evidence = '实测' }
    @{ Name = 'WebView2 原生核心'; Wildcard = 'Microsoft.Web.WebView2.Core.dll'
       Reason = '同上；注意托管投影 Microsoft.Web.WebView2.Core.Projection.dll 必须保留（在 $MustKeep）'
       Evidence = '实测' }

    # --- 未被任何页面使用的图片 ---
    @{ Name = '未引用图片'; Wildcard = 'WindowsAppRuntime.png'
       Reason = '仅打包应用的运行时安装界面用；删除后实测启动正常'; Evidence = '实测' }
)

# ---------------------------------------------------------------- 明确禁止删除
# 这些名字看着像可以删，实际删了就崩或打不开。列在这里是为了让审计结论可追溯，
# 也为了让 Verify-PrunedPublish.ps1 能回报「这些确实被保留了」。
#
# 注意：本清单是当年按「自包含发布」实测出来的。现在改为框架依赖发布（方案 B）后，
# 下面有相当一部分文件由系统提供、发布目录里本来就没有——这些移到了 $MustKeepSystemProvided。
# 保留原清单是为了「一旦又切回自包含发布，这套结论仍然生效」。
$MustKeep = @(
    @{ Wildcard = 'Microsoft.UI.Xaml.Phone.dll'
       Reason = '实测硬依赖：删除后启动即以 0xC000027B 崩溃在 Microsoft.UI.Xaml.dll（combase 报 0x8007007E 找不到模块）' }
    @{ Wildcard = 'Microsoft.Windows.SDK.NET.dll'
       Reason = '缺失会让 x:Bind 页面在运行期抛 NullReferenceException' }
    @{ Wildcard = 'WinUIEdit.dll'
       Reason = '缺失则应用完全无法启动' }
    @{ Wildcard = 'Microsoft.InteractiveExperiences.Projection.dll'
       Reason = '被 AutoLoginNet.dll 与 Microsoft.WinUI.dll 双重引用' }
    @{ Wildcard = 'marshal.dll'
       Reason = 'Microsoft.UI.Input.dll 的静态导入依赖' }
    @{ Wildcard = 'dcompi.dll'
       Reason = 'Microsoft.DirectManipulation.dll 的静态导入依赖' }
    @{ Wildcard = 'wuceffectsi.dll'
       Reason = '静态导入依赖' }
    @{ Wildcard = 'MRM.dll'
       Reason = 'Microsoft.Windows.ApplicationModel.Resources.dll 的静态导入依赖' }
    @{ Wildcard = 'DWriteCore.dll'
       Reason = '无静态导入者，按名动态加载，不能证明安全；实测未删，保持保留' }
    @{ Wildcard = 'dwmcorei.dll'
       Reason = '同上' }
    @{ Wildcard = 'DwmSceneI.dll'
       Reason = '同上' }
    @{ Wildcard = 'Microsoft.Web.WebView2.Core.Projection.dll'
       Reason = 'Microsoft.WinUI.dll 的 AssemblyRef 引用了它' }
    @{ Wildcard = '*.pri'
       Reason = '资源索引，ms-appx 查找依赖' }
    @{ Wildcard = '*.xbf'
       Reason = '编译后的 XAML；缺失会 0xC000027B' }
)

# ------------------------------------------------ 框架依赖发布下「本来就不该在包里」的文件
# 框架依赖（方案 B）下，Windows App Runtime 的原生件与投影由系统安装的 1.8 运行时提供；
# 发布目录里没有它们不是裁剪删掉的，而是压根没发布。这里单独列出来，是为了：
#   1. Remove-UnusedPublishFiles.ps1 不再把它们报成「必须保留项却不存在」的 WARNING
#      （16 条噪音警告会淹没真正的告警）；
#   2. 反向校验：如果它们又出现在发布目录里，说明有人把自包含开关打开了，应该报警告提醒。
# 依据：dist\tmp-probe-B 与正式框架依赖发布的交付清单（25 个文件）实测，均无这些文件。
$MustKeepSystemProvided = @(
    'Microsoft.UI.Xaml.Phone.dll'   # WinUI 原生（动态按名加载，自包含下删不得）
    'WinUIEdit.dll'                 # WinUI 输入栈硬依赖，自包含下删不得
    'marshal.dll'
    'dcompi.dll'
    'wuceffectsi.dll'
    'MRM.dll'
    'DWriteCore.dll'
    'dwmcorei.dll'
    'DwmSceneI.dll'
    '*.winmd'                       # 45 个 / 2.57 MB，由系统运行时的 Microsoft.WindowsAppRuntime.dll 按命名空间查找
)
