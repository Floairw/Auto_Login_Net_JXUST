// 安装时探测旧版安装目录的自定义动作（原生 DLL）。
//
// 为什么是原生 C++ 而不是托管的 C#：
//   1) msiexec 只会用 LoadLibrary + GetProcAddress 去调 DllEntry 自定义动作，
//      它不会帮你启动 CLR；托管 DLL 必须经过 DTF 的 MakeSfxCA 打包才能被加载，
//      而 WiX 5 的 SDK 里已经没有 MakeSfxCA、也没有 WixToolset.Dtf.CustomAction 包
//      （只有 WixToolset.Dtf.WindowsInstaller.dll），这条路在本机工具链上走不通；
//   2) 这个动作只做「读一个属性、查一次 MsiGetProductInfo、回写一个属性」，
//      用原生代码写出来是一个几 KB 的 DLL，不引入任何运行时依赖。
//
// 它解决的问题：MSI 的目录选择对话框在 UI 序列里就把 INSTALLFOLDER 填好了，
// 等不到 Execute 序列；所以必须在 UI 序列里、且早于 InstallDirDlg 先行介入，
// 否则用户升级时会被重新问一遍目录，而且默认指向一个新目录（旧版被卸载但新文件
// 落在别处）。这里读旧产品自己的 MSI 目录表，把 INSTALLFOLDER 预先指到旧目录。
//
// 注意：本文件必须用 /utf-8 编译（见 Build-Installer.ps1）。否则在 936 代码页下，
// 中文注释的末字节可能被当成行继续符，把下一行代码吞掉。

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <stdio.h>
#include <stdlib.h>

// x64 没有名字修饰（stdcall 的 @N 后缀只在 x86 上出现），
// 所以这里导出的是无修饰名；msiexec 按 CustomAction 表里的 DllEntry 原样查找。
#pragma comment(linker, "/EXPORT:DetectPreviousInstallLocation,PRIVATE")
#pragma comment(linker, "/EXPORT:DetectPreviousInstallLocationA,PRIVATE")

// 与托管夹具 CustomActions.Harness 里的属性名保持一致。
static const wchar_t* kInstallFolderProperty = L"INSTALLFOLDER";
static const wchar_t* kExplicitTargetProperty = L"ALN_TARGET_DIR";

// ---------------------------------------------------------------------------
// 自检日志。定义在这里（而不是文件末尾的自检区）是为了让**正式代码路径**
// 也能调用它留痕：诊断构建下扫描的中间量必须打得出来，否则真机上一个
// 「返回空」的结果无从定位是哪一段出的问题（踩过）。
//
// 为什么写 UTF-8：若用 WriteFile 直接写宽字符，得到的是 UTF-16，而 PowerShell 的
// Get-Content 会把它按单字节读出来，每个字符后面多一个 NUL，断言全部失配（踩过）。
// 正式构建（不带 ALN_SELFTEST）里这些函数没人调用，会被优化掉。
// ---------------------------------------------------------------------------

// 细节探针。只有设置 ALN_SELFTEST_VERBOSE 时才输出：默认跑一遍会枚举本机 400+
// 个产品，逐条打印会把自检结论淹掉（踩过）。排查时再打开。
//
// 整个自检日志实现都用 #ifdef 包住（而不是只包住调用点）：正式构建里所有调用点都被
// 条件编译掉了，留下一堆没人调用的内部函数会触发 C4505 警告，把构建输出弄脏。
#ifdef ALN_SELFTEST

static void SelfTestAppend(const wchar_t* line);

static void SelfTestProbe(const wchar_t* line)
{
    if (GetEnvironmentVariableW(L"ALN_SELFTEST_VERBOSE", NULL, 0) != 0)
    {
        SelfTestAppend(line);
    }
}

static void SelfTestAppend(const wchar_t* line)
{
    wchar_t path[MAX_PATH * 2];
    if (GetEnvironmentVariableW(L"ALN_SELFTEST_LOG", path, ARRAYSIZE(path)) == 0)
    {
        return;
    }

    bool isNew = GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES;

    FILE* file = NULL;
    if (_wfopen_s(&file, path, isNew ? L"wb" : L"ab") != 0 || file == NULL)
    {
        return;
    }

    if (isNew)
    {
        static const unsigned char bom[] = { 0xEF, 0xBB, 0xBF };
        fwrite(bom, 1, sizeof(bom), file);
    }

    // 先转 UTF-8 再写，保证非 ASCII（例如中文路径）不会丢。
    int needed = WideCharToMultiByte(CP_UTF8, 0, line, -1, NULL, 0, NULL, NULL);
    if (needed > 1)
    {
        char* utf8 = (char*)malloc((size_t)needed);
        if (utf8 != NULL)
        {
            int written = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, needed, NULL, NULL);
            if (written > 1)
            {
                fwrite(utf8, 1, (size_t)(written - 1), file); // 不含结尾 NUL
            }
            free(utf8);
        }
    }

    fwrite("\n", 1, 1, file);
    fclose(file);
}

#endif // ALN_SELFTEST

// 旧版主程序文件名。1.x 叫 AutoLoginNet.App.exe，2.x 叫 AutoLoginNet.exe。
// 列表按「新到旧」排列，因此下标越小越新。
static const wchar_t* kAppExecutableNames[] = {
    L"AutoLoginNet.exe",
    L"AutoLoginNet.App.exe",
};

// 当前这个包自己那一代的主程序下标。2.x 的包为 0。
// 升级时「同代优先」就是拿它和别的产品的主程序下标比较：相等即同代。
static const int kAppKind = 0;

// 「这份安装是 1.x 还是 2.x」由目录里的主程序文件名决定 —— 这是实测本机唯一可靠的判据：
//   C:\Program Files\AutoLoginNet App  只有 AutoLoginNet.App.exe (1.0.0.0)
//   D:\autologin                       只有 AutoLoginNet.exe     (2.0.7.0)
//
// 顺带把「目录里到底有没有我们的主程序」也判掉了：两者是同一个循环的两个侧面，
// 分开写就变成两份会各自漂移的重复逻辑（踩过）。命中返回下标（越小越新），否则 -1。
static int DetectAppKind(const wchar_t* directory)
{
    if (directory == NULL || directory[0] == L'\0')
    {
        return -1;
    }

    for (int i = 0; i < ARRAYSIZE(kAppExecutableNames); ++i)
    {
        wchar_t candidate[MAX_PATH * 2];
        if (swprintf_s(candidate, ARRAYSIZE(candidate), L"%s\\%s", directory, kAppExecutableNames[i]) < 0)
        {
            continue;
        }

        DWORD attributes = GetFileAttributesW(candidate);
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            return i;
        }
    }

    return -1;
}

// 把版本串（"1.0.0"、"2.0.7"）打成可比较的 64 位整数。
//
// 为什么要自己打：MsiGetProductInfo 只给字符串，"2.0.10" < "2.0.9" 这种字符串比较是错的；
// 而 MSI 的版本规则是四段、每段 0..65535，正好一字节 16 位装得下。
static unsigned __int64 PackVersion(const wchar_t* version)
{
    unsigned __int64 parts[4] = { 0, 0, 0, 0 };
    int index = 0;
    const wchar_t* cursor = version;

    while (index < 4 && cursor != NULL && *cursor != L'\0')
    {
        wchar_t* end = NULL;
        unsigned long value = wcstoul(cursor, &end, 10);
        if (end == cursor)
        {
            break;
        }

        parts[index++] = value > 0xFFFFu ? 0xFFFFu : (unsigned __int64)value;
        cursor = (*end == L'.') ? end + 1 : NULL;
    }

    return (parts[0] << 48) | (parts[1] << 32) | (parts[2] << 16) | parts[3];
}

// 取已装产品的版本，取不到返回 0（0 会被当成最旧，可接受：取不到版本的产品本就不可信）。
static unsigned __int64 GetProductVersionValue(const wchar_t* productCode)
{
    wchar_t value[64];
    DWORD length = ARRAYSIZE(value);
    if (MsiGetProductInfoW(productCode, INSTALLPROPERTY_VERSIONSTRING, value, &length) != ERROR_SUCCESS)
    {
        return 0;
    }

    return PackVersion(value);
}

// 目录里是否真的存在某个历史版本的主程序。
static bool DirectoryLooksLikeOurInstall(const wchar_t* directory)
{
    return DetectAppKind(directory) >= 0;
}

// 去掉 MSI 目录表 DefaultDir 里的短名前缀：
// "vkfebun5|AutoLoginNet App" 变成 "AutoLoginNet App"。
// 没有竖线时原样返回（已经统一了长短名，或者本来就是短名）。
static void StripShortNamePrefix(wchar_t* value)
{
    wchar_t* bar = wcschr(value, L'|');
    if (bar != NULL)
    {
        MoveMemory(value, bar + 1, (wcslen(bar + 1) + 1) * sizeof(wchar_t));
    }
}

class ProductEnumerator
{
public:
    ProductEnumerator()
    {
        m_index = 0;
    }

    bool Next(wchar_t* buffer, DWORD bufferCount)
    {
        // 防御性上限：本机实测已装产品约 400+ 个，20000 足够且能避免异常环境下卡死。
        if (m_index >= 20000)
        {
            return false;
        }

        DWORD length = bufferCount;
        if (MsiEnumProductsW(m_index, buffer) != ERROR_SUCCESS)
        {
            return false;
        }

        ++m_index;
        return length > 0 && buffer[0] != L'\0';
    }

private:
    DWORD m_index;
};

// 从 "D:\autologin\AutoLoginNet.exe" 得到 "D:\autologin"。
static bool IsDirectorySeparator(wchar_t c)
{
    return c == L'\\' || c == L'/';
}

static bool TryGetParentDirectory(wchar_t* path)
{
    for (wchar_t* cursor = path + wcslen(path); cursor > path; --cursor)
    {
        if (IsDirectorySeparator(*cursor))
        {
            *cursor = L'\0';
            return path[0] != L'\0';
        }
    }

    return false;
}

struct ProductInfo
{
    int kind;                      // 本产品这一代的主程序下标；<0 表示不是我们的产品
    bool hasInstalledDirectory;    // installedDirectory 是否可信
    wchar_t installedDirectory[MAX_PATH * 2];
    unsigned __int64 version;      // 打包后的版本号，越大越新
};

// 把 Component 表的「组件键」翻译成真正的组件 GUID（ComponentId 列）。
//
// 为什么必须翻译：File 表的 Component_ 列存的是组件**键**（形如 "c3"），而
// MsiGetComponentPath 要的是组件 GUID。实测直接传 "c3" 时返回值恒为 -2
// （INSTALLSTATE_UNKNOWN），看起来像「没装」，其实只是参数给错了（踩过）。
static bool TryReadComponentId(MSIHANDLE database, const wchar_t* componentKey,
                               wchar_t* guidBuffer, DWORD guidCount)
{
    guidBuffer[0] = L'\0';

    MSIHANDLE view = 0;
    if (MsiDatabaseOpenViewW(database, L"SELECT `Component`,`ComponentId` FROM `Component`", &view) != ERROR_SUCCESS ||
        view == 0 ||
        MsiViewExecute(view, 0) != ERROR_SUCCESS)
    {
        if (view != 0)
        {
            MsiCloseHandle(view);
        }

        return false;
    }

    bool found = false;
    MSIHANDLE record = 0;
    while (!found && MsiViewFetch(view, &record) == ERROR_SUCCESS && record != 0)
    {
        wchar_t key[64];
        DWORD keyLength = ARRAYSIZE(key);
        if (MsiRecordGetStringW(record, 1, key, &keyLength) == ERROR_SUCCESS &&
            _wcsicmp(key, componentKey) == 0)
        {
            DWORD guidLength = guidCount;
            if (MsiRecordGetStringW(record, 2, guidBuffer, &guidLength) == ERROR_SUCCESS && guidBuffer[0] != L'\0')
            {
                found = true;
            }
        }

        MsiCloseHandle(record);
    }

    MsiCloseHandle(view);
    return found;
}

// 按产品码 + 上下文后缀求组件的实际落地路径。
//
// szProduct 是「产品上下文」，不是裸产品码。实测本机两种上下文的表现（产品码为
// {4FEDDE3C-...}，1.0.0 按机器安装）：
//   裸产品码          -> state=3（INSTALLSTATE_LOCAL），path 正确   ← 要的是这个
//   产品码后加 'G'    -> state=-2（INSTALLSTATE_UNKNOWN）
// 所以先试裸产品码。加 'G' 的写法只在上下文本身是 SID（如 "S-1-5-18"）时才成立；
// 之前误解了文档，一直只试带 'G' 的写法，于是升级时永远定位不到旧目录（踩过）。
//
// 返回值：true 表示确实装了且路径可用（pathBuffer 已去掉文件名）。
static bool TryGetComponentDirectory(const wchar_t* productCode, const wchar_t* componentId,
                                     wchar_t* pathBuffer, DWORD pathCount)
{
    wchar_t contexts[2][80];
    wcsncpy_s(contexts[0], ARRAYSIZE(contexts[0]), productCode, _TRUNCATE);
    wcsncpy_s(contexts[1], ARRAYSIZE(contexts[1]), productCode, _TRUNCATE);
    wcsncat_s(contexts[1], ARRAYSIZE(contexts[1]), L"G", _TRUNCATE);

    for (int i = 0; i < ARRAYSIZE(contexts); ++i)
    {
        wchar_t installedPath[MAX_PATH * 2];
        DWORD installedLength = ARRAYSIZE(installedPath);
        INSTALLSTATE state = MsiGetComponentPathW(contexts[i], componentId, installedPath, &installedLength);

        if (state == INSTALLSTATE_LOCAL && TryGetParentDirectory(installedPath))
        {
#ifdef ALN_SELFTEST
            {
                wchar_t trace[MAX_PATH * 6];
                swprintf_s(trace, ARRAYSIZE(trace), L"probe | ctx=[%s] | comp=[%s] | state=%d | path=[%s]",
                           contexts[i], componentId, (int)state, installedPath);
                SelfTestProbe(trace);
            }
#endif
            wcsncpy_s(pathBuffer, pathCount, installedPath, _TRUNCATE);
            return true;
        }
    }

    return false;
}

// 判断某个已装产品是不是我们的产品、装在哪、什么版本。
//
// 核心是「别猜」：不照 MSI 里的默认目录名去找，因为用户改过安装目录就完全对不上。
// 本机实测 2.0.7 的 MSI 里 DefaultDir 是 "AutoLoginNet"，而它其实装在 D:\autologin。
// 可靠做法是问 Windows Installer 自己：读该产品**缓存 MSI** 的 File 表找到主程序，
// 取出它所属组件，再用 MsiGetComponentPath 求该组件的实际落地路径。这个值就是安装时
// 真正用到的路径，用户改没改目录都不影响。实测两个版本：
//   1.0.0 -> C:\Program Files\AutoLoginNet App\AutoLoginNet.App.exe
//   2.0.7 -> D:\autologin\AutoLoginNet.exe
//   MsiGetComponentPath 返回值：3 = INSTALLSTATE_LOCAL（已装到本地），1 = 该功能未装。
//
// 返回值：true 表示这是我们的产品（info.kind >= 0）；false 表示不是或查不出来。
static bool ReadProductInfo(const wchar_t* productCode, ProductInfo* info)
{
    info->kind = -1;
    info->hasInstalledDirectory = false;
    info->installedDirectory[0] = L'\0';
    info->version = GetProductVersionValue(productCode);

#ifdef ALN_SELFTEST
    {
        wchar_t trace[MAX_PATH * 4];
        swprintf_s(trace, ARRAYSIZE(trace), L"probe | code=%s | version=%llu", productCode, info->version);
        SelfTestProbe(trace);
    }
#endif

    wchar_t localPackage[MAX_PATH * 2];
    DWORD packageLength = ARRAYSIZE(localPackage);
    if (MsiGetProductInfoW(productCode, INSTALLPROPERTY_LOCALPACKAGE, localPackage, &packageLength) != ERROR_SUCCESS)
    {
#ifdef ALN_SELFTEST
        SelfTestProbe(L"probe | 读不到缓存 MSI 路径");
#endif
        return false;
    }

    if (GetFileAttributesW(localPackage) == INVALID_FILE_ATTRIBUTES)
    {
#ifdef ALN_SELFTEST
        {
            wchar_t trace[MAX_PATH * 4];
            swprintf_s(trace, ARRAYSIZE(trace), L"probe | 缓存 MSI 不存在：[%s]", localPackage);
            SelfTestProbe(trace);
        }
#endif
        return false;
    }

    MSIHANDLE database = 0;
    UINT openStatus = MsiOpenDatabaseW(localPackage, MSIDBOPEN_READONLY, &database);
    if (openStatus != ERROR_SUCCESS || database == 0)
    {
#ifdef ALN_SELFTEST
        {
            wchar_t trace[MAX_PATH * 4];
            swprintf_s(trace, ARRAYSIZE(trace), L"probe | 打不开数据库 rc=%u：[%s]", openStatus, localPackage);
            SelfTestProbe(trace);
        }
#endif
        return false;
    }

    MSIHANDLE view = 0;

    // 谓词一律搬到内存里做，不写 WHERE。
    // 实测 "... WHERE `FileName` LIKE '%AutoLoginNet%'" 会让 MsiDatabaseOpenViewW
    // 返回 1615（ERROR_BAD_QUERY_SYNTAX）（踩过）。
    UINT viewStatus = MsiDatabaseOpenViewW(database, L"SELECT `FileName`,`Component_` FROM `File`", &view);
    UINT executeStatus = (viewStatus == ERROR_SUCCESS && view != 0) ? MsiViewExecute(view, 0) : viewStatus;

#ifdef ALN_SELFTEST
    {
        wchar_t trace[MAX_PATH * 4];
        swprintf_s(trace, ARRAYSIZE(trace), L"probe | 缓存 MSI=[%s] | OpenView=%u | Execute=%u",
                   localPackage, viewStatus, executeStatus);
        SelfTestProbe(trace);
    }
#endif

    if (viewStatus == ERROR_SUCCESS && view != 0 && executeStatus == ERROR_SUCCESS)
    {
        MSIHANDLE record = 0;
        while (MsiViewFetch(view, &record) == ERROR_SUCCESS && record != 0)
        {
            wchar_t fileName[MAX_PATH];
            DWORD fileNameLength = ARRAYSIZE(fileName);
            if (MsiRecordGetStringW(record, 1, fileName, &fileNameLength) == ERROR_SUCCESS && fileName[0] != L'\0')
            {
                StripShortNamePrefix(fileName);

                for (int i = 0; i < ARRAYSIZE(kAppExecutableNames); ++i)
                {
                    if (_wcsicmp(fileName, kAppExecutableNames[i]) != 0)
                    {
                        continue;
                    }

#ifdef ALN_SELFTEST
                    {
                        wchar_t trace[MAX_PATH * 4];
                        swprintf_s(trace, ARRAYSIZE(trace), L"probe | 命中主程序：[%s] | kind=%d", fileName, i);
                        SelfTestProbe(trace);
                    }
#endif

                    if (info->kind < 0)
                    {
                        info->kind = i;
                    }

                    // 只有在本次调用还没定下目录时才去问路径。
                    if (!info->hasInstalledDirectory)
                    {
                        wchar_t componentKey[64];
                        DWORD componentLength = ARRAYSIZE(componentKey);
                        if (MsiRecordGetStringW(record, 2, componentKey, &componentLength) == ERROR_SUCCESS &&
                            componentKey[0] != L'\0')
                        {
                            wchar_t componentId[64];
                            if (TryReadComponentId(database, componentKey, componentId, ARRAYSIZE(componentId)))
                            {
                                wchar_t componentDirectory[MAX_PATH * 2];
                                if (TryGetComponentDirectory(productCode, componentId,
                                                             componentDirectory, ARRAYSIZE(componentDirectory)))
                                {
                                    wcsncpy_s(info->installedDirectory, ARRAYSIZE(info->installedDirectory),
                                              componentDirectory, _TRUNCATE);
                                    info->hasInstalledDirectory = true;
                                }
                            }
                        }
                    }
                }
            }

            MsiCloseHandle(record);
        }
    }

    if (view != 0)
    {
        MsiCloseHandle(view);
    }

    MsiCloseHandle(database);
    return info->kind >= 0;
}

// 在给定产品码里挑一个「值得装回去」的安装目录。
//
// 取舍规则（安全优先，宁可退回默认目录也不装错地方）：
//   1) 只要真装到本地的、且目录里现在确实还有主程序的产品；
//   2) 同代（主程序文件名与当前包一致）优先于异代；
//   3) 同代里取版本号最大的 —— 用户当前正在用的就是最新的那份；
//   4) 版本也并列时取先枚举到的，保证结果稳定；
//   5) 一个都不满足就返回 false，退回默认安装目录。
//
// requireSameKind：为真时只接受与当前包同代（主程序文件名一致）的产品。
// 两段解析的要求不同：
//   · WIX_UPGRADE_DETECTED 那段要 true —— 它保证至少含一个同族产品（就是它把动作唤起来的），
//     绝不能因为同族那份恰好没装成而退回到**另一代**的目录：从 1.0.0 升级到 2.x 时
//     那样会把新版装进 1.x 的目录里（踩过）。
//   · 全量枚举那段要 false —— 它就是为「同族产品一个都没枚举到」准备的兜底。
static bool TryFindPreviousInstallLocation(const wchar_t* productCodes, bool requireSameKind,
                                           wchar_t* pathBuffer, DWORD pathCount)
{
    wchar_t codes[4096];
    wcsncpy_s(codes, ARRAYSIZE(codes), productCodes, _TRUNCATE);

    bool haveBest = false;
    int bestKind = 0;
    unsigned __int64 bestVersion = 0;
    wchar_t bestPath[MAX_PATH * 2];
    bestPath[0] = L'\0';

    wchar_t* context = NULL;
    for (wchar_t* code = wcstok_s(codes, L";,", &context); code != NULL; code = wcstok_s(NULL, L";,", &context))
    {
        while (*code == L' ')
        {
            ++code;
        }

        if (*code == L'\0')
        {
            continue;
        }

        ProductInfo info;
        if (!ReadProductInfo(code, &info) || !info.hasInstalledDirectory)
        {
            continue;
        }

        // 目录必须还在，且确实含我们的主程序 —— 否则这是一条指向已手工删除目录的旧记录。
        if (!DirectoryLooksLikeOurInstall(info.installedDirectory))
        {
            continue;
        }

        if (requireSameKind && info.kind != kAppKind)
        {
            continue;
        }

        // 「更值得选」的判据：同代优先，其次版本更高。
        bool better = false;
        if (!haveBest)
        {
            better = true;
        }
        else if ((info.kind == kAppKind) != (bestKind == kAppKind))
        {
            better = info.kind == kAppKind;
        }
        else if (info.version > bestVersion)
        {
            better = true;
        }

        if (better)
        {
            haveBest = true;
            bestKind = info.kind;
            bestVersion = info.version;
            wcsncpy_s(bestPath, ARRAYSIZE(bestPath), info.installedDirectory, _TRUNCATE);
        }
    }

    if (!haveBest)
    {
        return false;
    }

    wcsncpy_s(pathBuffer, pathCount, bestPath, _TRUNCATE);
    return true;
}

static bool ReadSessionProperty(MSIHANDLE session, const wchar_t* name, wchar_t* buffer, DWORD bufferCount)
{
    DWORD length = bufferCount;
    buffer[0] = L'\0';
    return MsiGetPropertyW(session, name, buffer, &length) == ERROR_SUCCESS && buffer[0] != L'\0';
}

// 汇总「值得去查实际安装路径」的产品码，写入以分号分隔的字符串。
//
// 为什么需要它：WIX_UPGRADE_DETECTED 只包含 UpgradeCode 与当前包相同的产品，而 1.0.0
// 当年的 UpgradeCode 与现在不同，该属性为空。所以必须能枚举全部已装产品。
//
// 为什么先筛：本机装了 429 个产品，逐个去读缓存 MSI 会拖慢安装界面。筛选依据是各产品
// **缓存 MSI 的 File 表里有没有 AutoLoginNet.exe / AutoLoginNet.App.exe**（早期版本还试过
// 用 Installer\Folders 或注册表里的安装位置，前者不区分产品、后者对本产品是空的，都不可靠）。
//
// 这里只做「是不是我们的产品」这一层筛选，刻意**不**按代排序：同代优先、版本最大的选择
// 已经在 TryFindPreviousInstallLocation 里做了，在这里排一遍等于白读一遍 MSI（踩过）。
static bool CollectCandidateProducts(wchar_t* buffer, DWORD bufferCount)
{
    buffer[0] = L'\0';

    ProductEnumerator products;
    for (;;)
    {
        wchar_t productCode[64];
        if (!products.Next(productCode, ARRAYSIZE(productCode)))
        {
            break;
        }

        ProductInfo info;
        if (!ReadProductInfo(productCode, &info))
        {
            continue;
        }

        size_t used = wcslen(buffer);
        if (used != 0)
        {
            if (used + 1 >= bufferCount)
            {
                break;
            }

            buffer[used++] = L';';
            buffer[used] = L'\0';
        }

        if (used + wcslen(productCode) >= bufferCount)
        {
            break;
        }

        wcsncpy_s(buffer + used, bufferCount - used, productCode, _TRUNCATE);
    }

    return buffer[0] != L'\0';
}

// 真正的实现。自定义动作的返回值只用来表示是否中止安装，
// 这里任何失败都在内部安静降级（退回默认安装目录），所以恒返回 ERROR_SUCCESS。
static UINT DetectCore(MSIHANDLE session)
{
    if (session == 0)
    {
        return ERROR_SUCCESS;
    }

    // 命令行显式指定目录时绝不覆盖用户的明确意图。
    wchar_t existing[MAX_PATH * 2];
    if (ReadSessionProperty(session, kExplicitTargetProperty, existing, ARRAYSIZE(existing)) ||
        ReadSessionProperty(session, kInstallFolderProperty, existing, ARRAYSIZE(existing)))
    {
        return ERROR_SUCCESS;
    }

    wchar_t previous[MAX_PATH * 2];

    // 两段解析：
    //   1) WIX_UPGRADE_DETECTED —— UpgradeCode 与当前包相同的旧产品，最可信；
    //   2) 枚举本机全部已装 MSI 产品 —— 覆盖旧版 UpgradeCode 与当前不同的情形。
    // 实测本机 1.0.0 的 UpgradeCode 与当前包不同，因此从 1.0.0 升级时第 1 段为空；
    // 早期版本正是在这里「属性为空就直接返回」，导致 1.0.0 → 新版永远装不回旧目录。
    //
    // 第 2 段不能退化成「扫受管目录」：那样既无法判断目录属于哪个产品，也拿不到
    // 版本号，只能靠目录名猜——而用户改过安装目录时名字根本对不上（本机 2.0.7 的
    // MSI 记 "AutoLoginNet"，实际装在 D:\autologin）。改为逐个产品问
    // MsiGetComponentPath，拿到的是货真价实的安装路径。
    //
    // 第 2 段的前置条件是「确实检测到了关联产品」。否则全新安装会去翻出机器上
    // 另一条产品线的目录并占用它 —— 那不是升级，是篡改（踩过）。
    wchar_t upgradeDetected[2048];
    upgradeDetected[0] = L'\0';
    bool hasRelatedProduct = ReadSessionProperty(session, L"WIX_UPGRADE_DETECTED",
                                                 upgradeDetected, ARRAYSIZE(upgradeDetected));
    if (!hasRelatedProduct)
    {
        return ERROR_SUCCESS;
    }

    if (TryFindPreviousInstallLocation(upgradeDetected, true, previous, ARRAYSIZE(previous)))
    {
        MsiSetPropertyW(session, kInstallFolderProperty, previous);
        return ERROR_SUCCESS;
    }

    wchar_t allProducts[8192];
    allProducts[0] = L'\0';
    if (CollectCandidateProducts(allProducts, ARRAYSIZE(allProducts)) &&
        TryFindPreviousInstallLocation(allProducts, false, previous, ARRAYSIZE(previous)))
    {
        MsiSetPropertyW(session, kInstallFolderProperty, previous);
    }

    return ERROR_SUCCESS;
}

extern "C" UINT __stdcall DetectPreviousInstallLocation(MSIHANDLE session)
{
    return DetectCore(session);
}

extern "C" UINT __stdcall DetectPreviousInstallLocationA(MSIHANDLE session)
{
    return DetectCore(session);
}

#ifdef ALN_SELFTEST

// ---------------------------------------------------------------------------
// 仅诊断构建使用的自检入口（用 /DALN_SELFTEST 编译时才有）。
//
// rundll32 要求入口是 void CALLBACK f(void) 形状（x64 上 __stdcall 与 __cdecl 等价），
// 并且必须显式导出 —— 不加 #pragma 的话链接器不会把它放进导出表。
#pragma comment(linker, "/EXPORT:AlnSelfTestDetect,PRIVATE")
//
// 为什么需要它：正式路径必须在 msiexec 会话里跑，需要管理员权限，难以在开发和
// CI 里反复验证；而「解析逻辑本身对不对」是可以在普通权限下验证的。
// 这个入口把本文件里真正的解析函数（TryFindPreviousInstallLocation 及其全部
// 内部实现）接到一个假的 MSIHANDLE 上跑一遍，因此验证的是**将要交付的那份代码**，
// 而不是另写一份 C#/PowerShell 复刻版 —— 复刻版正是上一轮 bug 藏身的地方
// （夹具用的是等值 SQL，实际交付的代码用的是被 msiexec 拒绝的 LIKE）。
//
// 输入通过环境变量传入，输出写到文件，避免依赖控制台（rundll32 下没有可靠 stdout）。
// 正式构建不带 ALN_SELFTEST，因此这个入口不会出现在交付的 DLL 里。
// ---------------------------------------------------------------------------
namespace
{
    void SelfTest(const wchar_t* label, const wchar_t* productCodes, bool requireSameKind)
    {
        wchar_t resolved[MAX_PATH * 2];
        resolved[0] = L'\0';

        bool ok = TryFindPreviousInstallLocation(productCodes, requireSameKind, resolved, ARRAYSIZE(resolved));

        wchar_t line[MAX_PATH * 4];
        swprintf_s(line, ARRAYSIZE(line), L"%s | codes=[%s] | sameKindOnly=%d | ok=%d | path=[%s]",
                   label, productCodes, requireSameKind ? 1 : 0, ok ? 1 : 0,
                   resolved[0] != L'\0' ? resolved : L"<empty>");
        SelfTestAppend(line);
    }
}

extern "C" void __cdecl AlnSelfTestDetect()
{
    wchar_t logPath[MAX_PATH * 2];
    if (GetEnvironmentVariableW(L"ALN_SELFTEST_LOG", logPath, ARRAYSIZE(logPath)) != 0)
    {
        DeleteFileW(logPath);
    }

    SelfTestAppend(L"version=6");

    // 第一段：调用方给的产品码（真实安装里就是 WIX_UPGRADE_DETECTED），只认同族。
    wchar_t codes[2048];
    bool hasRelatedProduct = false;
    if (GetEnvironmentVariableW(L"ALN_SELFTEST_CODES", codes, ARRAYSIZE(codes)) != 0 && codes[0] != L'\0')
    {
        hasRelatedProduct = true;
        SelfTest(L"lineage", codes, true);
    }
    else
    {
        SelfTestAppend(L"lineage | (WIX_UPGRADE_DETECTED 为空，正式代码在此跳过后续解析)");
        codes[0] = L'\0';
    }

    // 第二段：全量枚举本机已装产品后自己筛 —— 覆盖「旧版 UpgradeCode 与当前不同、
    // WIX_UPGRADE_DETECTED 里只有当前版本」这种从 1.x 升级上来的情形。允许异族兜底。
    wchar_t allProducts[8192];
    allProducts[0] = L'\0';
    if (CollectCandidateProducts(allProducts, ARRAYSIZE(allProducts)))
    {
        SelfTest(L"enumerated", allProducts, false);
    }
    else
    {
        SelfTestAppend(L"enumerated | 未找到任何本产品");
    }

    // 把 DetectCore 的**最终决策**复刻出来，而不是只报两段的中间结果。
    //
    // 为什么必须单独打这一行：真正生效的是「有没有关联产品」这道闸门 —— 全新安装时
    // 即使机器上存在别的产品线目录也必须什么都不选。若只断言两段的中间结果，
    // 这道闸门失效时自检照样全绿（踩过：夹具绿、功能从未生效）。
    wchar_t chosen[MAX_PATH * 2];
    chosen[0] = L'\0';
    const wchar_t* via = L"none";
    bool decided = false;
    if (hasRelatedProduct)
    {
        decided = TryFindPreviousInstallLocation(codes, true, chosen, ARRAYSIZE(chosen));
        if (decided)
        {
            via = L"lineage";
        }
        else if (CollectCandidateProducts(allProducts, ARRAYSIZE(allProducts)) &&
                 TryFindPreviousInstallLocation(allProducts, false, chosen, ARRAYSIZE(chosen)))
        {
            decided = true;
            via = L"enumerated";
        }
    }

    {
        wchar_t line[MAX_PATH * 4];
        swprintf_s(line, ARRAYSIZE(line), L"chosen | related=%d | ok=%d | via=%s | path=[%s]",
                   hasRelatedProduct ? 1 : 0, decided ? 1 : 0, via,
                   decided ? chosen : L"<empty>");
        SelfTestAppend(line);
    }
}

#endif // ALN_SELFTEST
