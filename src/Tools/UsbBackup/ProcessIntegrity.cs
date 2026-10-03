using System.Runtime.InteropServices;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// 进程完整性级别检测结果。
/// </summary>
internal enum IntegrityCheckResult
{
    /// <summary>中完整性或更高：托盘图标可以正常注册。</summary>
    Normal,

    /// <summary>低于中完整性（沙箱 / 受限终端）：Windows 会拒绝注册托盘图标。</summary>
    Restricted,

    /// <summary>
    /// 无法确定完整性级别（API 失败、结构异常等）。
    /// <para>旧实现把这种情况直接当成「正常」（fail-open），于是受限环境下用户什么提示都没有，
    /// 只能自己猜「为什么进程在跑却没有图标」。现在单独成一态，由调用方给出可操作的提示。</para>
    /// </summary>
    Unknown
}

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
    /// 由完整性 RID 判定三态（纯函数，便于单元测试覆盖「读不到」与「受限」两条分支）。
    /// </summary>
    /// <param name="rid">完整性 RID；null 表示读取失败（未知）。</param>
    /// <param name="description">完整性级别的中文描述。</param>
    /// <param name="failureReason">返回 <see cref="IntegrityCheckResult.Unknown"/> 时的原因。</param>
    public static IntegrityCheckResult Classify(int? rid, out string description, out string? failureReason)
    {
        failureReason = null;

        if (rid is null)
        {
            description = string.Empty;
            failureReason = "读取进程完整性级别失败（原因未知）";
            return IntegrityCheckResult.Unknown;
        }

        description = DescribeRid(rid.Value);

        return rid.Value < MediumIntegrityRid
            ? IntegrityCheckResult.Restricted
            : IntegrityCheckResult.Normal;
    }

    /// <summary>
    /// 检测当前进程的完整性级别（三态）。
    /// </summary>
    /// <param name="description">完整性级别的中文描述，用于提示用户。</param>
    /// <param name="failureReason">返回 <see cref="IntegrityCheckResult.Unknown"/> 时的原因（含 Win32 错误码）。</param>
    public static IntegrityCheckResult Check(out string description, out string? failureReason)
    {
        var rid = GetCurrentProcessIntegrityRid(out var error);
        var result = Classify(rid, out description, out var classifyReason);

        // 把真实的 P/Invoke 失败原因（含 Win32 错误码）带给调用方
        failureReason = result == IntegrityCheckResult.Unknown
            ? error ?? classifyReason
            : null;

        return result;
    }

    /// <summary>
    /// 当前进程是否运行在受限上下文（低于中完整性，例如沙箱或受限终端）。
    /// </summary>
    /// <param name="description">完整性级别的中文描述，用于提示用户。</param>
    /// <remarks>
    /// 保留此便捷重载以兼容既有调用点；读不到完整性级别时返回 false（不打扰用户），
    /// 需要区分「确定正常」与「读不到」时请改用 <see cref="Check"/>。
    /// </remarks>
    public static bool IsRestricted(out string description)
        => Check(out description, out _) == IntegrityCheckResult.Restricted;

    private static string DescribeRid(int rid) => rid switch
    {
        0x0000 => "Untrusted（不受信任）",
        0x1000 => "Low（低完整性，通常来自沙箱或受限终端）",
        0x2000 => "Medium（普通用户）",
        0x3000 => "High（管理员）",
        _ => $"0x{rid:X}"
    };

    /// <summary>
    /// 读取当前进程完整性 RID；失败时通过 <paramref name="failureReason"/> 给出**含 Win32 错误码**的原因。
    /// </summary>
    private static int? GetCurrentProcessIntegrityRid(out string? failureReason)
    {
        failureReason = null;
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                failureReason = $"OpenProcessToken 失败（Win32 错误码 {Marshal.GetLastWin32Error()}）";
                return null;
            }

            // 第一次调用只用于探测所需缓冲区长度：此时 API 必然返回 FALSE（ERROR_INSUFFICIENT_BUFFER），
            // 因此不能把返回值当失败判断，只能看它回填的 length。
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                failureReason =
                    $"GetTokenInformation 未能给出缓冲区长度（Win32 错误码 {Marshal.GetLastWin32Error()}）";
                return null;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
            {
                failureReason = $"GetTokenInformation 失败（Win32 错误码 {Marshal.GetLastWin32Error()}）";
                return null;
            }

            // TOKEN_MANDATORY_LABEL 的第一个字段是指向 SID 的指针，取 SID 最后一个子认证值作为完整性 RID
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                failureReason = "TOKEN_MANDATORY_LABEL 的 SID 指针为空";
                return null;
            }

            var subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0)
            {
                failureReason = "完整性 SID 的子认证值数量为 0";
                return null;
            }

            return Marshal.ReadInt32(sid, 8 + ((subAuthorityCount - 1) * 4));
        }
        catch (Exception ex)
        {
            failureReason = $"读取完整性级别时发生异常：{ex.GetType().Name} {ex.Message}";
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
