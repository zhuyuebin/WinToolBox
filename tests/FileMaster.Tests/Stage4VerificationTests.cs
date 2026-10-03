using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 阶段四独立验证（verifier4）：不依赖任何既有用例的结论，直接对产品代码做对抗性攻击。
/// <list type="number">
/// <item><b>P1-12</b>：规则第三列（自由文本）必须无法把文件写到目标根目录之外 —— 逐个尝试
/// <c>..</c>、绝对路径、<c>\\?\</c>、UNC、<c>/</c>、非法字符、超长路径、纯空白、<c>\</c> 开头等输入，
/// 并检查「执行后整个工作区里有没有文件落在目标根之外」这一最终事实。</item>
/// <item><b>最终安全断言</b>：任何 <c>CanApply=true</c> 的项，其 <c>TargetPath</c> 必须落在目标根之内。</item>
/// <item><b>P1-19</b>：源 / 目标互相嵌套必须拒绝，同时不得误拒合法场景（兄弟目录、同名前缀兄弟、UNC、跨盘驱动器根）。</item>
/// <item><b>P1-10</b>：占用查询的截断计数必须真实（刚好等于上限 / 超限 1 个 / 含子目录）。</item>
/// <item><b>P1-11</b>：扫描目录归一化不得产生「自重复组」，也不得误合并同名前缀兄弟目录。</item>
/// </list>
/// </summary>
/// <remarks>
/// 本文件只做验证，不修改任何产品代码与既有测试。
/// 部分用例会把「实测到的行为矩阵」写到临时目录下的 txt 证据文件（不影响断言，仅用于向 Lead 汇报数字）。
/// </remarks>
public sealed class Stage4VerificationTests
{
    // ==================================================================
    // 公共辅助
    // ==================================================================

    private static readonly object EvidenceLock = new();

    /// <summary>把实测结果追加到临时目录下的证据文件（写失败不影响测试结论）。</summary>
    private static void RecordEvidence(string fileName, string line)
    {
        try
        {
            lock (EvidenceLock)
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), fileName),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // 证据文件只是给人看的，失败不应让断言失败
        }
    }

    /// <summary>清空证据文件（由拥有该文件的用例在开头调用）。</summary>
    private static void ResetEvidence(string fileName)
    {
        try
        {
            lock (EvidenceLock)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), fileName), string.Empty);
            }
        }
        catch
        {
            // 同上
        }
    }

    private static string CreateFile(TempWorkspace ws, string relativePath, string content = "sample")
    {
        var fullPath = ws.PathOf(relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>严格判断：path 是否位于 root 之内（root 本身不算）。</summary>
    private static bool IsStrictlyUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Path.GetFullPath(path).StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 稳健地枚举目录下所有文件：手写遍历，单个子目录不可访问时不影响其它子目录。
    /// </summary>
    /// <remarks>
    /// 不能用 <c>Directory.EnumerateFiles(..., AllDirectories)</c>：它在遇到任何一个无法枚举的子目录时整体抛异常，
    /// 一旦被 try/catch 吞掉就会返回空集合，让「没有越界文件」的断言变成空跑（假绿）。
    /// </remarks>
    private static IReadOnlyList<string> AllFilesUnder(string directory)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(directory))
        {
            pending.Push(directory);
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            try
            {
                results.AddRange(Directory.GetFiles(current));
            }
            catch
            {
                // 单个目录读不了不影响其它目录
            }

            try
            {
                foreach (var child in Directory.GetDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch
            {
                // 同上
            }
        }

        return results;
    }

    /// <summary>列出目录下一级的子目录全路径（读不了返回空）。</summary>
    private static IReadOnlyList<string> TopLevelDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>列出目录下一级的全部条目（文件 + 目录，读不了返回空）。</summary>
    private static IReadOnlyList<string> TopLevelEntries(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>两个路径是否指向同一位置（忽略大小写与尾分隔符）。</summary>
    private static bool IsSamePathAs(string pathA, string pathB)
        => string.Equals(
            Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>把一次枚举尝试的结果描述成字符串（用于诊断证据）。</summary>
    private static string DescribeEnumeration(string directory, bool directories)
    {
        try
        {
            var entries = directories ? Directory.GetDirectories(directory) : Directory.GetFiles(directory);
            return "ok[" + string.Join(",", entries.Select(Path.GetFileName)) + "]";
        }
        catch (Exception ex)
        {
            return "throw:" + ex.GetType().Name;
        }
    }

    /// <summary>一条「其它 → 指定子目录」的兜底规则。</summary>
    private static ArchiveOptions OptionsWithSubDirectory(
        string source,
        string target,
        string subDirectory,
        ArchiveAction action = ArchiveAction.Move)
        => new()
        {
            SourceDirectory = source,
            TargetDirectory = target,
            IncludeSubDirectories = true,
            Action = action,
            Overwrite = false,
            Rules = new[]
            {
                new ArchiveRule
                {
                    Kind = ArchiveRuleKind.Fallback,
                    TargetSubDirectory = subDirectory,
                    SourceText = "其它||" + subDirectory
                }
            }
        };

    /// <summary>
    /// 明确的越界输入：解析后必然落在目标根之外，必须被 <c>CanApply=false</c> + 空 TargetPath 拒绝。
    /// </summary>
    public static TheoryData<string> EscapingSubDirectories => new()
    {
        "..",
        @"..\..\Windows",
        @"..\..\..\..\..\Windows",
        "/etc",
        @"C:\Windows",
        @"\\?\D:\x",
        @"\\server\share",
        @"..\..\target",              // 逃出去再绕回来（绕到的已不是目标根）
        @"images\..\..\outside",      // 中间夹带 ..
        @"\rooted",                   // 以分隔符开头 = 当前盘根下的路径
        @"\",                         // 只有分隔符
        @"..\..\..\..\..\..\..\..\Windows",
    };

    /// <summary>
    /// 语义模糊但必须仍然安全的输入：可能被接受（字符串上确实在根之内），
    /// 但无论如何都不得产生「目标根之外的文件」。
    /// </summary>
    public static TheoryData<string> AmbiguousSubDirectories => new()
    {
        "....//",                     // 点号目录名 + 空段
        "   ",                        // 纯空白
        "a<b",                        // 非法字符
        "a\"b",                       // 非法字符（引号）
        new string('L', 300),         // 超长路径
        @".. \outside",               // Win32 会去掉尾随空格/点 → 若被去掉就是 ..（逃逸候选）
        @"...\outside",               // 尾随点 → 若被去掉就是空段
        @".. .\outside",              // 尾随「点+空格」
        @"images\..\docs",            // 合法的「中间 .. 后仍在根内」
        @"images\sub",                // 完全合法的嵌套
        @"图片\2026\10",              // 中文合法路径
        @" \outside",                 // 纯空格中间段（会被 Win32 丢掉）
        @". \outside",                // 「点 + 空格」段
        @"images\.. \outside",        // 中间段是「.. 」——越界候选
        "图片 ",                      // 合法名字 + 尾随空格（GetFullPath 会去掉，仍应落在根内）
    };

    // ==================================================================
    // P1-12：逃逸矩阵（明确的越界输入）
    // ==================================================================

    /// <summary>
    /// 对每个明确越界的第三列：预览必须拒绝、TargetPath 必须为空、执行后
    /// <b>工作区里绝不能出现任何落在目标根之外的文件</b>，且源文件必须原封不动（移动语义下不得被搬走）。
    /// </summary>
    [Theory]
    [MemberData(nameof(EscapingSubDirectories))]
    public void P1_12_EscapingSubDirectory_IsRejected_AndNothingIsWrittenOutside(string subDirectory)
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var sourceFile = CreateFile(ws, Path.Combine("source", "a.txt"), "payload");

        var options = OptionsWithSubDirectory(source, target, subDirectory);
        var service = new AutoArchiverService();

        var plan = service.BuildPlan(options);
        Assert.Null(plan.Error);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply, $"[{subDirectory}] 越界子目录必须被拒绝");
        Assert.Equal(string.Empty, item.TargetPath);
        Assert.Contains("目标目录之外", item.Message);

        // 执行一遍：即使预览被拒绝，也确认 Apply 不会用旧计划中的 TargetPath 做任何事
        var result = service.Apply(plan, options);
        Assert.Equal(0, result.SucceededCount);

        // 最终事实：工作区里除了源目录之外，不能再有任何文件（目标目录里也不该有）
        Assert.True(File.Exists(sourceFile), "源文件必须原封不动");
        Assert.Empty(AllFilesUnder(target));

        var workspaceFiles = AllFilesUnder(ws.Root);
        Assert.True(workspaceFiles.Count > 0, "工作区文件枚举为空 → 越界检查会变成空跑（假绿）");

        foreach (var file in workspaceFiles)
        {
            Assert.True(
                IsStrictlyUnder(file, source) || IsStrictlyUnder(file, target),
                $"[{subDirectory}] 出现了既不在源目录也不在目标目录的文件：{file}");
        }

        RecordEvidence(
            "stage4-verifier4-escape-matrix.txt",
            $"ESCAPE sub=[{subDirectory}] canApply={item.CanApply} targetPath=[] msg={item.Message}");
    }

    // ==================================================================
    // P1-12：语义模糊输入 + 最终安全断言
    // ==================================================================

    /// <summary>
    /// 对每个模糊输入：<c>CanApply=true</c> 时 TargetPath 必须在目标根之内、<c>CanApply=false</c> 时
    /// TargetPath 必须为空；执行后工作区里不得出现「既不在源目录也不在目标目录」的文件。
    /// </summary>
    [Theory]
    [MemberData(nameof(AmbiguousSubDirectories))]
    public void P1_12_AmbiguousSubDirectory_NeverProducesTargetPathOutsideRoot(string subDirectory)
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "a.txt"), "payload");

        var options = OptionsWithSubDirectory(source, target, subDirectory);
        var service = new AutoArchiverService();

        var plan = service.BuildPlan(options);
        Assert.Null(plan.Error);

        var item = Assert.Single(plan.Items);

        if (item.CanApply)
        {
            Assert.True(
                IsStrictlyUnder(item.TargetPath, target),
                $"[{subDirectory}] CanApply=true 但 TargetPath 落到了目标根之外：{item.TargetPath}");
        }
        else
        {
            Assert.Equal(string.Empty, item.TargetPath);
        }

        var result = service.Apply(plan, options);

        // 非空跑保证：执行成功则目标文件必须真的存在，执行失败则源文件必须还在（不能数据丢失）
        if (result.SucceededCount > 0)
        {
            Assert.True(File.Exists(item.TargetPath), $"[{subDirectory}] 报告成功但目标路径上没有文件：{item.TargetPath}");
        }
        else
        {
            Assert.True(File.Exists(ws.PathOf(Path.Combine("source", "a.txt"))),
                $"[{subDirectory}] 执行失败但源文件也消失了（数据丢失）");
        }

        // 主判据（不依赖递归枚举）：工作区顶层只允许出现 source / target 这两个条目。
        // 这种输入的逃逸距离最多一层（逃出去就落在工作区顶层），因此该判据足以发现真实逃逸；
        // 而递归枚举可能因为目录名怪异（例如字面量 ".. "）而抛异常，不能作为唯一依据。
        var topLevel = TopLevelEntries(ws.Root);
        var unexpected = topLevel
            .Where(entry => !IsSamePathAs(entry, source) && !IsSamePathAs(entry, target))
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            $"[{subDirectory}] 工作区顶层出现了源 / 目标之外的条目（真实逃逸）：{string.Join(" ; ", unexpected)}");

        // 辅助判据：递归遍历到的文件（能遍历到的部分）也必须在源 / 目标之内
        foreach (var file in AllFilesUnder(ws.Root))
        {
            Assert.True(
                IsStrictlyUnder(file, source) || IsStrictlyUnder(file, target),
                $"[{subDirectory}] 出现了越界文件：{file}");
        }

        foreach (var resultItem in result.Items.Where(static r => r.Success))
        {
            Assert.True(
                IsStrictlyUnder(resultItem.TargetPath, target),
                $"[{subDirectory}] 执行成功的项写到了目标根之外：{resultItem.TargetPath}");
        }

        RecordEvidence(
            "stage4-verifier4-ambiguous-matrix.txt",
            $"AMBIGUOUS sub=[{subDirectory}] canApply={item.CanApply} targetPath=[{item.TargetPath}] " +
            $"applyOk={result.SucceededCount} applyFail={result.FailedCount} topLevel=[{string.Join(" ; ", topLevel.Select(Path.GetFileName))}] msg={item.Message}");
    }

    /// <summary>
    /// 落地诊断：对「尾随空格 / 点的 <c>..</c> 变体」这类依赖 Win32 路径归一化的输入，
    /// 记录文件<b>真正</b>落在哪里（工作区递归遍历 + 显式候选路径 + 顶层目录名），
    /// 用来确认字符串层面「仍在目标根之内」是否等于文件系统层面也在其内。
    /// </summary>
    [Fact]
    public void P1_12_TrailingSpaceDotDot_LandingDiagnostics()
    {
        var inputs = new[] { @".. \outside", @".. .\outside", @"...\outside", "....//", "   " };

        foreach (var subDirectory in inputs)
        {
            using var ws = new TempWorkspace();
            var source = ws.CreateDirectory("source");
            var target = ws.CreateDirectory("target");
            CreateFile(ws, Path.Combine("source", "a.txt"), "payload");

            var options = OptionsWithSubDirectory(source, target, subDirectory);
            var service = new AutoArchiverService();
            var plan = service.BuildPlan(options);
            var apply = service.Apply(plan, options);

            var walked = AllFilesUnder(ws.Root)
                .Select(file => Path.GetRelativePath(ws.Root, file))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

            var topDirectories = TopLevelDirectories(ws.Root)
                .Select(Path.GetFileName)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

            // 显式探测「若 Win32 把 ".. " 归一化成 ".." 时会落到的位置」
            var wouldEscapeTo = Path.Combine(ws.Root, "outside", "a.txt");
            var literalTarget = Path.Combine(target, ".. ", "outside", "a.txt");

            // 记录该目录是否还能被正常枚举（用来判断归档后目标目录本身是否被搞坏）
            var targetDirsEnumeration = DescribeEnumeration(target, directories: true);
            var targetFilesEnumeration = DescribeEnumeration(target, directories: false);

            RecordEvidence(
                "stage4-verifier4-landing.txt",
                $"sub=[{subDirectory}] targetPath=[{plan.Items[0].TargetPath}] " +
                $"applyOk={apply.SucceededCount} applyFail={apply.FailedCount} " +
                $"walked=[{string.Join(" ; ", walked)}] topDirs=[{string.Join(" ; ", topDirectories)}] " +
                $"existsAtEscapedCandidate={File.Exists(wouldEscapeTo)} existsAtLiteralTarget={File.Exists(literalTarget)} " +
                $"targetSubDirs={targetDirsEnumeration} targetFiles={targetFilesEnumeration} " +
                $"topEntries=[{string.Join(" ; ", TopLevelEntries(ws.Root).Select(Path.GetFileName))}]");

            // 主不变量（与枚举是否报错无关）：工作区顶层只允许有 source / target
            var unexpected = TopLevelEntries(ws.Root)
                .Where(entry => !IsSamePathAs(entry, source) && !IsSamePathAs(entry, target))
                .ToArray();
            Assert.True(
                unexpected.Length == 0,
                $"[{subDirectory}] 文件真正落到了源 / 目标之外：{string.Join(" ; ", unexpected)}");

            // 正向确认：文件确实落在目标根之内（不能因为什么都没发生而「安全」）
            if (apply.SucceededCount > 0)
            {
                Assert.True(IsStrictlyUnder(plan.Items[0].TargetPath, target));
                Assert.True(
                    File.Exists(plan.Items[0].TargetPath),
                    $"[{subDirectory}] 报告成功但目标路径不存在：{plan.Items[0].TargetPath}");
            }
            else
            {
                Assert.True(
                    File.Exists(Path.Combine(source, "a.txt")),
                    $"[{subDirectory}] 执行失败但源文件也不见了（数据丢失）");
            }
        }

        // 固定几类「落地形态」，避免这些结论只停留在证据文件里。
        // 契约（Lead 修复后）：
        //   a) 段名 trim 掉末尾空格 / 点后会变成 ".." 的输入（".. \outside"、".. .\outside"）
        //      以及会被整段丢掉的输入（"...\outside"、"....//"）→ 一律拒绝；
        //   b) 纯空白 "   " → GetFullPath 会把它折掉、等价于目标根本身，无害 → 接受并落在目标根。
        var rejectedInputs = new[] { @".. \outside", @".. .\outside", @"...\outside", @"....//" };
        foreach (var subDirectory in rejectedInputs)
        {
            using var ws = new TempWorkspace();
            var source = ws.CreateDirectory("source");
            var target = ws.CreateDirectory("target");
            var sourceFile = CreateFile(ws, Path.Combine("source", "a.txt"), "payload");
            var options = OptionsWithSubDirectory(source, target, subDirectory);
            var service = new AutoArchiverService();
            var plan = service.BuildPlan(options);

            Assert.False(plan.Items[0].CanApply, $"[{subDirectory}] 会被 Win32 重新解释的子目录必须被拒绝");
            Assert.Equal(string.Empty, plan.Items[0].TargetPath);
            Assert.Contains("目标目录之外", plan.Items[0].Message);

            var apply = service.Apply(plan, options);
            Assert.Equal(0, apply.SucceededCount);
            Assert.True(File.Exists(sourceFile), $"[{subDirectory}] 源文件必须原封不动");
            Assert.Empty(
                TopLevelEntries(ws.Root).Where(entry => !IsSamePathAs(entry, source) && !IsSamePathAs(entry, target)));
        }

        // b) 纯空白：等价于目标根，文件应直接落在 target\a.txt（既没有越界，也没有改变层级）
        using (var ws = new TempWorkspace())
        {
            var source = ws.CreateDirectory("source");
            var target = ws.CreateDirectory("target");
            var sourceFile = CreateFile(ws, Path.Combine("source", "a.txt"), "payload");
            var options = OptionsWithSubDirectory(source, target, "   ");
            var service = new AutoArchiverService();
            var plan = service.BuildPlan(options);

            Assert.True(plan.Items[0].CanApply, "纯空白子目录等价于目标根，应被接受（无害）");
            Assert.Equal(
                Path.Combine(Path.GetFullPath(target), "a.txt"),
                Path.GetFullPath(plan.Items[0].TargetPath));

            var apply = service.Apply(plan, options);
            Assert.Equal(1, apply.SucceededCount);
            Assert.True(File.Exists(Path.Combine(target, "a.txt")));
            Assert.False(File.Exists(sourceFile));
            Assert.Empty(
                TopLevelEntries(ws.Root).Where(entry => !IsSamePathAs(entry, source) && !IsSamePathAs(entry, target)));
        }
    }

    /// <summary>
    /// OS 层探针（绕过产品代码）：<c>".. "</c> 这种「点 + 尾随空格」的路径段，
    /// Win32 到底是当成字面量目录名，还是去掉空格后当成父目录引用。
    /// </summary>
    /// <remarks>
    /// 这条用例存在的意义：Lead 报告「<c>target\.. \outside</c> 实际会写到 target 之外」，
    /// 而 verifier4 之前的记录显示 <c>existsAtEscapedCandidate=False</c> 且工作区顶层只有 source/target。
    /// 这里用最原始的方式（<c>Directory.CreateDirectory</c> + <c>File.Move</c>）直接问操作系统，
    /// 并把两种解释下的落点都记录下来 —— 结论只认实测，不认推测。
    /// </remarks>
    [Fact]
    public void P1_12_OsLevelProbe_TrailingSpaceSegmentSemantics()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var sourceFile = CreateFile(ws, Path.Combine("source", "a.txt"), "payload");

        var probeDirectory = Path.Combine(target, ".. ", "outside");
        var probeFile = Path.Combine(probeDirectory, "a.txt");

        string createResult;
        try
        {
            Directory.CreateDirectory(probeDirectory);
            createResult = "ok";
        }
        catch (Exception ex)
        {
            createResult = "throw:" + ex.GetType().Name;
        }

        string moveResult;
        try
        {
            File.Move(sourceFile, probeFile);
            moveResult = "ok";
        }
        catch (Exception ex)
        {
            moveResult = "throw:" + ex.GetType().Name;
        }

        var escapedCandidate = Path.Combine(ws.Root, "outside", "a.txt");       // 若 ".. " 被当成 ".."
        var droppedCandidate = Path.Combine(target, "outside", "a.txt");        // 若 ".. " 被整段丢掉
        var literalCandidate = probeFile;                                      // 若 ".. " 是字面量目录名

        RecordEvidence(
            "stage4-verifier4-os-probe.txt",
            $"create={createResult} move={moveResult} " +
            $"existsLiteral={File.Exists(literalCandidate)} existsEscaped={File.Exists(escapedCandidate)} existsDropped={File.Exists(droppedCandidate)} " +
            $"topEntries(ws)=[{string.Join(" ; ", TopLevelEntries(ws.Root).Select(Path.GetFileName))}] " +
            $"subDirs(target)=[{string.Join(" ; ", TopLevelDirectories(target).Select(Path.GetFileName))}] " +
            $"walked=[{string.Join(" ; ", AllFilesUnder(ws.Root).Select(f => Path.GetRelativePath(ws.Root, f)))}]");

        // 唯一断言：无论 OS 怎么解释，都不允许在「源 / 目标」之外（即工作区顶层）出现新条目
        Assert.Empty(
            TopLevelEntries(ws.Root).Where(entry => !IsSamePathAs(entry, source) && !IsSamePathAs(entry, target)));
    }

    /// <summary>
    /// 最终安全断言（跨全部输入一次性验证）：用「一条扩展名规则对应一个攻击输入」的方式构造单个计划，
    /// 遍历所有条目断言 —— 任何 CanApply=true 的项，其 TargetPath 必须严格位于目标根之下。
    /// </summary>
    [Fact]
    public void P1_12_FinalSafetyAssertion_NoApplicableItemEscapesTargetRoot()
    {
        ResetEvidence("stage4-verifier4-final-assertion.txt");

        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");

        // TheoryData 有到 ImmutableArray 的隐式转换，这里显式按 IEnumerable<object[]> 取行
        var inputs = ((IEnumerable<object[]>)EscapingSubDirectories)
            .Select(static row => (string)row[0])
            .Concat(((IEnumerable<object[]>)AmbiguousSubDirectories).Select(static row => (string)row[0]))
            .ToArray();

        // 每个输入配一个「只匹配自己那个文件」的扩展名规则
        var rules = new List<ArchiveRule>();
        for (var index = 0; index < inputs.Length; index++)
        {
            CreateFile(ws, Path.Combine("source", $"f{index:D2}.x{index:D2}"), "payload");
            rules.Add(new ArchiveRule
            {
                Kind = ArchiveRuleKind.Extension,
                Extensions = $".x{index:D2}",
                TargetSubDirectory = inputs[index],
                SourceText = $".x{index:D2}||{inputs[index]}"
            });
        }

        var options = new ArchiveOptions
        {
            SourceDirectory = source,
            TargetDirectory = target,
            IncludeSubDirectories = false,
            Action = ArchiveAction.Move,
            Rules = rules
        };

        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.Equal(inputs.Length, plan.Items.Count);

        var targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        var violations = new List<string>();

        foreach (var item in plan.Items)
        {
            if (item.CanApply)
            {
                if (!IsStrictlyUnder(item.TargetPath, target))
                {
                    violations.Add($"[CanApply] {item.SourcePath} -> {item.TargetPath}");
                }
            }
            else
            {
                // 被拒绝的项不允许再给出任何「看似可用」的目标路径
                if (item.TargetPath.Length != 0 && !IsStrictlyUnder(item.TargetPath, target))
                {
                    violations.Add($"[Skipped但越界] {item.SourcePath} -> {item.TargetPath}");
                }
            }

            RecordEvidence(
                "stage4-verifier4-final-assertion.txt",
                $"canApply={item.CanApply} src={Path.GetFileName(item.SourcePath)} " +
                $"target=[{item.TargetPath}] underRoot={(item.TargetPath.Length == 0 ? "-" : IsStrictlyUnder(item.TargetPath, target).ToString())} " +
                $"msg={item.Message}");
        }

        Assert.True(violations.Count == 0, "存在越界的可执行项：" + string.Join(" | ", violations));

        // 执行后：全工作区的文件必须仍然只落在源目录或目标目录里
        var result = service.Apply(plan, options);
        foreach (var file in AllFilesUnder(ws.Root))
        {
            Assert.True(
                Path.GetFullPath(file).StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || IsStrictlyUnder(file, source),
                "执行后出现越界文件：" + file);
        }

        RecordEvidence(
            "stage4-verifier4-final-assertion.txt",
            $"SUMMARY ready={plan.ReadyCount} skipped={plan.SkippedCount} applyOk={result.SucceededCount} applyFail={result.FailedCount}");
    }

    /// <summary>
    /// 反向验证「拒绝」不是靠猜：合法嵌套子目录必须被接受，且文件真的落在目标根下。
    /// </summary>
    [Fact]
    public void P1_12_LegitimateSubDirectories_AreStillAccepted()
    {
        var legitimate = new[]
        {
            "images",
            @"images\2026",
            @"images\..\docs",
            @"图片\2026\10",
            "a b c",
            "文件夹.with.dots",
        };

        foreach (var subDirectory in legitimate)
        {
            using var ws = new TempWorkspace();
            var source = ws.CreateDirectory("source");
            var target = ws.CreateDirectory("target");
            CreateFile(ws, Path.Combine("source", "a.txt"), "payload");

            var options = OptionsWithSubDirectory(source, target, subDirectory);
            var service = new AutoArchiverService();

            var plan = service.BuildPlan(options);
            var item = Assert.Single(plan.Items);

            Assert.True(item.CanApply, $"[{subDirectory}] 合法子目录被误拒：{item.Message}");
            Assert.True(IsStrictlyUnder(item.TargetPath, target));

            var result = service.Apply(plan, options);
            Assert.Equal(1, result.SucceededCount);
            Assert.True(File.Exists(item.TargetPath), $"[{subDirectory}] 文件没有落到目标路径");
        }
    }

    // ==================================================================
    // P1-12：PathSafety 的边界（内部 API 直测）
    // ==================================================================

    /// <summary>TryResolveInside 的边界：空串 / null / 空白根 / 根自身 / 越界。</summary>
    [Fact]
    public void PathSafety_TryResolveInside_Boundaries()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("root");

        // 空子目录 = 目标根自身
        Assert.True(PathSafety.TryResolveInside(root, string.Empty, out var emptyResolved));
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), emptyResolved!.TrimEnd(Path.DirectorySeparatorChar));

        Assert.True(PathSafety.TryResolveInside(root, null!, out var nullResolved));
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), nullResolved!.TrimEnd(Path.DirectorySeparatorChar));

        // 根为空 / 空白：一律不可用
        Assert.False(PathSafety.TryResolveInside(string.Empty, "docs", out _));
        Assert.False(PathSafety.TryResolveInside("   ", "docs", out _));

        // 兄弟前缀不得被误判
        var data = ws.CreateDirectory("data");
        var database = ws.CreateDirectory("database");
        Assert.False(PathSafety.IsChildPath(database, data));
        Assert.False(PathSafety.IsChildPath(data, database));
        Assert.False(PathSafety.IsChildPath(ws.PathOf("data2"), data));

        // 驱动器根
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(data))!;
        Assert.True(PathSafety.IsChildPath(data, driveRoot));
        Assert.True(PathSafety.IsSameOrChildPath(driveRoot, driveRoot));
        Assert.True(PathSafety.IsSamePath(driveRoot, driveRoot + Path.DirectorySeparatorChar));

        // 「严格子路径」语义：路径根不能把自己当成子路径（否则会出现「自己吞掉自己」）
        Assert.False(PathSafety.IsChildPath(driveRoot, driveRoot));
        Assert.False(PathSafety.IsChildPath(driveRoot + Path.DirectorySeparatorChar, driveRoot));
        Assert.False(PathSafety.IsChildPath(data, data));
    }

    // ==================================================================
    // P1-19：嵌套矩阵
    // ==================================================================

    /// <summary>该拒的必须拒（源=目标、目标在源内、源在目标内，多种写法）。</summary>
    [Fact]
    public void P1_19_NestingMatrix_RejectsAllNestingForms()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var nested = ws.CreateDirectory(Path.Combine("data", "nested"));
        var deeper = ws.CreateDirectory(Path.Combine("data", "nested", "deeper"));

        var separator = Path.DirectorySeparatorChar;

        // 同一个目录的各种写法
        Assert.Contains("不能是同一个目录", AutoArchiverService.ValidateSourceAndTarget(data, data)!);
        Assert.Contains("不能是同一个目录", AutoArchiverService.ValidateSourceAndTarget(data + separator, data)!);
        Assert.Contains("不能是同一个目录", AutoArchiverService.ValidateSourceAndTarget(data, data.ToUpperInvariant())!);
        Assert.Contains("不能是同一个目录", AutoArchiverService.ValidateSourceAndTarget(data, Path.Combine(data, "..", "data"))!);
        Assert.Contains("不能是同一个目录", AutoArchiverService.ValidateSourceAndTarget(data, Path.Combine(data, ".", "."))!);

        // 目标在源内（1 层 / 2 层 / 用 .. 绕）
        Assert.Contains("目标目录不能位于源目录内部", AutoArchiverService.ValidateSourceAndTarget(data, nested)!);
        Assert.Contains("目标目录不能位于源目录内部", AutoArchiverService.ValidateSourceAndTarget(data, deeper)!);
        Assert.Contains("目标目录不能位于源目录内部", AutoArchiverService.ValidateSourceAndTarget(data, Path.Combine(nested, "..", "nested", "x"))!);

        // 源在目标内（1 层 / 2 层 / 尾分隔符）
        Assert.Contains("源目录不能位于目标目录内部", AutoArchiverService.ValidateSourceAndTarget(nested, data)!);
        Assert.Contains("源目录不能位于目标目录内部", AutoArchiverService.ValidateSourceAndTarget(deeper, data)!);
        Assert.Contains("源目录不能位于目标目录内部", AutoArchiverService.ValidateSourceAndTarget(deeper + separator, data + separator)!);
    }

    /// <summary>
    /// 误拒检查：合法场景（兄弟目录、同名前缀兄弟、跨盘驱动器根目标、UNC 路径数学）必须被放行；
    /// 同时把「同盘驱动器根当目标」的实测结论记录下来（实现选择：拒绝）。
    /// </summary>
    [Fact]
    public void P1_19_LegitimateScenarios_AreNotFalseRejected()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var database = ws.CreateDirectory("database");
        var sibling = ws.CreateDirectory("sibling");
        var data2 = ws.CreateDirectory("data2");

        // 兄弟目录（含同名前缀兄弟）必须放行
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data, sibling));
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data, database));
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(database, data));
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data, data2));
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data2, data));

        // 大小写/尾分隔符不同的兄弟
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data, database.ToUpperInvariant() + Path.DirectorySeparatorChar));

        // UNC：纯路径数学，不应因为跨机器写法而被拒
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(@"\\server\share\a", @"\\server\share\b"));
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(@"C:\local\a", @"\\server\share"));
        Assert.NotNull(AutoArchiverService.ValidateSourceAndTarget(@"\\server\share\a\b", @"\\server\share\a"));

        // 驱动器根当目标：跨盘合法
        var crossDriveTarget = Path.GetPathRoot(Path.GetFullPath(data))!.Equals(@"C:\", StringComparison.OrdinalIgnoreCase)
            ? @"D:\"
            : @"C:\";
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data, crossDriveTarget));

        // 同盘驱动器根当目标：目标包含源 → 实现（保守）拒绝，这里把事实固定下来
        var sameDriveRoot = Path.GetPathRoot(Path.GetFullPath(data))!;
        var sameDriveResult = AutoArchiverService.ValidateSourceAndTarget(data, sameDriveRoot);
        RecordEvidence(
            "stage4-verifier4-nesting.txt",
            $"sameDriveRoot source={data} target={sameDriveRoot} result={(sameDriveResult is null ? "ACCEPT" : "REJECT")}");
        Assert.NotNull(sameDriveResult);

        // 端到端：兄弟目录分类必须真的能跑通
        CreateFile(ws, Path.Combine("data", "a.txt"), "payload");
        var options = OptionsWithSubDirectory(data, database, "moved");
        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.ReadyCount);

        var result = service.Apply(plan, options);
        Assert.Equal(1, result.SucceededCount);
        Assert.True(File.Exists(plan.Items[0].TargetPath), "分类结果没有真正落盘：" + plan.Items[0].TargetPath);
    }

    /// <summary>
    /// 文案层的确定性验证（不受本沙箱 <c>RmStartSession</c> 失败影响）：
    /// 未截断时逐字保持原有文案，截断时绝不再输出「没有被任何进程占用」这种确定性结论。
    /// </summary>
    [Fact]
    public void P1_10_SummaryTexts_CompleteUnchanged_TruncatedExplicit()
    {
        // 未截断 + 无占用：逐字保持原样
        var complete = new FileLockQueryResult
        {
            Path = @"D:\data\report.txt",
            PathExists = true,
            ScannedFileCount = 1,
            TotalFileCount = 1
        };

        Assert.False(complete.Truncated);
        Assert.Equal("没有被任何进程占用。", complete.Summary);

        // 未截断 + 有占用：逐字保持原样
        var completeLocked = new FileLockQueryResult
        {
            Path = @"D:\data\report.txt",
            PathExists = true,
            ScannedFileCount = 1,
            TotalFileCount = 1,
            Processes = new List<FileLockProcess>
            {
                new() { ProcessId = 42, ProcessName = "notepad", AppType = LockingAppType.MainWindow }
            }
        };

        Assert.Equal("被 1 个进程占用：notepad.exe(42)", completeLocked.Summary);
        Assert.DoesNotContain("结果不完整", completeLocked.Summary);

        // 截断 + 无占用：必须说明检查了多少 / 共多少 / 结果不完整
        var truncated = new FileLockQueryResult
        {
            Path = @"D:\data",
            PathExists = true,
            IsDirectory = true,
            ScannedFileCount = 4096,
            TotalFileCount = 5000,
            Truncated = true
        };

        Assert.Equal(
            "已检查前 4096 个文件，未发现占用（该目录共 5000 个文件，结果不完整）。",
            truncated.Summary);
        Assert.DoesNotContain("没有被任何进程占用", truncated.Summary);

        // 截断 + 有占用：占用明细照常，同时注明结果不完整
        var truncatedLocked = new FileLockQueryResult
        {
            Path = @"D:\data",
            PathExists = true,
            IsDirectory = true,
            ScannedFileCount = 4096,
            TotalFileCount = 5000,
            Truncated = true,
            Processes = new List<FileLockProcess>
            {
                new() { ProcessId = 42, ProcessName = "notepad", AppType = LockingAppType.MainWindow }
            }
        };

        Assert.Contains("notepad.exe(42)", truncatedLocked.Summary);
        Assert.Contains("共 5000 个文件", truncatedLocked.Summary);
        Assert.Contains("结果不完整", truncatedLocked.Summary);

        // 截断状态 + 查询错误：错误优先，且复制出去的报告仍必须写明总数与不完整
        var errored = new FileLockQueryResult
        {
            Path = @"D:\data",
            PathExists = true,
            IsDirectory = true,
            ScannedFileCount = 4096,
            TotalFileCount = 5000,
            Truncated = true,
            Error = "RmStartSession 失败（错误码 29）。"
        };

        Assert.Equal("查询失败：RmStartSession 失败（错误码 29）。", errored.Summary);
        Assert.Contains("目录文件总数：5000（结果不完整）", FileUnlockerService.BuildReport(errored));
    }

    /// <summary>
    /// UNC 目标根与驱动器根作目标根时的路径判定（<see cref="PathSafety.TryResolveInside"/> 不访问文件系统，
    /// 因此 UNC 部分可以在没有网络的情况下验证字符串语义）。
    /// </summary>
    [Fact]
    public void PathSafety_TryResolveInside_HandlesUncAndDriveRoots()
    {
        // UNC 共享根作目标根
        Assert.True(PathSafety.TryResolveInside(@"\\server\share", "docs", out var uncInside));
        Assert.Equal(@"\\server\share\docs", uncInside);

        // 用 .. 试图越过共享根：无论 .NET 是把 .. 夹在共享根上（钳制）还是判为越界，
        // 都必须满足「要么拒绝、要么解析结果仍在共享根之内」这条安全不变量。
        foreach (var up in new[] { @"..\other", @"..\..\..\other" })
        {
            var accepted = PathSafety.TryResolveInside(@"\\server\share", up, out var resolvedUp);
            RecordEvidence(
                "stage4-verifier4-unc.txt",
                $"UNC root=[\\\\server\\share] sub=[{up}] accepted={accepted} resolved=[{resolvedUp}]");

            if (accepted)
            {
                Assert.True(
                    PathSafety.IsSameOrChildPath(resolvedUp!, @"\\server\share"),
                    $"[{up}] 解析结果逃出了共享根：{resolvedUp}");
            }
        }

        // 指向另一个共享的绝对 UNC 路径必须被拒
        Assert.False(PathSafety.TryResolveInside(@"\\server\share", @"\\server\other", out _));

        // 驱动器根作目标根：同盘路径都在里面，且 .. 不能越过盘根
        Assert.True(PathSafety.TryResolveInside(@"D:\", "docs", out var driveInside));
        Assert.Equal(@"D:\docs", driveInside);

        Assert.True(PathSafety.TryResolveInside(@"D:\", @"..\..\Windows", out var aboveRoot));
        Assert.StartsWith(@"D:\", aboveRoot, StringComparison.OrdinalIgnoreCase);
        Assert.False(PathSafety.TryResolveInside(@"D:\", @"C:\Windows", out _));
    }

    // ==================================================================
    // P1-10：占用查询截断（计数与文案）
    // ==================================================================

    /// <summary>刚好等于单次上限时必须<b>不</b>算截断，且总数等于实际上限。</summary>
    [Fact]
    public void P1_10_ExactlyAtLimit_IsNotTruncated()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("exact");
        var limit = FileUnlockerService.MaxFilesPerQuery;

        for (var index = 0; index < limit; index++)
        {
            File.WriteAllText(Path.Combine(directory, $"f{index:D5}.tmp"), string.Empty);
        }

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory);
        Assert.Equal(limit, result.TotalFileCount);
        Assert.Equal(limit, result.ScannedFileCount);
        Assert.False(result.Truncated, $"刚好 {limit} 个文件不应被判为截断");

        RecordEvidence(
            "stage4-verifier4-p110.txt",
            $"EXACT_LIMIT limit={limit} total={result.TotalFileCount} scanned={result.ScannedFileCount} truncated={result.Truncated} error={(result.Error ?? "<null>")}");
    }

    /// <summary>
    /// 超过上限 1 个：总数必须是不截断的真实值（含子目录里的文件），标记截断，
    /// 且该结论不得被「查询失败」掩盖（本沙箱 RmStartSession 返回 29，走 Error 分支）。
    /// </summary>
    [Fact]
    public void P1_10_LimitPlusOne_ReportsRealTotalAndTruncated()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("many");
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var limit = FileUnlockerService.MaxFilesPerQuery;

        // 128 个放在子目录里，用来验证 TotalFileCount 走的是递归总数（而不是只看顶层）
        for (var index = 0; index < 128; index++)
        {
            File.WriteAllText(Path.Combine(nested, $"n{index:D4}.tmp"), string.Empty);
        }

        for (var index = 0; index < limit + 1 - 128; index++)
        {
            File.WriteAllText(Path.Combine(root, $"f{index:D5}.tmp"), string.Empty);
        }

        var expectedTotal = limit + 1;
        var result = new FileUnlockerService().FindLockingProcesses(root);

        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory);
        Assert.Equal(expectedTotal, result.TotalFileCount);
        Assert.Equal(limit, result.ScannedFileCount);
        Assert.True(result.Truncated, "超过上限必须标记结果不完整");

        // 报告里必须出现真实总数（报告会被复制出去，不能误导）
        var report = FileUnlockerService.BuildReport(result);
        Assert.Contains($"目录文件总数：{expectedTotal}（结果不完整）", report);

        if (result.Error is null)
        {
            Assert.Contains($"已检查前 {limit} 个文件", result.Summary);
            Assert.Contains($"共 {expectedTotal} 个文件", result.Summary);
            Assert.Contains("结果不完整", result.Summary);
            Assert.DoesNotContain("没有被任何进程占用", result.Summary);
        }
        else
        {
            // 受限环境：错误优先，但截断事实必须已经被记录
            Assert.Contains("查询失败", result.Summary);
        }

        RecordEvidence(
            "stage4-verifier4-p110.txt",
            $"LIMIT_PLUS_ONE limit={limit} total={result.TotalFileCount} scanned={result.ScannedFileCount} " +
            $"truncated={result.Truncated} error={(result.Error ?? "<null>")} summary={result.Summary}");
    }

    /// <summary>未截断的小目录：文案必须保持原有语义（不能出现「结果不完整」）。</summary>
    [Fact]
    public void P1_10_SmallDirectory_KeepsOriginalSummarySemantics()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("small");
        CreateFile(ws, Path.Combine("small", "a.txt"));
        CreateFile(ws, Path.Combine("small", "nested", "b.txt"));

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        Assert.Equal(2, result.TotalFileCount);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.False(result.Truncated);
        Assert.DoesNotContain("结果不完整", result.Summary);

        if (result.Error is null)
        {
            Assert.Equal("没有被任何进程占用。", result.Summary);
        }

        // 未截断的报告不出现「目录文件总数」这一行
        Assert.DoesNotContain("目录文件总数", FileUnlockerService.BuildReport(result));
    }

    // ==================================================================
    // P1-11：重复查找目录归一化
    // ==================================================================

    /// <summary>
    /// 核心回归：父目录 + 其子目录同时给出时，子目录里的唯一文件只能被统计一次，
    /// <b>绝不能</b>与自己组成「自重复组」（修复前 ScannedFileCount=2 且出现 1 组重复）。
    /// </summary>
    [Fact]
    public void P1_11_ParentAndChild_ProduceNoSelfDuplicateGroup()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("data");
        var child = ws.CreateDirectory(Path.Combine("data", "sub"));
        CreateFile(ws, Path.Combine("data", "sub", "only.bin"), "unique-content-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { parent, child },
            IncludeSubDirectories = true
        });

        Assert.Null(result.Error);
        Assert.Equal(1, result.ScannedFileCount);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.DuplicateFileCount);
        Assert.Contains(child, result.MergedDirectories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>归一化与输入顺序无关：子目录写在前也必须被合并掉。</summary>
    [Fact]
    public void P1_11_ParentAndChild_OrderIndependent()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("data");
        var child = ws.CreateDirectory(Path.Combine("data", "sub"));

        // 父目录顶层与子目录各放一个内容相同的文件：合并后应得到 1 组（2 个不同物理文件），
        // 而不是「同一文件与自己重复」导致的更大组。
        CreateFile(ws, Path.Combine("data", "a.bin"), "same-content-0123456789");
        CreateFile(ws, Path.Combine("data", "sub", "b.bin"), "same-content-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { child, parent },
            IncludeSubDirectories = true
        });

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Single(result.Groups);
        Assert.Equal(2, result.Groups[0].Files.Count);
        Assert.Equal(2, result.Groups[0].Files.Select(static f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(child, result.MergedDirectories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 同名前缀兄弟目录（<c>data</c> / <c>database</c>）绝不能被误合并 ——
    /// 裸 StartsWith 会在这里把 database 吞掉，导致漏扫。
    /// </summary>
    [Fact]
    public void P1_11_PrefixSiblings_AreNotMerged()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var database = ws.CreateDirectory("database");
        var data2 = ws.CreateDirectory("data2");

        CreateFile(ws, Path.Combine("data", "a.bin"), "unique-a-0123456789");
        CreateFile(ws, Path.Combine("database", "b.bin"), "unique-b-0123456789");
        CreateFile(ws, Path.Combine("data2", "c.bin"), "unique-c-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { data, database, data2 },
            IncludeSubDirectories = true
        });

        Assert.Null(result.Error);
        Assert.Equal(3, result.ScannedFileCount);
        Assert.Empty(result.MergedDirectories);
        Assert.Empty(result.Groups);
    }

    /// <summary>
    /// IncludeSubDirectories=false 时不合并父子目录（否则顶层扫描会漏掉子目录里的文件）。
    /// </summary>
    [Fact]
    public void P1_11_TopLevelOnly_DoesNotMergeParentAndChild()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("p");
        var child = ws.CreateDirectory(Path.Combine("p", "c"));
        CreateFile(ws, Path.Combine("p", "top.bin"), "unique-top-0123456789");
        CreateFile(ws, Path.Combine("p", "c", "deep.bin"), "unique-deep-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { parent, child },
            IncludeSubDirectories = false
        });

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Empty(result.MergedDirectories);
    }

    /// <summary>同一目录的不同写法（大小写 / 尾分隔符 / “.”后缀）只应被扫描一次。</summary>
    [Fact]
    public void P1_11_EquivalentSpellings_AreScannedOnce()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("data");
        CreateFile(ws, Path.Combine("data", "a.bin"), "unique-0123456789");

        var spellings = new[]
        {
            directory,
            directory.ToUpperInvariant(),
            directory + Path.DirectorySeparatorChar,
            Path.Combine(directory, "."),
        };

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = spellings,
            IncludeSubDirectories = true
        });

        Assert.Null(result.Error);
        Assert.Equal(1, result.ScannedFileCount);

        // 四种写法指向同一个目录：按规范化路径去重后只保留一条「被合并」记录
        Assert.Single(result.MergedDirectories);
        Assert.Empty(result.Groups);
    }

    /// <summary>归一化不得误伤真实重复：不同子目录里的同内容文件仍然必须被发现。</summary>
    [Fact]
    public void P1_11_GenuineDuplicates_AreStillFound()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("data");
        CreateFile(ws, Path.Combine("data", "a.bin"), "duplicate-content-0123456789");
        CreateFile(ws, Path.Combine("data", "sub", "b.bin"), "duplicate-content-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { parent },
            IncludeSubDirectories = true
        });

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Single(result.Groups);
        Assert.Equal(2, result.Groups[0].Files.Count);
    }

    /// <summary>忽略名单语义不变：被忽略的文件不参与统计，也就不会因为「只扫到一个」而伪造重复组。</summary>
    [Fact]
    public void P1_11_ExcludeNames_BehaviourUnchanged()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("data");
        CreateFile(ws, Path.Combine("data", "keep.bin"), "duplicate-content-0123456789");
        CreateFile(ws, Path.Combine("data", "skip.bin"), "duplicate-content-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { parent },
            IncludeSubDirectories = true,
            ExcludeNames = "skip.bin"
        });

        Assert.Null(result.Error);
        Assert.Equal(1, result.ScannedFileCount);
        Assert.Empty(result.Groups);
    }

    /// <summary>父子目录同时给出时，候选文件计数不得因「同一文件被枚举两次」而虚增。</summary>
    [Fact]
    public void P1_11_ParentAndChild_FileCountIsNotDoubled()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("data");
        CreateFile(ws, Path.Combine("data", "a.bin"), "unique-a-0123456789");
        CreateFile(ws, Path.Combine("data", "sub", "b.bin"), "unique-b-0123456789");
        CreateFile(ws, Path.Combine("data", "sub", "deeper", "c.bin"), "unique-c-0123456789");

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { parent, Path.Combine(parent, "sub"), Path.Combine(parent, "sub", "deeper") },
            IncludeSubDirectories = true
        });

        Assert.Null(result.Error);
        Assert.Equal(3, result.ScannedFileCount);
        Assert.Equal(2, result.MergedDirectories.Count);
        Assert.Empty(result.Groups);
    }

    /// <summary>
    /// <b>未修复的缺陷</b>（verifier4 独立发现）：把驱动器根（或 UNC 共享根）作为一个扫描目录、
    /// 同时再给任何第二个目录时，根目录会被「自己」吞掉。
    /// </summary>
    /// <remarks>
    /// <para>根因：<c>TrimTrailingSeparator</c> 对路径根（<c>D:\</c>）保留尾分隔符，
    /// 而 <c>IsProperSubPathOf(path, parent)</c> 在 parent 以分隔符结尾时直接把 parent 当作前缀，
    /// 于是 <c>IsProperSubPathOf("D:\", "D:\")</c> 返回 true —— 根目录被判定为自己的严格子路径。</para>
    /// <para>后果：<c>NormalizeDirectories(new[] { "D:\", "D:\photos" }, true, ...)</c> 与
    /// <c>new[] { "D:\", "E:\" }</c> 都会返回<b>空</b>列表，<c>DuplicateFinderService.Find</c>
    /// 随即返回 <c>Error = "没有可扫描的目录（请确认目录存在）。"</c>，一个文件都不会扫描
    /// （修复前这两个场景都能正常扫描）。单个目录时因为 <c>kept.Count &lt; 2</c> 提前返回，所以看不出问题。</para>
    /// <para>这里断言「正确行为」：根目录必须保留、其下的子目录被合并。本机实测 kept 为空。</para>
    /// </remarks>
    [Fact]
    public void P1_11_DriveRootScanDirectory_IsNotSwallowedByItself()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(data))!;

        var kept = DuplicateFinderService.NormalizeDirectories(
            new[] { driveRoot, data },
            includeSubDirectories: true,
            out var merged);

        RecordEvidence(
            "stage4-verifier4-p111-defect.txt",
            $"DRIVE_ROOT driveRoot=[{driveRoot}] exists={Directory.Exists(driveRoot)} " +
            $"kept=[{string.Join(" | ", kept)}] merged=[{string.Join(" | ", merged)}]");

        Assert.True(
            kept.Count == 1 && string.Equals(kept[0], driveRoot, StringComparison.OrdinalIgnoreCase),
            $"驱动器根应当被保留、其子目录被合并；实测 kept=[{string.Join(" | ", kept)}]，merged=[{string.Join(" | ", merged)}]");

        Assert.Contains(data, merged, StringComparer.OrdinalIgnoreCase);

        // 只有一个根目录时（走 kept.Count < 2 的提前返回）也必须保留
        var singleRoot = DuplicateFinderService.NormalizeDirectories(
            new[] { driveRoot },
            includeSubDirectories: true,
            out var singleMerged);
        Assert.True(
            singleRoot.Count == 1 && string.Equals(singleRoot[0], driveRoot, StringComparison.OrdinalIgnoreCase),
            $"单个驱动器根被吞掉：kept=[{string.Join(" | ", singleRoot)}]");
        Assert.Empty(singleMerged);

        // 多个驱动器根同时给出：一个都不能被合并掉，否则 Find 会直接报「没有可扫描的目录」
        var otherRoot = new[] { @"C:\", @"D:\", @"E:\" }
            .First(root => Directory.Exists(root)
                           && !string.Equals(root, driveRoot, StringComparison.OrdinalIgnoreCase));

        var twoRoots = DuplicateFinderService.NormalizeDirectories(
            new[] { driveRoot, otherRoot },
            includeSubDirectories: true,
            out var twoRootsMerged);
        Assert.True(
            twoRoots.Count == 2,
            $"两个驱动器根不应互相合并：kept=[{string.Join(" | ", twoRoots)}] merged=[{string.Join(" | ", twoRootsMerged)}]");
        Assert.Empty(twoRootsMerged);
    }

    /// <summary>NormalizeDirectories 的边界：空输入 / 不存在 / 空白 / 父子不合并。</summary>
    [Fact]
    public void P1_11_NormalizeDirectories_Boundaries()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var nested = ws.CreateDirectory(Path.Combine("data", "nested"));

        // 不存在与空白项被忽略
        var kept = DuplicateFinderService.NormalizeDirectories(
            new[] { "   ", ws.PathOf("missing"), data },
            includeSubDirectories: true,
            out var merged);
        Assert.True(
            kept.Count == 1,
            $"kept=[{string.Join(" | ", kept)}] merged=[{string.Join(" | ", merged)}] dataExists={Directory.Exists(data)} blankExists={Directory.Exists("   ")}");
        Assert.Empty(merged);

        // 全部无效 → 空（调用方据此返回「没有可扫描的目录」）
        var none = DuplicateFinderService.NormalizeDirectories(
            new[] { "   ", ws.PathOf("missing") },
            includeSubDirectories: true,
            out _);
        Assert.Empty(none);

        // 驱动器根作为扫描目录：见 P1_11_DriveRootScanDirectory_IsNotSwallowedByItself
        // （该场景目前有缺陷，单独放在一个用例里，避免掩盖本用例的其它边界结论）

        // IncludeSubDirectories=false 时不合并父子
        var keptTopOnly = DuplicateFinderService.NormalizeDirectories(
            new[] { data, nested },
            includeSubDirectories: false,
            out var mergedTopOnly);
        Assert.Equal(2, keptTopOnly.Count);
        Assert.Empty(mergedTopOnly);

        // 公开的包含关系判定（UI 提示用）
        Assert.True(DuplicateFinderService.IsSameOrSubPathOf(nested, data));
        Assert.True(DuplicateFinderService.IsSameOrSubPathOf(data, data));
        Assert.False(DuplicateFinderService.IsSameOrSubPathOf(data, nested));
    }
}
