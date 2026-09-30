namespace WinToolBox.Core;

/// <summary>
/// 复制排除规则：默认排除系统目录、autorun.inf 等，可按后缀扩展。
/// 所有比较均忽略大小写。
/// </summary>
public sealed class ExcludeRules
{
    private readonly HashSet<string> _directories;
    private readonly HashSet<string> _files;
    private readonly HashSet<string> _extensions;

    /// <summary>默认排除的目录名。</summary>
    public static readonly string[] DefaultDirectoryNames =
    {
        "System Volume Information",
        "$RECYCLE.BIN",
        "$Recycle.Bin",
        "found.000",
        ".Trash",
        ".Trashes"
    };

    /// <summary>默认排除的文件名。</summary>
    public static readonly string[] DefaultFileNames =
    {
        "autorun.inf",
        "desktop.ini",
        "Thumbs.db"
    };

    /// <summary>默认排除的文件后缀。</summary>
    public static readonly string[] DefaultExtensions =
    {
        ".tmp",
        ".part",
        ".crdownload"
    };

    /// <summary>创建规则集合。</summary>
    public ExcludeRules(
        IEnumerable<string>? directoryNames = null,
        IEnumerable<string>? fileNames = null,
        IEnumerable<string>? extensions = null)
    {
        _directories = ToSet(directoryNames);
        _files = ToSet(fileNames);
        _extensions = ToSet(extensions?.Select(NormalizeExtension));
    }

    /// <summary>排除的目录名（只读视图）。</summary>
    public IReadOnlyCollection<string> ExcludedDirectoryNames => _directories;

    /// <summary>排除的文件名（只读视图）。</summary>
    public IReadOnlyCollection<string> ExcludedFileNames => _files;

    /// <summary>排除的文件后缀（只读视图，均为小写且以 '.' 开头）。</summary>
    public IReadOnlyCollection<string> ExcludedExtensions => _extensions;

    /// <summary>默认规则（安全基线：autorun.inf / System Volume Information / $RECYCLE.BIN）。</summary>
    public static ExcludeRules CreateDefault(IEnumerable<string>? extraExtensions = null)
    {
        var extensions = new List<string>(DefaultExtensions);
        if (extraExtensions is not null)
        {
            extensions.AddRange(extraExtensions);
        }

        return new ExcludeRules(DefaultDirectoryNames, DefaultFileNames, extensions);
    }

    /// <summary>目录是否应被排除。</summary>
    public bool IsExcludedDirectory(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return false;
        }

        return _directories.Contains(directoryName.Trim().TrimEnd('\\', '/'));
    }

    /// <summary>文件是否应被排除（按文件名或后缀）。</summary>
    public bool IsExcludedFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName.Trim());
        if (_files.Contains(name))
        {
            return true;
        }

        var extension = Path.GetExtension(name);
        return !string.IsNullOrEmpty(extension) && _extensions.Contains(extension.ToLowerInvariant());
    }

    private static HashSet<string> ToSet(IEnumerable<string>? values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return set;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Trim());
            }
        }

        return set;
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return value;
        }

        return value.StartsWith('.') ? value.ToLowerInvariant() : "." + value.ToLowerInvariant();
    }
}
