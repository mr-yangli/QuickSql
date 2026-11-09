using System.ComponentModel.DataAnnotations.Schema;
using QuickSql;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>日程表：<b>本文件自带的实体</b>，DateOnly 与 DateTime 两对列同表 —— 三口的三种参数位
/// （DateTime 列 / DateOnly 列 / 两颗 DateOnly 参数）在这里都有现场。不进 Tests.Common 的共享模型：
/// 那份的列序与表名被几十份黄金样本钉死，往里加一列就是整片样本陪绑 —— 与当年
/// <see cref="QuickSql.TestInfra.Customer"/> 之外另起一张的先例（Ticket/InventoryItem）同一笔账。</summary>
[Table("QuickSqlSchedule")]
public sealed class Schedule
{
    public int Id { get; set; }

    /// <summary>时间段两端（DateTime 列）。</summary>
    public DateTime BeginAt { get; set; }

    public DateTime EndAt { get; set; }

    /// <summary>日期段两端（DateOnly 列）。</summary>
    public DateOnly BeginDate { get; set; }

    public DateOnly EndDate { get; set; }
}

/// <summary>QuickFx 三口的钉子（日期范围 / 日期交集 / 时间段×日期交集 —— SqlSugar 旧 SqlFunc 封装的一比一移植）。
///
/// <para>判据三半：① <b>黄金样本</b>钉"整条文本长什么样"（含参数颗数与序）；② <b>口径等价</b>钉
/// QuickFx 的产出与手写比较逐字节相同 —— 这条功能最重要的主张就是"译文只是比较 + AND，
/// 十八家方言共用一份，谁都不用登记模板"，等价钉在<b>全部十八家叶子</b>上各答一次；
/// ③ 谓词文本再按<b>九个在册金标族</b>逐族钉整条 —— 各族引号、参数前缀、大小写折叠这一层
/// 由金标册认领（九个金标在案的族：自有方言五家 + 血统代表金仓/达梦/MySQL + 自带金标的虚谷）。</para>
///
/// <para>与旧封装语义的两处分岔都在 <c>DateRange</c>，都往好处翻：
/// ① 终点<b>含当天</b>（旧封装要调用方自己 +1 天并留注释道歉；现在内核翻译时在参数侧算好）；
/// ② 边界必须能当场求值 —— 拿行上的成员当边界就得落方言日期算术，当场拒并指路（那颗拒也在本册）。</para></summary>
public sealed class QuickFxTests
{
    /// <summary>用户口径：终点传<b>当天</b>。05-01、05-02、05-03 三天都该在。</summary>
    static readonly DateOnly From = new(2026, 5, 1);
    static readonly DateOnly To = new(2026, 5, 3);

    static readonly TimeOnly Midnight = new();

    /// <summary>手写对照用的两端（测试代码里先把 <see cref="DateOnly.ToDateTime"/> 算掉，再让闭包圈走
    /// <see cref="DateTime"/> 字段）：那个实例方法没有登记翻译，手写版把它放进表达式树反而走不通 ——
    /// 而这也正是 QuickFx 的主张之一：边界在<b>调用方代码里</b>算好成为参数，树里只留比较。
    /// <see cref="ToAtPlus1"/> 同理：树里的 <c>AddDays</c> 是逐家登记的方言函数（DATEADD/INTERVAL），
    /// 对照必须是"调用方在参数侧加完一天再比较"的原味形态，QuickFx 的产出才谈得上逐字节等价。</summary>
    static readonly DateTime FromAt = From.ToDateTime(Midnight);
    static readonly DateTime ToAt = To.ToDateTime(Midnight);
    static readonly DateTime ToAtPlus1 = ToAt.AddDays(1);

    static QuickSqlOrm orm() => SqlServer.Orm();

    // ---------- DateRange：DateTime 列 / DateOnly 列 / 上界摘除 / 空段 ----------

    [Fact]
    public void DateRange_on_a_datetime_column_covers_the_last_day_without_caller_adding_one()
    {
        var sql = orm().Select<Schedule>()
            .Where(s => QuickFx.DateRange(s.BeginAt, From, To))
            .ToSql(out var parameters);

        Golden.Verify("fx-daterange-datetime", sql);

        // 终态半开式：上界的 +1 天已经算进参数（05-04），列上零表达式 —— 索引照走。
        Checks.Has("[t0].[BeginAt] >= @p0 AND [t0].[BeginAt] < @p1", sql);

        // "+1 天算在参数侧"的直接见证：两颗参数值是 05-01 与 05-04；调用方手里的 To 原样是 05-03。
        Assert.Equal([From, To.AddDays(1)], parameters.Select(p => (DateOnly) p.Value!).ToArray());
    }

    [Fact]
    public void DateRange_writes_a_dateonly_column_the_same_way()
    {
        var sql = orm().Select<Schedule>()
            .Where(s => QuickFx.DateRange(s.BeginDate, From, To))
            .ToSql();

        Golden.Verify("fx-daterange-dateonly", sql);

        Checks.Has("[t0].[BeginDate] >= @p0 AND [t0].[BeginDate] < @p1", sql);
    }

    [Fact]
    public void DateRange_on_the_last_readable_day_drops_the_upper_bound()
    {
        // MaxValue 没有"下一天"，AddDays 会溢出 —— 内核把上界那半边摘掉，照发不炸。
        var sql = orm().Select<Schedule>()
            .Where(s => QuickFx.DateRange(s.BeginAt, From, DateOnly.MaxValue))
            .ToSql();

        Golden.Verify("fx-daterange-open-ended", sql);

        Checks.Has("[t0].[BeginAt] >= @p0", sql);
        Checks.Absent("< @p1", sql);
    }

    [Fact]
    public void An_inverted_range_is_the_empty_interval_not_an_error()
    {
        // begin > end：上界（05-02）不大于下界（05-03）→ 整句恒不中。区间数学的空集，照发不拦。
        var sql = orm().Select<Schedule>()
            .Where(s => QuickFx.DateRange(s.BeginAt, To, From))
            .ToSql(out var parameters);

        Checks.Has("[t0].[BeginAt] >= @p0 AND [t0].[BeginAt] < @p1", sql);

        // 空不等于"沉默改写"：上界参数就是 From+1，读出来的 SQL 自己就把整段判空了。
        Assert.Equal([To, From.AddDays(1)], parameters.Select(p => (DateOnly) p.Value!).ToArray());
    }

    // ---------- 三口的"口径等价"：与手写比较逐字节相同，十八家叶子各答一次 ----------

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    [InlineData("xugu")]
    [InlineData("gbase8s")]
    [InlineData("clickhouse")]
    [InlineData("postgresql")]
    [InlineData("kingbase")]
    [InlineData("duckdb")]
    [InlineData("gbase8c")]
    [InlineData("gaussdb")]
    [InlineData("highgo")]
    [InlineData("opengauss")]
    [InlineData("mysql")]
    [InlineData("tidb")]
    [InlineData("oceanbase")]
    [InlineData("dameng")]
    [InlineData("shentong")]
    [InlineData("oracle")]
    public void The_three_shapes_match_their_handwritten_equivalents_byte_for_byte(string family)
    {
        var orm = For(family);

        var range = orm.Select<Schedule>().Where(s => QuickFx.DateRange(s.BeginAt, From, To)).ToSql();
        var rangeByHand = orm.Select<Schedule>()
            .Where(s => s.BeginAt >= FromAt && s.BeginAt < ToAtPlus1)
            .ToSql();
        Assert.Equal(rangeByHand, range);

        var intersection = orm.Select<Schedule>()
            .Where(s => QuickFx.DateIntersection(s.BeginDate, s.EndDate, From, To))
            .ToSql();
        var intersectionByHand = orm.Select<Schedule>()
            .Where(s => s.BeginDate <= To && s.EndDate >= From)
            .ToSql();
        Assert.Equal(intersectionByHand, intersection);

        var timeIntersection = orm.Select<Schedule>()
            .Where(s => QuickFx.DateTimeIntersection(s.BeginAt, s.EndAt, From, To))
            .ToSql();
        var timeIntersectionByHand = orm.Select<Schedule>()
            .Where(s => s.BeginAt <= ToAt && s.EndAt >= FromAt)
            .ToSql();
        Assert.Equal(timeIntersectionByHand, timeIntersection);
    }

    /// <summary>按名取一家的离线组装根（现有十八家叶子<b>全数在册</b>，两家理论共用）。
    /// 默认分支响而不静默少测：以后添一家，只要 InlineData 忘了登、或登了名而这里没有探针，都是当场红。</summary>
    static QuickSqlOrm For(string family) => family switch
    {
        // 自有方言五家（各自一册金标；QuickFx 没有方言函数可登，在它们家也只有这几处共享字形）。
        "sqlserver" => SqlServer.Orm(),
        "sqlite" => Sqlite.Orm(),
        "xugu" => Xugu.Orm(),
        "gbase8s" => Gbase8s.Orm(),
        "clickhouse" => ClickHouse.Orm(),

        // PostgreSql 血统七家（本尊 ∶ 金标代表金仓 ∶ 自带金标的虚谷 ∶ 另四叶逐字同形，§13.3 同形交给对读）。
        "postgresql" => PostgreSql.Orm(),
        "kingbase" => Kingbase.Orm(),
        "duckdb" => DuckDb.Orm(),
        "gbase8c" => Gbase8c.Orm(),
        "gaussdb" => GaussDb.Orm(),
        "highgo" => Highgo.Orm(),
        "opengauss" => OpenGauss.Orm(),

        // MySQL 血统三家（本尊带金标）。
        "mysql" => MySql.Orm(),
        "tidb" => TiDb.Orm(),
        "oceanbase" => OceanBase.Orm(),

        // Oracle 血统三家（达梦带金标；本尊与神通的查询链与它逐字同形）。
        "dameng" => Dameng.Orm(),
        "shentong" => ShenTong.Orm(),
        "oracle" => Oracle.Orm(),
        _ => throw new NotSupportedException($"QuickFx 册里没有 {family} 这一家的探针。"),
    };

    // ---------- 九族金标：谓词整条文本长什么样，账按族立 ----------

    /// <summary>谓词文本按<b>九个在册金标族</b>钉整条 —— 与 <see cref="InsertOrUpdateLayerTests"/>
    /// "新产出的文本按族立基线"同一分工：金标只立在金标在案的族（自有方言五家 ∶ 血统代表金仓/达梦/MySQL ∶
    /// 自带金标的虚谷），没有金标目录的九颗叶子（PG 血统 postgresql/opengauss/gaussdb/highgo/gbase8c，
    /// Oracle 血统 oracle/shentong，MySQL 血统 tidb/oceanbase）走各家 DialectTests 的"同形交给逐字对读"
    /// —— 这里抄基线只会多一处要同步的地方。它们的<b>行为</b>由上面那颗十八家等价钉逐族量过。
    /// <para>每族钉两枰：范围形把一条比较谓词里最重要的三格字形（列引法 ∶ 参数前缀 ∶ AND 组合）一次钉住，
    /// 交集形钉旧四参模板的语序。<c>fx-daterange-open-ended</c>/<c>fx-daterange-dateonly</c>/
    /// <c>fx-datetimeintersection</c> 不入各族：上界摘除与列型分岔、DateTime×DateOnly 混排是<b>共享翻译器</b>
    /// 的行为不是方言行为——sqlserver 那五份钉形状，谁家都不会在这三处分岔。</para></summary>
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    [InlineData("xugu")]
    [InlineData("gbase8s")]
    [InlineData("clickhouse")]
    [InlineData("kingbase")]
    [InlineData("dameng")]
    [InlineData("mysql")]
    [InlineData("duckdb")]
    public void The_predicate_text_is_byte_pinned_per_golden_family(string family)
    {
        var orm = For(family);

        Golden.Verify("fx-daterange-datetime",
            orm.Select<Schedule>().Where(s => QuickFx.DateRange(s.BeginAt, From, To)).ToSql(), family);

        Golden.Verify("fx-dateintersection",
            orm.Select<Schedule>().Where(s => QuickFx.DateIntersection(s.BeginDate, s.EndDate, From, To)).ToSql(), family);
    }

    // ---------- 交集两口：闭端重叠的形状 ----------

    [Fact]
    public void DateIntersection_is_the_closed_overlap_predicate()
    {
        var sql = orm().Select<Schedule>()
            .Where(s => QuickFx.DateIntersection(s.BeginDate, s.EndDate, From, To))
            .ToSql();

        Golden.Verify("fx-dateintersection", sql);

        // {0} <= {3} AND {1} >= {2}：旧封装的四参模板原样 —— 文本序先见 To（→@p0）后见 From（→@p1）。
        Checks.Has("[t0].[BeginDate] <= @p0 AND [t0].[EndDate] >= @p1", sql);
    }

    [Fact]
    public void DateTimeIntersection_mixes_datetime_columns_with_date_parameters()
    {
        var sql = orm().Select<Schedule>()
            .Where(s => QuickFx.DateTimeIntersection(s.BeginAt, s.EndAt, From, To))
            .ToSql();

        Golden.Verify("fx-datetimeintersection", sql);

        Checks.Has("[t0].[BeginAt] <= @p0 AND [t0].[EndAt] >= @p1", sql);
    }

    [Fact]
    public void QuickFx_predicates_compose_with_the_chain()
    {
        // 与链上其它谓词折叠成扁平 AND、与 OR 组成嵌套 —— 就是普通比较节点该有的两种合成。
        var sql = orm().Select<Schedule>()
            .Where(s => s.Id > 0)
            .Where(s => QuickFx.DateIntersection(s.BeginDate, s.EndDate, From, To))
            .Where(s => s.EndAt > s.BeginAt || s.BeginDate != default)
            .ToSql();

        Assert.True(Checks.Occurrences(sql, " AND ") >= 2, sql);
        Checks.Has("[t0].[BeginDate] <= @p1 AND [t0].[EndDate] >= @p2", sql);
    }

    [Theory]
    [InlineData("DateRange")]
    [InlineData("DateIntersection")]
    [InlineData("DateTimeIntersection")]
    public void Calling_outside_an_expression_throws_the_pointer_message(string method)
    {
        // 三颗方法体没有实现；真被调到（例如先物化再在内存里"顺性"调一下）就要响得指路。
        var ex = method switch
        {
            "DateRange" => Assert.Throws<NotSupportedException>(() => QuickFx.DateRange(From.ToDateTime(Midnight), From, To)),
            "DateIntersection" => Assert.Throws<NotSupportedException>(() =>
                QuickFx.DateIntersection(From, To, From, To)),
            _ => Assert.Throws<NotSupportedException>(() =>
                QuickFx.DateTimeIntersection(default, default, From, To)),
        };

        Assert.Contains("只活在查询表达式树里", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ToList()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bound_living_on_the_row_is_refused_with_a_pointer_to_the_alternatives()
    {
        // 边界在行上（s.BeginDate）→ "终态 +1 天"要落方言日期算术，当场拒并指两条路。
        var ex = Assert.Throws<UntranslatableExpressionException>(() =>
            orm().Select<Schedule>()
                .Where(s => QuickFx.DateRange(s.BeginAt, s.BeginDate, To))
                .ToSql());

        Assert.Contains("DateIntersection", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddDays(1)", ex.Message, StringComparison.Ordinal);
    }
}
