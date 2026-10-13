using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoLoginNet.Tests.Infrastructure;

/// <summary>
/// 读取文件的「文件身份」——同一个文件无论被改写多少次，FileIndex 都不变；
/// 一旦被「删除+改名」替换成另一个文件，FileIndex 就会改变。
/// <para>
/// 用来给 hosts 写入方式上锁：必须原地改写，不能走同目录改名替换——
/// 后者会被安全软件 HIPS 驱动拦成 Access denied（火绒实测）。
/// </para>
/// </summary>
internal static class FileIdentity
{
    /// <summary>取 NTFS 文件身份；非 Windows 平台返回 null。</summary>
    public static (ulong Volume, ulong Index)? TryGet(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = Native.CreateFileW(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        if (!Native.GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            return null;
        }

        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);
    }
}
