using System.Runtime.InteropServices;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// 进程完整性级别检测。
/// </summary>
/// <remarks>
/// 背景：Windows 的 UIPI 会拒绝低完整性（Low IL）进程向中等完整性的资源管理器注册托盘图标，
/// <c>Shell_NotifyIcon</c> 只是静默失败——表现为“进程确实在运行，任务管理器里能看到，
/// 但通知区域里永远没有图标”，用户会误以为程序没启动。
/// 这里把这种情况检测出来，交给调用方给出明确提示。
/// </remarks>
internal static class ProcessIntegrity
{
    private const int TokenIntegrityLevel = 25;
    private const uint TokenQuery = 0x0008;

    /// <summary>中完整性（普通用户进程）对应的 RID。</summary>
    private const int MediumIntegrityRid = 0x2000;

    /// <summary>
    /// 当前进程是否运行在受限上下文（低于中完整性，例如沙箱或受限终端）。
    /// </summary>
    /// <param name="description">完整性级别的中文描述，用于提示用户。</param>
    public static bool IsRestricted(out string description)
    {
        description = string.Empty;

        var rid = GetCurrentProcessIntegrityRid();
        if (rid is null)
        {
            // 读不到就不打扰用户，按正常处理
            return false;
        }

        description = DescribeRid(rid.Value);
        return rid.Value < MediumIntegrityRid;
    }

    private static string DescribeRid(int rid) => rid switch
    {
        0x0000 => "Untrusted（不受信任）",
        0x1000 => "Low（低完整性，通常来自沙箱或受限终端）",
        0x2000 => "Medium（普通用户）",
        0x3000 => "High（管理员）",
        _ => $"0x{rid:X}"
    };

    private static int? GetCurrentProcessIntegrityRid()
    {
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return null;
            }

            // 第一次调用只用于探测所需缓冲区长度：此时 API 必然返回 FALSE（ERROR_INSUFFICIENT_BUFFER），
            // 因此不能把返回值当失败判断，只能看它回填的 length。
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL 的第一个字段是指向 SID 的指针，取 SID 最后一个子认证值作为完整性 RID
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return null;
            }

            var subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0)
            {
                return null;
            }

            return Marshal.ReadInt32(sid, 8 + ((subAuthorityCount - 1) * 4));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
