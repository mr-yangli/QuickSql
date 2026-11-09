using System.Diagnostics;

namespace QuickSql.Benchmarks;

/// <summary>门禁的度量原语。<b>为什么不用 BenchmarkDotNet 出判据</b>：BDN 一次跑要几分钟且要交叉验证环境，
/// 而 §12.8 的表是 CI 阻断项，必须"一条命令、几秒内、给红绿"。所以判据走这里的自测，
/// BDN 那一份是给人看前后对比的（PR 要附的数字）。两者跑的是同一批 <c>Func</c>，不会漂成两套代码。
/// <para>字节数用 <see cref="GC.GetAllocatedBytesForCurrentThread"/>：它是精确计数而不是采样，
/// 且不要求启用 Server GC 或关闭并发 GC。中位数取自 9 次（预热 25 次），
/// 因为分配计数几乎不该有抖动 —— <b>有抖动本身就是要看的信息</b>，所以不复用 BDN 的显著性 machinery。</para></summary>
static class Measuring
{
    /// <summary>比值的交替对数。判据要的是稳，报告要的是快，41 对约 0.3 秒。
    /// 这个数同时出现在 <c>--gate</c> 的输出文本里，所以它是常量而不是参数 —— 两处写死不叫一个口径。</summary>
    public const int RatioPairs = 41;

    public static long Bytes(Func<Task> run, int warmup = 25, int reps = 9)
        => BytesAsync(run, warmup, reps);

    public static long Bytes(Action run, int warmup = 25, int reps = 9)
        => Bytes(() => { run(); return Task.CompletedTask; }, warmup, reps);

    public static long BytesAsync(Func<Task> run, int warmup = 25, int reps = 9)
    {
        for (var i = 0; i < warmup; i++) run().GetAwaiter().GetResult();

        var samples = Sample(reps, () =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            run().GetAwaiter().GetResult();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        return samples[samples.Length / 2];
    }

    public static double Nanoseconds(Func<Task> run, int warmup = 25, int reps = 15)
    {
        for (var i = 0; i < warmup; i++) run().GetAwaiter().GetResult();

        var samples = Sample(reps, () =>
        {
            var started = Stopwatch.GetTimestamp();
            run().GetAwaiter().GetResult();
            return (double) Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        });

        return samples[samples.Length / 2];
    }

    public static double Nanoseconds(Action run, int warmup = 25, int reps = 15)
        => Nanoseconds(() => { run(); return Task.CompletedTask; }, warmup, reps);

    /// <summary>两点法拆 <c>y = 每查询 + 每行 × 行数</c>。
    /// 用两点而不是"总量 ÷ 行数"，因为后者的口径要求先知道驱动与 ADO 对象花掉多少 —— 而那正是本次要隔离的东西。
    /// 截距与斜率同一次实验里出来，两行的数就互相对得上。</summary>
    public static (double PerQuery, double PerRow) Split(Func<int, Task> atRows, int shortRun = 1, int longRun = 1_001)
    {
        var at0 = BytesAsync(() => atRows(shortRun));
        var at1 = BytesAsync(() => atRows(longRun));

        var perRow = (at1 - at0) / (double) (longRun - shortRun);

        return (at0 - perRow * shortRun, perRow);
    }

    /// <summary>交错采样后取<b>两侧中位数之比</b>。<b>为什么不是"每对比值再取中位"</b>：
    /// 单对样本各含一次随机停顿（GC、PGO 重编、CPU 频率），比值会把两边的抖动相加，
    /// 实测同一个场景连跑三次的头条数是 97%、65%、87% —— 阈值 90% 正好落在里面，红绿会随跑次翻转。
    /// 交错采集后各自取中位再相除，82 个样本都在为同一个数出力，同样连跑三次是 88%、87%、88%，稳得多。
    /// <para>全距（逐对最小/最大）要一起报出去：它量的是<b>一次运行内</b>的散布，与上面那种跨运行的稳定性互补 ——
    /// 只报比值就是把噪音藏进判据。</para></summary>
    public static (double Ratio, double MedianA, double MedianB, double Low, double High) Ratio(
        Func<Task> a, Func<Task> b, int warmup = 15, int pairs = RatioPairs)
    {
        for (var i = 0; i < warmup; i++)
        {
            a().GetAwaiter().GetResult();
            b().GetAwaiter().GetResult();
        }

        var nsA = new double[pairs];
        var nsB = new double[pairs];

        for (var i = 0; i < pairs; i++)
        {
            nsA[i] = Nanos(a);
            nsB[i] = Nanos(b);
        }

        var low = double.MaxValue;
        var high = 0d;

        for (var i = 0; i < pairs; i++)
        {
            low = Math.Min(low, nsB[i] / nsA[i]);
            high = Math.Max(high, nsB[i] / nsA[i]);
        }

        Array.Sort(nsA);
        Array.Sort(nsB);

        var medianA = nsA[pairs / 2];
        var medianB = nsB[pairs / 2];

        return (medianB / medianA, medianA, medianB, low, high);

        static double Nanos(Func<Task> run)
        {
            var started = Stopwatch.GetTimestamp();
            run().GetAwaiter().GetResult();
            return (double) Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        }
    }

    static long[] Sample<T>(int reps, Func<T> pick) where T : struct
    {
        Collect();

        var samples = new long[reps];

        for (var i = 0; i < reps; i++)
            samples[i] = Convert.ToInt64(pick()!);

        Array.Sort(samples);

        return samples;
    }

    static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

/// <summary>仓库根的定位：门禁里有几条是源码级断言（§12.8 的架构测试行、第七行的生成物），
/// 它们读的是文件不是程序集。手法与集成测试找连接串那份一致 —— 从运行目录往上找 <c>QuickSql.slnx</c>。</summary>
static class Repo
{
    public static string Root { get; } = Find() ?? throw new InvalidOperationException(
        "找不到仓库根（判据是 QuickSql.slnx）：门禁的静态断言必须在仓库内跑，不能从发布好的 dll 旁边猜。");

    static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickSql.slnx")))
                return dir.FullName;

        return null;
    }

    /// <summary><c>src/**\/*.cs</c>，排除 <c>obj/</c> 与 <c>bin/</c>：生成物与快照不能算进"手写代码里有没有反射"。</summary>
    public static IEnumerable<string> Sources(params string[] projects)
        => Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => projects.Length == 0 || projects.Any(p => f.Contains($"{Path.DirectorySeparatorChar}{p}{Path.DirectorySeparatorChar}")));

    /// <summary>数出现次数，两条收窄：
    /// ① <b>跳过整行注释</b>（<c>//</c> 开头与文档注释的续行）—— "这里没写反射"不能被一句解释反射的注释打回红；
    /// ② <b>要求命中位置前面不是标识符字符</b> —— 生成物里 60 处 <c>ValueConverter.To&lt;&gt;</c> 都含子串
    /// <c>Convert.</c>，不加这条的话"零 <c>Convert.</c>"这条断言会当场打死它自己要求存在的那扇门（§12.7 C 记过同一次自相矛盾）。
    /// 同行尾注仍计入，宁多勿漏 —— 这条门禁的是热路径上出现反射，误判只是让人多看一眼。</summary>
    public static int Count(string file, string pattern)
    {
        var needle = pattern.AsSpan();
        var hits = 0;

        foreach (var line in File.ReadLines(file))
        {
            var text = line.AsSpan().TrimStart();

            if (text.StartsWith("//") || text.StartsWith('*')) continue;

            for (var at = text.IndexOf(needle); at >= 0;)
            {
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1]) && text[at - 1] != '_') hits++;

                text = text[(at + needle.Length)..];
                at = text.IndexOf(needle);
            }
        }

        return hits;
    }
}
