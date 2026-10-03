using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 测试辅助：创建 **目录联接（junction）**。
/// </summary>
/// <remarks>
/// 为什么不用 <c>Directory.CreateSymbolicLink</c>：创建符号链接需要
/// <c>SeCreateSymbolicLinkPrivilege</c>（通常只有管理员或开发者模式才有），
/// 普通用户会话里会直接抛「此操作需要管理员权限」，导致所有「跳过目录联接」的测试被静默跳过。
/// <para>而 **junction 不需要任何特权**，且同样带 <see cref="FileAttributes.ReparsePoint"/>，
/// 对被测代码来说与符号链接在「必须跳过」这一点上等价。</para>
/// <para>实现方式：<c>FSCTL_SET_REPARSE_POINT</c> + <c>IO_REPARSE_TAG_MOUNT_POINT</c> 重解析数据。</para>
/// </remarks>
internal static class JunctionHelper
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>
    /// 尝试创建目录联接。
    /// </summary>
    /// <param name="linkPath">要创建的联接路径（必须尚不存在）。</param>
    /// <param name="targetPath">联接指向的已存在目录。</param>
    /// <returns>成功返回 true；平台不支持或创建失败返回 false（调用方据此跳过用例，而不是失败）。</returns>
    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var linkFull = Path.GetFullPath(linkPath).TrimEnd(Path.DirectorySeparatorChar);
        var targetFull = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar);

        if (Directory.Exists(linkFull) || File.Exists(linkFull))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(linkFull);
        }
        catch
        {
            return false;
        }

        var handle = CreateFile(
            linkFull,
            GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == new IntPtr(-1))
        {
            TryRemoveDirectory(linkFull);
            return false;
        }

        try
        {
            var substitute = @"\??\" + targetFull;
            var printName = @"\??\" + targetFull;

            var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substitute);
            var printNameBytes = System.Text.Encoding.Unicode.GetBytes(printName);

            // 重解析数据布局：tag(4) + length(2) + reserved(2) + MountPoint 缓冲区
            // MountPoint 缓冲区：substituteOffset(2) + substituteLength(2) + printOffset(2) + printLength(2) + 路径(4 字节对齐)
            var mountPointSize = 8 + substituteBytes.Length + 2 + printNameBytes.Length + 2;
            var totalSize = 8 + mountPointSize;

            var buffer = new byte[totalSize];
            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)mountPointSize).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);

            // MountPoint 头部从 offset 8 开始
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                                  // SubstituteNameOffset
            BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);            // SubstituteNameLength
            BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12);      // PrintNameOffset
            BitConverter.GetBytes((ushort)printNameBytes.Length).CopyTo(buffer, 14);             // PrintNameLength

            substituteBytes.CopyTo(buffer, 16);
            // 16 + substituteBytes.Length 处已是 0（4 字节对齐的填充/终止）
            printNameBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);

            var ok = DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                (uint)buffer.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                TryRemoveDirectory(linkFull);
                return false;
            }

            return (new DirectoryInfo(linkFull).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            TryRemoveDirectory(linkFull);
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void TryRemoveDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
