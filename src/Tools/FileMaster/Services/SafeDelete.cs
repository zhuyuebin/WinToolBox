using Microsoft.VisualBasic.FileIO;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 删除辅助：所有删除操作默认走「回收站」，只有显式要求时才永久删除。
/// </summary>
/// <remarks>
/// 使用 <see cref="FileSystem"/>（Microsoft.VisualBasic）实现回收站删除：
/// <c>UIOption.OnlyErrorDialogs</c> 表示只在出错时弹系统对话框，正常删除不打扰用户。
/// </remarks>
internal static class SafeDelete
{
    /// <summary>删除文件：<paramref name="useRecycleBin"/> 为 true 时移入回收站。</summary>
    public static void DeleteFile(string path, bool useRecycleBin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (useRecycleBin)
        {
            FileSystem.DeleteFile(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
            return;
        }

        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    /// <summary>删除目录（含内容）：<paramref name="useRecycleBin"/> 为 true 时移入回收站。</summary>
    /// <remarks>
    /// 这是无差别递归删除。清理空目录请改用 <see cref="DeleteEmptyDirectory"/>，
    /// 它只删空目录，天然不会连带删掉里面后来出现的文件。
    /// </remarks>
    public static void DeleteDirectory(string path, bool useRecycleBin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (useRecycleBin)
        {
            FileSystem.DeleteDirectory(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
            return;
        }

        ClearReadOnlyAttributes(path);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// 只删除「空目录」：<paramref name="useRecycleBin"/> 为 true 时移入回收站，否则永久删除。
    /// <para>两条路径都有非空保护：</para>
    /// <list type="bullet">
    /// <item>永久删除：<c>Directory.Delete(path, recursive: false)</c>，目录非空时直接抛
    /// <see cref="IOException"/>。</item>
    /// <item>回收站：<c>FileSystem.DeleteDirectory</c> 是递归语义，会把整个目录连同内容移入回收站，
    /// 因此必须先自行判空；非空时抛 <see cref="IOException"/>，不做任何移动。</item>
    /// </list>
    /// <para>残余风险（无法彻底消除）：从判空到实际删除之间存在极短的 TOCTOU 窗口，
    /// 此时新写入的文件仍可能被一并删除；回收站路径尤其如此（永久删除路径由
    /// <c>recursive: false</c> 兜底）。调用方在删除前应重新判空以缩小该窗口。</para>
    /// </summary>
    /// <exception cref="IOException">目录非空，或目录不存在。</exception>
    public static void DeleteEmptyDirectory(string path, bool useRecycleBin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 先判空：回收站路径是递归语义，必须在调用前自己挡住非空目录
        if (!EmptyFolderCleanerService.IsEmptyDirectory(path))
        {
            throw new IOException($"目录不是空目录（或已不可访问），拒绝删除：{path}");
        }

        if (useRecycleBin)
        {
            FileSystem.DeleteDirectory(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
            return;
        }

        // recursive: false —— 非空即抛异常，作为判空之后的第二道保险
        Directory.Delete(path, recursive: false);
    }

    /// <summary>递归清除只读属性（永久删除前调用，避免因只读属性失败）。</summary>
    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception)
            {
                // 单个文件处理失败不影响整体删除
            }
        }
    }
}
