namespace QuickSql.Metadata;

/// <summary>分片键的算法档。时间档把分片键的值下取整成一个周期，
/// 哈希档对值取模——两档都是内建的，因为这两件事覆盖了绝大多数"按月一张表"与"按租户分 N 份"的写法。</summary>
public enum SplitKind
{
    Day,
    Week,
    Month,
    Season,
    Year,

    /// <summary>按值取模。不用 object.GetHashCode：运行期随进程随机化的哈希
    /// 会把同一行在两次运行里送到两张表，那是比报错更贵的东西。整数直接取模，字符串走本文件里
    /// 那份逐字符的 FNV-1a。</summary>
    Hash,
}

/// <summary>实体的分片声明，活在 TableInfo.Split 这一格里。
/// 它是编译期/启动期就算完的静态事实：分片算法、分片键成员名、桶数、以及表名模板里
/// 那些占位符该按哪一档填充，全部不依赖运行期的连接与库内容。
/// MemberName 是分片键的属性名，与列契约的 MemberName 同一口径；Buckets 仅 Hash 档有意义，其余档为 0。</summary>
public sealed record SplitFacts(SplitKind Kind, string MemberName, int Buckets)
{
    /// <summary>成员名换算出来的列名。声明侧（G1 生成的事实、G2 反射读到的注解）留 null，
    /// 由 EntityContractFactory 一处填上——与索引、引用导航同一分工，所以
    /// "分片键指向了一颗没映射的属性"在两条路径上是同一句话，而填好之后它才随契约冻结不可变。</summary>
    public string? ColumnName { get; init; }

    /// <summary>是否时间档。Week 走 7 天，Season 走 3 个月，
    /// 其余时间档按各自的月/年步长。</summary>
    public bool IsTime => Kind is SplitKind.Day or SplitKind.Week or SplitKind.Month or SplitKind.Season or SplitKind.Year;
}

/// <summary>表名模板与分片事实的换算。全仓库只有一份实现：写侧按行算目标表、读侧按范围算片清单、
/// DDL 按范围建表，三处都从这里取，于是"同一个月在两处算出两个表名"这种偏差没有能发生的形状。</summary>
public static class SplitRouting
{
    /// <summary>表名模板里认得的占位符。写在这一个数组里，校验与替换共用它，
    /// 免得日后加一档时只改了替换没改校验。</summary>
    static readonly (string Token, bool IsDate)[] Tokens =
    [
        ("{year}", true), ("{month}", true), ("{day}", true), ("{shard}", false),
    ];

    /// <summary>模板里必须出现的占位符。缺一颗就是"这张表的名字算不出来"，所以这条校验走的是
    /// 启动期抛，不是发 SQL 时才发现。</summary>
    public static string[] RequiredTokens(SplitKind kind) => kind switch
    {
        SplitKind.Day or SplitKind.Week => ["{year}", "{month}", "{day}"],
        SplitKind.Month or SplitKind.Season => ["{year}", "{month}"],
        SplitKind.Year => ["{year}"],
        SplitKind.Hash => ["{shard}"],
        _ => throw new QuickSqlException($"未知的分片档 {kind}"),
    };

    /// <summary>校验一份表名模板：只许用认得的占位符、每颗至多一次、本档要的必须在、
    /// 本档不要的一律不许有。理由与 RequiredTokens 同一句话——算不出名字就别留着它。</summary>
    public static void ValidateTemplate(SplitKind kind, string name)
    {
        foreach (var (token, _) in Tokens)
        {
            var count = Count(name, token);

            if (count > 1)
                throw new QuickSqlException($"分表名模板 {name} 里 {token} 出现了 {count} 次，一颗占位符至多写一次");
        }

        var required = RequiredTokens(kind);

        foreach (var (token, _) in Tokens)
        {
            var wanted = Array.IndexOf(required, token) >= 0;

            if (wanted && Count(name, token) == 0)
                throw new QuickSqlException($"分表名模板 {name} 缺 {kind} 档要求的占位符 {token}");

            if (!wanted && Count(name, token) > 0)
                throw new QuickSqlException($"分表名模板 {name} 含 {kind} 档用不上的占位符 {token}");
        }

        // 兜住"写了个像占位符的东西但我不认"的那一类：{hour} 这类拼错不会被替换，会原样留在表名里，
        // 于是症状是建出一张名字叫 order_{hour} 的表 —— 比这句话晚了整整一次部署。
        for (var i = name.IndexOf('{'); i >= 0; i = name.IndexOf('{', i + 1))
        {
            var end = name.IndexOf('}', i);

            if (end < 0 || Array.FindIndex(Tokens, t => string.Equals(t.Token, name[i..(end + 1)], StringComparison.Ordinal)) < 0)
                throw new QuickSqlException($"分表名模板 {name} 里有认不出的占位符（第 {i} 个字符起）；认得的只有 {string.Join(" ", [.. Array.ConvertAll(Tokens, t => t.Token)])}");
        }
    }

    /// <summary>一行数据对应它该落的那张表。分片键为空直接抛：另一家 ORM 在这里是
    /// "当作今天"，而本家不肯让一行数据因为一个空字段静默进错表——那笔账要到月底统计时才看得见。
    /// 时间档先下整到周期起点再填模板，与 EnumerateTime 同一颗 Floor：周档的片名记的是那一周的周一，
    /// 而写入的行可以落在那一周的任何一天。少这一步，周四那一行会进当天名字的那张表，
    /// 而按周读时算出来的是周一那张——行写进去了却读不到，是这一族最难查的那种错。</summary>
    public static TableInfo Resolve(TableInfo template, SplitFacts split, object? value)
    {
        var filled = split.Kind == SplitKind.Hash
            ? template.Name.Replace("{shard}", BucketOf(value, split.Buckets).ToString("D2"))
            : FillDate(template.Name, Floor(ToDate(value, split.Kind), split.Kind));

        return template with { Name = filled, Split = null };
    }

    /// <summary>时间档的片清单，升序。from 晚于 to 直接抛，
    /// 不返回空清单：空清单在读侧是"一句 WHERE 打在一颗也不存在的表上"还是"一句恒假"，各家不一样，
    /// 而这一格的本家答案只许有一种。</summary>
    public static IReadOnlyList<TableInfo> EnumerateTime(TableInfo template, SplitFacts split, DateTime from, DateTime to)
    {
        if (!split.IsTime)
            throw new QuickSqlException($"实体 {split.MemberName} 这一档分片是 {split.Kind}，按时间范围选片只适用于时间档");

        if (from > to)
            throw new QuickSqlException($"分表范围起点 {from:yyyy-MM-dd} 晚于终点 {to:yyyy-MM-dd}");

        var list = new List<TableInfo>();
        var current = Floor(from, split.Kind);
        var last = Floor(to, split.Kind);

        while (true)
        {
            list.Add(template with { Name = FillDate(template.Name, current), Split = null });

            if (current == last)
                return list;

            current = Step(current, split.Kind);

            // 上限这一格既是"别 UNION 三百张表"的性能闸门，也是 DateTime.MaxValue 附近
            // AddYears/AddMonths 溢出后越推越远的兜底 —— 后者撞到上限就报，不给出无限循环。
            if (list.Count >= MaxShards)
                throw new QuickSqlException($"分表范围 [{from:yyyy-MM-dd}, {to:yyyy-MM-dd}] 在 {split.Kind} 档上超过上限 {MaxShards} 张表；请缩小范围或改按更粗的档分片");
        }
    }

    /// <summary>哈希档的片清单，按给出的桶号顺序（去重）。桶号越界抛。</summary>
    public static IReadOnlyList<TableInfo> EnumerateBuckets(TableInfo template, SplitFacts split, IReadOnlyList<int> buckets)
    {
        if (split.Kind != SplitKind.Hash)
            throw new QuickSqlException($"实体 {split.MemberName} 这一档分片是 {split.Kind}，按桶号选片只适用于 {SplitKind.Hash}");

        var seen = new List<int>(buckets.Count);

        foreach (var bucket in buckets)
        {
            if (bucket < 0 || bucket >= split.Buckets)
                throw new QuickSqlException($"分表桶号 {bucket} 不在 0..{split.Buckets - 1} 之内（实体声明了 {split.Buckets} 个桶）");

            if (!seen.Contains(bucket))
                seen.Add(bucket);
        }

        if (seen.Count == 0)
            throw new QuickSqlException("按桶选片至少要给一个桶号");

        return [.. seen.ConvertAll(b => template with { Name = template.Name.Replace("{shard}", b.ToString("D2")), Split = null })];
    }

    /// <summary>一次选片的片数上限。日档跨十年是 3 653 张表，而 UNION ALL 里挂 3 653 个分支
    /// 在任何一家都是"编译到死"，所以这一格宁可报一句可行动的错。</summary>
    public const int MaxShards = 512;

    static int BucketOf(object? value, int buckets)
    {
        if (value is null)
            throw new QuickSqlException($"分片键为空，无法算出目标分片（{SplitKind.Hash} 档）");

        // 三个分支都收成同一个 uint，末尾一次取模：整数那一支折成无符号而不是 Math.Abs，
        // 因为负数取模在 C# 里跟着被除数走（会得到负的桶号），而 long.MinValue 的 Math.Abs 溢出成它自己。
        // 折成无符号两件事一起解决，代价是 -1 与 4294967295 落进同一个桶——那是一枚哈希，本来就该这样。
        var hash = value switch
        {
            byte or short or int or long => unchecked((uint)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            string text => Fnv1A(text),
            _ => Fnv1A(value.ToString() ?? string.Empty),
        };

        return (int)(hash % (uint)buckets);
    }

    static uint Fnv1A(string text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;

        for (var i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= prime;
        }

        return hash;
    }

    static DateTime ToDate(object? value, SplitKind kind)
    {
        if (value is null)
            throw new QuickSqlException($"分片键为空，无法算出目标分片（{kind} 档）");

        return value switch
        {
            DateTime date => date,
            DateTimeOffset offset => offset.DateTime,
            DateOnly only => only.ToDateTime(new TimeOnly()),
            _ => throw new QuickSqlException($"{kind} 档的分片键要的是日期类型，实际拿到 {value.GetType().Name}"),
        };
    }

    /// <summary>把一个日期折进它所属的周期。周档取那一周的周一，季档取 01/04/07/10 月。</summary>
    static DateTime Floor(DateTime value, SplitKind kind) => kind switch
    {
        SplitKind.Day => value.Date,
        SplitKind.Week => value.Date.AddDays(-((int)value.DayOfWeek + 6) % 7),
        SplitKind.Month => new DateTime(value.Year, value.Month, 1),
        SplitKind.Season => new DateTime(value.Year, (value.Month - 1) / 3 * 3 + 1, 1),
        SplitKind.Year => new DateTime(value.Year, 1, 1),
        _ => throw new QuickSqlException($"未知的分片档 {kind}"),
    };

    static DateTime Step(DateTime current, SplitKind kind) => kind switch
    {
        SplitKind.Day => current.AddDays(1),
        SplitKind.Week => current.AddDays(7),
        SplitKind.Month => current.AddMonths(1),
        SplitKind.Season => current.AddMonths(3),
        SplitKind.Year => current.AddYears(1),
        _ => throw new QuickSqlException($"未知的分片档 {kind}"),
    };

    static string FillDate(string template, DateTime date)
        => template
            .Replace("{year}", date.Year.ToString("D4"))
            .Replace("{month}", date.Month.ToString("D2"))
            .Replace("{day}", date.Day.ToString("D2"));

    static int Count(string text, string token)
    {
        var count = 0;

        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + 1, StringComparison.Ordinal))
            count++;

        return count;
    }
}
