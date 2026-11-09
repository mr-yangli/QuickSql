using QuickSql.Materialization;
using QuickSql.Query;
using QuickSql.TestInfra;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>链式集合加载（<c>IncludeMany(…).ThenIncludeMany(…)</c>，§5.7）在<b>离线</b>能判的那一半。
///
/// <para>这一家与 <c>Include</c>/<c>ThenInclude</c> 最大的形状差别是：<b>层数不进主查询</b>。
/// <c>Include</c> 每一层多一张 LEFT JOIN 与一列扩展列，而集合这一档每层是执行期另发的一条
/// <c>SELECT … WHERE 外键 IN (父端主键集合)</c> —— 模型里只多一颗挂在父节点 <c>Children</c> 上的条目。
/// 所以离线这一半判的就是这两件事：<em>主查询文本一个字都没动</em>（往下那一层与旁边那一根各钉一颗 ——
/// 引用那一档从 v0.39 第四笔起也挂得上这条链），以及挂错形状（那一层不是集合导航、那一根不是引用导航）
/// 在<em>接上链的那一刻</em>就响，而不是等一句看起来合理的句子。</para>
///
/// <para>真库那一半（三张表各一句、第二层的 IN 里躺的是刚缝出来的那批子行的键、钩挂对了父；
/// 以及明细→物资那一句的去重与"配不到行的那一只留空"）在
/// <c>CascadePathTests</c> 的 <c>A_chained_include_many_loads_two_collection_levels</c> 与
/// <c>A_reference_leg_on_a_collection_level_loads_the_target</c>：
/// 子查询的文本由执行层现造，离线这一步压根不发它，而外键回填要库给值。</para></summary>
public sealed class IncludeManyChainTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>靶子是货架那三层（架 → 位 → 钩）：两层都是 <c>[ForeignKey]</c> 点名的边，
    /// 而这一格判的只有一句话 —— 往下多挂一层，主查询那条文本必须与单层逐字符相同。</summary>
    [Fact]
    public void A_second_level_adds_nothing_to_the_main_select()
    {
        var flat = orm().Select<Rack>().IncludeMany(r => r.Slots!).ToSql();
        var chained = orm().Select<Rack>().IncludeMany(r => r.Slots!).ThenIncludeMany(s => s.Hooks!).ToSql();

        Assert.Equal(flat, chained);
    }

    /// <summary>下一层挂在谁的契约上是<em>编译期</em>就定死的：<see cref="ShelfSlot"/> 那一侧只有
    /// <c>Hooks</c> 这一根集合边，把<em>根上</em>那根（<c>Slots</c>）再点一次根本接不上去 ——
    /// 这一格不需要一句断言，它需要的是那句 CS 报错，也正是这一族从前缺的东西
    /// （<c>Include(x =&gt; x.Groups).ThenInclude(g =&gt; g.Details)</c> 报的是
    /// <c>List&lt;…&gt; 上没有 Details</c>，读起来像链写错了，其实是那一家根本没有第二副形状）。</summary>
    [Fact]
    public void A_second_level_resolves_against_the_level_above_it()
    {
        // 点错了父（拿根的 Slots 当第二层）在编译期就接不上，所以这里只钉一句：往下走一层这件事
        // 本身不发任何主查询的东西 —— 与上面那颗同一把尺子，但靶子是"链的父端换了一层"。
        var chained = orm().Select<Rack>().IncludeMany(r => r.Slots!).ThenIncludeMany(s => s.Hooks!);

        Checks.Has("[QuickSqlStorageRack]", chained.ToSql());
        Checks.Absent("[QuickSqlShelfSlot]", chained.ToSql());
        Checks.Absent("[QuickSqlSlotHook]", chained.ToSql());
    }

    /// <summary><c>IncludeMany</c> 的那道"投影之后不能再加载导航"闸门在本次拆口（节点构造与入清单分成两颗）
    /// 之后必须还在，而且响的还是原来那句话 —— 拆的是<em>怎么落模型</em>，不是<em>什么时候不许落</em>。
    /// 靶子投影刻意<em>投成一只实体</em>（<c>ShelfSlot</c>）：那道闸判的是计划形态，不是 CLR 类型，
    /// 而链上的形参只有长成 <c>IQuery&lt;ShelfSlot&gt;</c> 才递得进第二颗 lambda。</summary>
    [Fact]
    public void A_projection_still_refuses_the_collection_navigation()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Rack>()
                .Select(r => new ShelfSlot { Id = r.Id, RackKey = r.Id, Bin = r.Label })
                .IncludeMany(s => s.Hooks!));

        Checks.Has("IncludeMany 只能在实体查询上使用", failure.Message);
    }

    /// <summary>旁边那一根（<c>IncludeMany(…).ThenInclude(…)</c>，§5.7 引用那一档）与往下那一层同一条纪律：
    /// <b>引用边的目标表也不进主查询</b>。靶子是托盘 → 明细那一层集合、明细 → 物资那一根引用 ——
    /// 物资这一侧的 <c>LEFT JOIN</c> 今天只存在于执行期另发的那条 SELECT 里（离线这一步不发它，
    /// 所以这里量的是"主查询没被它碰过"，那条文本本身归 <c>CascadePathTests</c> 的真库那一半）。</summary>
    [Fact]
    public void A_reference_leg_adds_nothing_to_the_main_select()
    {
        var flat = orm().Select<Pallet>().IncludeMany(p => p.Lines!).ToSql();
        var chained = orm().Select<Pallet>().IncludeMany(p => p.Lines!).ThenInclude(l => l.Material).ToSql();

        Assert.Equal(flat, chained);
        Checks.Absent("[QuickSqlPalletLine]", chained);
        Checks.Absent("[QuickSqlMaterial]", chained);
    }

    /// <summary>引用那一档收在集合链上是<em>旁边多一根</em>，不是<em>下面多一层</em>：把集合导航递进这颗口
    /// 必须在接上链的那一刻就响，并指名换 <c>ThenIncludeMany</c>。
    ///
    /// <para><b>为什么这一格只能靠库自己判</b>：编译期拦不住 —— 形参是 <c>Expression&lt;Func&lt;ShelfSlot, TProp&gt;&gt;</c>，
    /// 而 <c>List&lt;SlotHook&gt;</c> 是一只完全合法的 <c>TProp</c>。放过去的话执行层按<em>主键</em>缝引用，
    /// 一根集合边只会剩下最后一只，而"少了几只"在返回的结果里看不出形状。</para></summary>
    [Fact]
    public void The_reference_mouth_refuses_a_collection_navigation()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Rack>().IncludeMany(r => r.Slots!).ThenInclude(s => s.Hooks!));

        Checks.Has("是集合导航", failure.Message);
        Checks.Has("ThenIncludeMany", failure.Message);
    }

    /// <summary>写入口的可见性进不了<em>发出的文本</em>：<c>OpenShop</c> 与 <c>GuardedShop</c> 同表、同列、
    /// 同一根点名的边，只差 CLR 那半颗 set —— <c>ToSql()</c> 必须逐字节相同。
    ///
    /// <para><b>这一格为什么值得单独立一颗</b>：缝合的 accessor 选择发生在执行期（第二句 <c>SELECT</c>
    /// 由执行层现造，离线这一步不发它），计划文本本来就不该认识 CLR 的写口 —— 而旧形状炸的正是
    /// <c>protected set</c> 那一形，报的还是「没有这根导航属性」这种两次不实的话（成员在场，
    /// 集合导航也成立）。逐字节对照把整个「写口可见性」这一族钉死在文本侧之外。</para></summary>
    [Fact]
    public void A_protected_setter_twin_emits_the_same_plan_text()
    {
        var open = orm().Select<OpenShop>().IncludeMany(s => s.Certs!).ToSql();
        var guarded = orm().Select<GuardedShop>().IncludeMany(s => s.Certs!).ToSql();

        Assert.Equal(open, guarded);
    }

    /// <summary>与 <c>AsSplitQuery()</c> 的互斥（2026-11-01 起当场拒，此前是<b>静默丢集合</b>）：
    /// 执行分派按拆查先赢（<c>Rows</c> 的第一格），<c>IncludeManys</c> 在场就是无声蒸发 ——
    /// "少了几只"在返回的结果里看不出形状，那正是本仓库最不肯做的形状。
    /// 两条接链顺序（先集合后拆查 ∶ 先拆查后集合）各自在接上链的那一刻响：先集合的那一向拦在
    /// <c>SplitQuery()</c>，先拆查的那一向拦在 <c>IncludeMany</c> 入口，凑成双向护栏；
    /// 执行期 <c>Rows</c> 还有第三道兜底，正常链到不了那里。</summary>
    [Fact]
    public void Split_query_after_a_collection_navigation_is_refused()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Rack>().IncludeMany(r => r.Slots!).AsSplitQuery());

        Checks.Has("AsSplitQuery() 与 IncludeMany", failure.Message);
        Checks.Has("单独成句", failure.Message);
    }

    [Fact]
    public void A_collection_navigation_after_split_query_is_refused()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Rack>().AsSplitQuery().IncludeMany(r => r.Slots!));

        Checks.Has("AsSplitQuery() 与 IncludeMany", failure.Message);
        Checks.Has("单独成句", failure.Message);
    }

    /// <summary>不碰连接的真身读数：写入口受保护而集合位预置成活串时，<see cref="MemberAccessors.BindCollection"/>
    /// 不再炸在写口上，<c>Attach</c> 往 getter 当场交回的那只集合里追加 —— 这条路全程不需要 set。
    /// 真库那一半（同一根边从库里缝回来）在 <c>GuardedCollectionPathTests</c>。</summary>
    [Fact]
    public void The_protected_setter_binding_appends_into_the_live_collection()
    {
        var nav = MemberAccessors.BindCollection(
            typeof(GuardedShop), nameof(GuardedShop.Certs), typeof(ShopCert),
            "实体 GuardedShop 上没有名为 Certs 的集合导航属性，无法挂上 IncludeMany 加载的子实体。");

        var shop = new GuardedShop { Id = 7 };

        nav.Attach(shop, new ShopCert { Id = 1, ShopId = 7, No = "甲一" });
        nav.Attach(shop, new ShopCert { Id = 2, ShopId = 7, No = "甲二" });

        Assert.Equal(["甲一", "甲二"], shop.Certs.Select(c => c.No).ToArray());
    }

    /// <summary>集合位为 null 而又没有公共写口 —— 这一格确实缝不上，而文案要<em>同时点到两颗</em>：
    /// 「写入口不是公共的」与「getter 交回的集合位是 null」，并且不许再谎称成员不存在
    /// （那句「没有名为」正是被本家作废的旧文案）。</summary>
    [Fact]
    public void A_null_slot_behind_a_non_public_setter_names_both_actors()
    {
        var nav = MemberAccessors.BindCollection(
            typeof(UninitialisedShop), nameof(UninitialisedShop.Certs), typeof(ShopCert),
            "实体 UninitialisedShop 上没有名为 Certs 的集合导航属性，无法挂上 IncludeMany 加载的子实体。");

        var shop = new UninitialisedShop();

        var failure = Checks.Raised<QuickSqlException>(
            () => { nav.Attach(shop, new ShopCert { Id = 3 }); return null; });

        Checks.Has("写入口不是公共的", failure.Message);
        Checks.Has("集合位是 null", failure.Message);
        Checks.Absent("没有名为", failure.Message);
    }

    /// <summary>只读而又收不了追加（<c>IReadOnlyList&lt;T&gt;</c> 那一形）：当场抛在建绑定这一步，
    /// 而文案点名的是<em>形状</em> —— 需要的二选一（公共写口 / 初始化好的可追加集合）直接写在句子里，
    /// 同样不许出现那句「没有名为」。这一形从来没有"能工作的一半"可对照，所以钉的只有文案。</summary>
    [Fact]
    public void A_read_only_immutable_shape_names_the_shape_required()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => MemberAccessors.BindCollection(
                typeof(LockedShop), nameof(LockedShop.Certs), typeof(ShopCert),
                "实体 LockedShop 上没有名为 Certs 的集合导航属性，无法挂上 IncludeMany 加载的子实体。"));

        Checks.Has("IReadOnlyList<ShopCert>", failure.Message);
        Checks.Has("追加口", failure.Message);
        Checks.Absent("没有名为", failure.Message);
    }

    // ── OrderByMany（§5.7 的子行排序口）──────────────────────────────
    //
    // 头注里那句"子查询的文本由执行层现造，离线这一步压根不发它"从今天起要多一颗例外：这一族
    // 用 <c>AopExecutingTests</c> 同款的替身驱动（只换连接出口，方言与发射器都是 SqlServer 真身）
    // 把两条 SELECT 真发到桩上，于是<em>子句的文本</em>第一次能在离线量到 —— 母句照旧由
    // <c>ToSql()</c> 那一家钉，这一族钉的是父句逐字节不动之外，序确实进了子句的 ORDER BY。

    /// <summary>点了这颗口：子 SELECT 带上 T-SQL 引号的 <c>ORDER BY</c> 出场；没点的那一形，
    /// 子句里连 <c>ORDER BY</c> 三个字都不出现（与本口存在之前逐字节同形）；而<em>父句</em>与
    /// 不点的孪生逐字节相等 —— 三件事一起钉，才配得上"发射在库里、主查询一个字符不变"那句承诺。</summary>
    [Fact]
    public async Task The_ordered_child_select_carries_the_order_by_while_the_parent_twin_is_untouched()
    {
        var plain = new ReadJournal();
        await ProbeOrm(plain).Select<Rack>().IncludeMany(r => r.Slots!).ToListAsync(TestContext.Current.CancellationToken);

        var ordered = new ReadJournal();
        await ProbeOrm(ordered).Select<Rack>().IncludeMany(r => r.Slots!).OrderByMany(s => s.Bin)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, plain.Texts.Count);
        Assert.Equal(2, ordered.Texts.Count);

        Assert.Equal(plain.Texts[0], ordered.Texts[0]);
        Checks.Absent("ORDER BY", plain.Texts[1]);
        Checks.Has("ORDER BY [t0].[Bin] ASC", ordered.Texts[1]);
    }

    /// <summary>嵌套两层、各点各的：点在 <c>IncludeMany</c> 后面排第一层子行，点在
    /// <c>ThenIncludeMany</c> 后面排第二层 —— "每一层都生效"不是一句承诺，是这三条文本读数。
    /// 特别钉住"没点的那一层不发 ORDER BY"：隔层生效或静默吞层这两种失败形态在这一格里都无处藏。</summary>
    [Fact]
    public async Task Order_by_many_targets_the_layer_it_was_written_after()
    {
        var plain = new ReadJournal();
        await ProbeOrm(plain).Select<Rack>().IncludeMany(r => r.Slots!).ThenIncludeMany(s => s.Hooks!)
            .ToListAsync(TestContext.Current.CancellationToken);

        var ordered = new ReadJournal();
        await ProbeOrm(ordered).Select<Rack>()
            .IncludeMany(r => r.Slots!)
            .OrderByMany(s => s.Bin, descending: true)
            .ThenIncludeMany(s => s.Hooks!)
            .OrderByMany(h => h.Name)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, ordered.Texts.Count);

        Assert.Equal(plain.Texts[0], ordered.Texts[0]);
        Checks.Has("ORDER BY [t0].[Bin] DESC", ordered.Texts[1]);
        Checks.Has("ORDER BY [t0].[Name] ASC", ordered.Texts[2]);
    }

    /// <summary>认列响在<em>接链的那一刻</em>，不等执行期缝子行：<c>Hooks</c> 是导航不是列，
    /// 递进这颗口与链级 <c>OrderBy(string)</c> 撞同一句话（三档咽喉共用一颗，§5.8）。</summary>
    [Fact]
    public void An_unmappable_child_key_refuses_at_the_chain()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Rack>().IncludeMany(r => r.Slots!).OrderByMany(s => s.Hooks));

        Checks.Has("没有名为 Hooks 的列", failure.Message);
    }

    /// <summary>嵌套路径（<c>d =&gt; d.Warehouse.Name</c> 那一形）当场拒，而不是剥成末级名去撞子实体
    /// 自己的列：这一档不发 JOIN，排在别的表那一侧的列根本发不出来。</summary>
    [Fact]
    public void A_nested_path_key_is_refused_rather_than_flattened()
    {
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Select<Rack>().IncludeMany(r => r.Slots!).OrderByMany(s => s.Bin!.Length));

        Checks.Has("直接属性访问", failure.Message);
    }

    /// <summary>替身驱动的读数出口：把每条真发到连接上的 SELECT 文本记进账本，并按表名给一行现成的
    /// 行集（架一句、位一句、钩一句）。父查询必须交回一只架，否则子 SELECT 整个被跳过；位必须交回
    /// 一只位，否则第二层没有可当 IN 值的键。桩顶掉的只有连接出口，方言、发射器、参数 binder 全是
    /// SqlServer 真身 —— 这正是"文本里那对 <c>[]</c> 引号算真读数"的前提。</summary>
    static QuickSqlOrm ProbeOrm(ReadJournal journal)
        => new(new QuickSqlOrmOptions
        {
            Provider = new ProbeReadProvider(SqlServer.Provider(SqlServer.NoDriverFactory.Instance), journal),
            ConnectionString = SqlServer.OfflineConnectionString,
        });

    sealed class ReadJournal
    {
        public List<string> Texts { get; } = [];

        public DbDataReader ReaderFor(string sql)
        {
            Texts.Add(sql);

            if (sql.Contains("QuickSqlStorageRack", StringComparison.Ordinal))
                return new ScriptedReader([new object?[] { 1, "架" }], typeof(int), typeof(string));

            if (sql.Contains("QuickSqlShelfSlot", StringComparison.Ordinal))
                return new ScriptedReader([new object?[] { 11, 1, "位" }], typeof(int), typeof(int), typeof(string));

            if (sql.Contains("QuickSqlSlotHook", StringComparison.Ordinal))
                return new ScriptedReader([new object?[] { 21, 11, "钩" }], typeof(int), typeof(int), typeof(string));

            throw new InvalidOperationException($"读数桩没计划过的语句形状：{sql}");
        }
    }

    /// <summary>按位置交行的最小行源：物化那边 int 走 <c>GetInt32</c>、string 走 <c>IsDBNull + GetString</c>
    /// （<c>Materialization/ReadOps.cs</c> 的臂表），其余访问器这一册撞不到，撞上即响。</summary>
    sealed class ScriptedReader(IReadOnlyList<object?[]> rows, params Type[] fieldTypes) : DbDataReader
    {
        int _at = -1;

        public override int FieldCount => fieldTypes.Length;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override bool HasRows => rows.Count > 0;
        public override int RecordsAffected => -1;
        public override int VisibleFieldCount => fieldTypes.Length;
        public override bool Read() => ++_at < rows.Count;
        public override bool NextResult() => false;

        object? Value(int ordinal) => rows[_at][ordinal];

        public override bool IsDBNull(int ordinal) => Value(ordinal) is null;
        public override object GetValue(int ordinal) => Value(ordinal)!;
        public override Type GetFieldType(int ordinal) => fieldTypes[ordinal];
        public override string GetName(int ordinal) => $"col{ordinal}";
        public override string GetDataTypeName(int ordinal) => fieldTypes[ordinal].Name;
        public override int GetOrdinal(string name) => throw new NotSupportedException("物化按位置读，不按名字取列。");
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();

        public override int GetValues(object[] values)
        {
            var taken = Math.Min(values.Length, fieldTypes.Length);

            for (var i = 0; i < taken; i++)
                values[i] = Value(i)!;

            return taken;
        }

        public override int GetInt32(int ordinal) => (int)Value(ordinal)!;
        public override string GetString(int ordinal) => (string)Value(ordinal)!;

        public override bool GetBoolean(int ordinal) => throw NotUsed();
        public override byte GetByte(int ordinal) => throw NotUsed();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw NotUsed();
        public override char GetChar(int ordinal) => throw NotUsed();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw NotUsed();
        public override DateTime GetDateTime(int ordinal) => throw NotUsed();
        public override decimal GetDecimal(int ordinal) => throw NotUsed();
        public override double GetDouble(int ordinal) => throw NotUsed();
        public override float GetFloat(int ordinal) => throw NotUsed();
        public override Guid GetGuid(int ordinal) => throw NotUsed();
        public override short GetInt16(int ordinal) => throw NotUsed();
        public override long GetInt64(int ordinal) => throw NotUsed();

        public override IEnumerator GetEnumerator() => rows.GetEnumerator();

        static NotSupportedException NotUsed()
            => new("靶子实体只有 int 与 string 两臂：撞上别的访问器说明链的形状变了，这一册要显式承认。");

        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>只换连接出口的那一层：其余能力逐字转交内层真 provider（与 <c>AopExecutingTests</c>
    /// 那颗 <c>ProbeProvider</c> 同一条纪律，只是这里的命令要交回行源而不是只认 non-query）。</summary>
    sealed class ProbeReadProvider(IDbProvider inner, ReadJournal journal) : IDbProvider
    {
        public DbKind Kind => inner.Kind;
        public DbCapabilities Capabilities => inner.Capabilities;
        public IConnectorFactory Connectors { get; } = new ProbeConnectors(journal);
        public ISqlDialect Dialect => inner.Dialect;
        public ITypeMap Types => inner.Types;
        public IValueBinder Values => inner.Values;
        public IExpressionRules Expressions => inner.Expressions;
        public ICodeFirstDialect CodeFirst => inner.CodeFirst;
        public ISchemaReader Schema => inner.Schema;
        public IKeyStrategy Keys => inner.Keys;
        public IConcurrencyStrategy Concurrency => inner.Concurrency;
        public IChangeGuard ChangeGuard => inner.ChangeGuard;
        public IBulkStrategy? Bulk => inner.Bulk;
    }

    sealed class ProbeConnectors(ReadJournal journal) : IConnectorFactory
    {
        public DbConnection CreateConnection(string connectionString) => new ProbeConnection();

        public DbCommand CreateCommand(DbConnection connection) => new ProbeCommand(journal, connection);

        public bool TryEnsureLoaded(out string? failure)
        {
            failure = null;
            return true;
        }
    }

    sealed class ProbeConnection : DbConnection
    {
        ConnectionState _state = ConnectionState.Closed;

        [AllowNull] public override string ConnectionString { get; set; } = SqlServer.OfflineConnectionString;
        public override string Database => "probe-db";
        public override string DataSource => "probe";
        public override string ServerVersion => "0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new ProbeCommand(null, this);
    }

    sealed class ProbeCommand(ReadJournal? journal, DbConnection? connection) : DbCommand
    {
        DbParameterCollection? _parameters;

        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get => connection; set => _ = value; }
        protected override DbParameterCollection DbParameterCollection => _parameters ??= new ProbeParameters();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new ProbeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => journal!.ReaderFor(CommandText);

        public override int ExecuteNonQuery() => throw new NotSupportedException("这一册只发 SELECT。");
        public override object? ExecuteScalar() => throw new NotSupportedException("这一册只发 SELECT。");
    }

    sealed class ProbeParameter : DbParameter
    {
        public override DbType DbType { get; set; } = DbType.Object;
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }

        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }

        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }

        public override void ResetDbType() => DbType = DbType.Object;
    }

    sealed class ProbeParameters : DbParameterCollection
    {
        readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot { get; } = new();

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
                _items.Add((DbParameter)value!);
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains(value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => _items.ToArray().CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    /// <summary>写入口同样受保护、而初始化器<em>不</em>预置集合位的那一形：缝合当场找不到可追加的现串。
    /// 只当 <see cref="MemberAccessors"/> 的靶子用，不进任何查询与契约。
    /// <b>不能是 sealed</b>：C# 在密封类型的<em>嵌套</em>类型里不许出现 protected 成员（CS0628），
    /// 而这一形的全部意义就是那颗受保护的 set。</summary>
    private class UninitialisedShop
    {
        public List<ShopCert> Certs { get; protected set; } = null!;
    }

    /// <summary>只读的集合导航：没有写口，形状自己又收不了就地追加。同上，只当报错文案的靶子。</summary>
    sealed class LockedShop
    {
        public IReadOnlyList<ShopCert> Certs { get; } = [];
    }
}
