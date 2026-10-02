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
