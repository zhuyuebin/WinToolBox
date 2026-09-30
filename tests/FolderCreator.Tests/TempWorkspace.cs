namespace WinToolBox.FolderCreator.Tests;

/// <summary>
/// 测试用临时工作区：每次实例化创建一个独立的空目录，保证测试之间互不干扰、可重复执行、可并行。
/// 目录位置：{系统临时目录}\FolderCreatorTests\{GUID}\
/// 典型用法：<c>using var ws = new TempWorkspace();</c>
/// 本类只使用系统临时目录，不触碰真实 U 盘、网络路径或用户目录。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>临时工作区的父目录名（位于系统临时目录下）。</summary>
    private const string WorkspaceRootName = "FolderCreatorTests";

    /// <summary>创建本次测试独占的临时目录（目录本身会被立即创建）。</summary>
    public TempWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            WorkspaceRootName,
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);
    }

    /// <summary>本次测试独占的根目录。</summary>
    public string Root { get; }

    /// <summary>把相对于 <see cref="Root"/> 的路径转换为绝对路径（不创建任何目录）。</summary>
    public string PathOf(string relativePath)
        => Path.GetFullPath(Path.Combine(Root, relativePath));

    /// <summary>在根目录下创建目录（含缺失的父级），返回该目录的绝对路径。</summary>
    public string CreateDirectory(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var fullPath = PathOf(relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    /// <summary>判断根目录下的相对路径是否存在（目录）。</summary>
    public bool Exists(string relativePath) => Directory.Exists(PathOf(relativePath));

    /// <summary>
    /// 枚举根目录下所有层级的子目录，返回相对 <see cref="Root"/> 的路径。
    /// 结果按序号排序，保证断言所需的确定性（文件系统枚举顺序本身不稳定）。
    /// </summary>
    public IReadOnlyList<string> EnumerateDirectories()
    {
        if (!Directory.Exists(Root))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>尽力递归删除临时目录：失败不影响测试结果，也绝不向外抛异常。</summary>
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 清理失败（例如文件仍被占用）不应让测试失败，也不应覆盖真正的断言失败信息
        }
    }
}
