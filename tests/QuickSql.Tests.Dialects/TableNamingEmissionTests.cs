using System.Reflection;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>规格 §16.4② 那颗"加一条架构测试钉住即可"的测试：表名在<em>整个仓库里是一条值的通路</em>，
/// 不许在任何地方以字符串形式从契约直接拼进 SQL。分表把这个要求从"整洁"升级成了"正确性"——
/// 一处绕过换算的拼接发出的是<code>INSERT INTO QuickSqlShardLog_{year}</code>，库里既没有这张表、
/// 也不报错，只在某张别的表上少了行。
///
/// <para><b>两半各钉一件事</b>。第一半是<em>行为</em>：十八家方言族逐家量一遍（第十七颗 DuckDb 是
/// v0.39 第一笔落的；第十八颗 ClickHouse 是 v0.39 第二笔落的，那一家砍掉 写·UPDATE/写·DELETE/DDL·时间档
/// 三格发射口、另立 <see cref="ClickHouse_refuses_mutation_and_indexed_ddl"/> 点名；
/// 等号按 <see cref="DbKind"/> 全家数，一家一家从上面那个 switch 里点名），模板进发射口必须当场抛，
/// 而换算好的名字家户都落得下同一条后缀（钉的是"没有一家把兜底覆写掉了"——<c>EmitTableName</c> 是
/// virtual，任何一家重写它而忘了那一句守卫，只有逐家跑一遍才看得见）。第二半是<em>形状</em>：反射枚举
/// Abstractions 里所有收 <see cref="TableInfo"/> 的接口成员，闭集就是那九颗，多一颗就要人来认账
/// （钉的是"别新开一条命名的路"，那是这一档唯一防得住"将来"的形式）。</para>
///
/// <para><b>为什么不是文件扫描</b>：扫 <c>src/</c> 找"哪里把 <c>table.Name</c> 拼进了 writer"要一份豁免清单
/// —— DDL 那六家 <c>*CodeFirst</c>、<c>SqlServerKeys.IdentityInsertToggle</c>（今天不可达）、模型打印器、
/// 异常消息、执行标签各占一行。清单一旦写下就替豁免的那几处说话，而它<em>恰恰量不到</em>新加的第六处。
/// 行为那一半不需要清单：模板漏进任何一句可执行文本，后缀断言自己会红。</para>
///
/// <para><b>两次拆探针：各咬得住一半，但两册都会红，区别在红的是哪 16 颗</b>（下段那些读数是 v0.38 第十笔
/// 拆探针时的历史读数，那时在场的家数是十六；本笔落 DuckDb 之后逐家那一条各多一户，四位数的口径也随之多一）。把 <c>DialectBase.EmitTableName</c>
/// 那句守卫注释掉 → 本册 <c>34 / 16</c>，红的全是 <see cref="Every_family_refuses_a_template_at_the_naming_hook"/>
/// （逐家一条，后缀那颗与两条 Fact 一条不红），而换算链路那一册 <c>63 / 1</c> —— 唯一陪红的
/// <c>A_template_reaching_the_emitter_is_refused_there</c> 就是这条守卫在别处的第二次断言
/// （<c>obj/logs/v38-10-probe-1a.log</c> ∶ <c>obj/logs/v38-10-probe-1b.log</c>）。把桶号宽度从 <c>D2</c> 改成
/// <c>D0</c> → 本册仍是 <c>34 / 16</c> 而红的换成 <see cref="Nothing_a_family_emits_ever_carries_a_placeholder"/>
/// （后缀那一半，逐家一条），换算那一册 <c>63 / 4</c>，四颗全是桶名形状
/// （<c>obj/logs/v38-10-probe-2.log</c>，一场里两册各一段汇总）。两次都数得出 16，可指认的那一半不同 ——
/// 这就是"两半各有各的牙口"能成立的准确形状：不是"只有一册红"，是同一册里两颗 Fact 各自只被自己的那种改动咬得住。</para></summary>
public sealed class TableNamingEmissionTests
{
    static readonly DateTime January = new(2026, 1, 15, 10, 30, 0);
    static readonly DateTime February = new(2026, 2, 15, 10, 30, 0);

    /// <summary><see cref="SplitRouting"/> 认的四颗占位符。判据用"这四颗在不在文本里"而不是"有没有花括号"：
    /// 后者会把 <c>WithSql</c> 那种原样透出的用户文本也算进来。</summary>
    static readonly string[] Tokens = ["{year}", "{month}", "{day}", "{shard}"];

    static QuickSqlOrm Orm(string family) => family switch
    {
        "SqlServer" => SqlServer.Orm(),
        "MySql" => MySql.Orm(),
        "Oracle" => Oracle.Orm(),
        "PostgreSql" => PostgreSql.Orm(),
        "Kingbase" => Kingbase.Orm(),
        "OpenGauss" => OpenGauss.Orm(),
        "GaussDb" => GaussDb.Orm(),
        "Highgo" => Highgo.Orm(),
        "Dameng" => Dameng.Orm(),
        "ShenTong" => ShenTong.Orm(),
        "Xugu" => Xugu.Orm(),
        "Gbase8s" => Gbase8s.Orm(),
        "Sqlite" => Sqlite.Orm(),
        "TiDb" => TiDb.Orm(),
        "OceanBase" => OceanBase.Orm(),
        "Gbase8c" => Gbase8c.Orm(),
        "DuckDb" => DuckDb.Orm(),
        "ClickHouse" => ClickHouse.Orm(),
        "TDengine" => TDengine.Orm(),
        _ => throw new InvalidOperationException($"没有这一家方言：{family}"),
    };

    /// <summary>十九家 = <see cref="DbKind"/> 的成员数，一颗不多一颗不少：漏一家就是"这家没被量过"，
    /// 而台账上写的却是"这一档全族已接"。</summary>
    public static TheoryData<string> Families()
    {
        var data = new TheoryData<string>();

        foreach (var kind in Enum.GetNames<DbKind>())
            data.Add(kind);

        return data;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_family_refuses_a_template_at_the_naming_hook(string family)
    {
        var dialect = Orm(family).Provider.Dialect;

        foreach (var template in new[] { ShardTemplate(family), TenantTemplate(family) })
        {
            var raised = Checks.Raised<QuickSqlException>(() =>
            {
                Emit(dialect, template);

                return null;
            });

            Assert.Contains("没换算成具体分片就被送去发射", raised.Message);

            // 消息给的是两条出路而不是一句"不支持"：读侧缺的是选片范围，写侧缺的是行上那颗分片键值。
            Assert.Contains("读侧要先给选片范围", raised.Message);
            Assert.Contains("写侧按行的分片键值算目标表", raised.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Nothing_a_family_emits_ever_carries_a_placeholder(string family)
    {
        var orm = Orm(family);

        foreach (var (label, sql, suffixes) in Surfaces(orm, family))
        {
            foreach (var token in Tokens)
                Assert.False(sql.Contains(token, StringComparison.Ordinal),
                    $"{family} 的 {label} 把分表模板原样发了出去（文本里还有 {token}）：{Environment.NewLine}{sql}");

            foreach (var suffix in suffixes)
                Assert.True(sql.Contains(suffix, StringComparison.OrdinalIgnoreCase),
                    $"{family} 的 {label} 里没有换算好的片名后缀 {suffix}：{Environment.NewLine}{sql}");
        }
    }

    /// <summary>一家方言能碰到的<em>全部</em>命名出口，各配它应当落下的片名后缀。
    /// 读、三种写、DDL 两条档各占一格 —— 这一格少一条，那一面就没有被量过。
    /// <b>表是 family-aware 的</b>（v0.39 第二笔：第十八颗 ClickHouse 到场）：写·UPDATE/写·DELETE
    /// 两格在那一家用的是"整格拒发"那一支
    ///（<see href="https://clickhouse.com/docs/sql-reference/statements/delete"/>，变异档 不进写路径），
    /// DDL·时间档那格在那一家用的是"建表遇契约索引整格拒"
    ///（ShardLog 带 <c>[Index(nameof(Level))]</c>，ClickHouse 没有 CREATE INDEX 语句级格）——
    /// 砍三格砍的是发射口的事，不是命名通路的事；发出去的那五格仍然逐家同量。
    /// 另立有一颗 <see cref="ClickHouse_refuses_mutation_and_indexed_ddl"/> 把被砍的三格折成一册点名。
    /// <b>v0.39 第五笔：第十九颗 TDengine 到场，砍的格子比 ClickHouse 多一格</b>：写·UPDATE/写·DELETE
    /// 同一档整格拒（3.x 无传统 UPDATE、DELETE 只认首列时间窗）；DDL·时间档同一档整格拒
    ///（ShardLog 的契约索引）；<b>DDL·哈希档也砍</b>——TenantRow 是 int Id 血统，本家每一张表
    /// 第一列都必须是 TIMESTAMP，键形状校验面在建表前就拒（这一条不是"还没有实现"，是
    /// 建模面的硬约束）。被砍的四格折在 <see cref="TDengine_refuses_mutation_and_family_shaped_ddl"/>。</summary>
    static (string Label, string Sql, string[] Suffixes)[] Surfaces(QuickSqlOrm orm, string family)
    {
        var readOnlySurfaces = new (string Label, string Sql, string[] Suffixes)[]
        {
            ("读·单片", orm.Select<ShardLog>().SplitTables(January, January).ToSql(), ["202601"]),
            ("读·两片", orm.Select<ShardLog>().SplitTables(January, February).ToSql(), ["202601", "202602"]),
            ("读·桶", orm.Select<TenantRow>().SplitTables(0, 1).ToSql(), ["_00", "_01"]),
            ("写·INSERT", orm.Insert(new ShardLog { Id = Guid.Empty, OccurredAt = January, Level = 1 }).Explain().Chunks[0].Sql.Text, ["202601"]),
            // 哈希档是急切求值的那一格（元组数组一构造就跑）：TDengine 在这一格走不到发射——
            // TenantRow 是 int Id 血统，本家的键形状校验面在建表前就拒，所以这一格按家摆、不进共表。
            family == "TDengine" ? default : ("DDL·哈希档", orm.SplitTablesScript<TenantRow>(0, 1), new[] { "_00", "_01" }),
        };

        if (family == "TDengine")
            return readOnlySurfaces[..4];

        if (family == "ClickHouse")
            return readOnlySurfaces;

        return
        [
            .. readOnlySurfaces[..4],
            ("写·UPDATE", orm.Update<ShardLog>().SplitTable(January).Set(o => o.Level, 9).Where(o => o.Id == Guid.Empty).ToSql(), ["202601"]),
            ("写·DELETE", orm.Delete<ShardLog>().SplitTable(January).Where(o => o.Id == Guid.Empty).ToSql(), ["202601"]),
            ("DDL·时间档", orm.SplitTablesScript<ShardLog>(January, February), ["202601", "202602"]),
            .. readOnlySurfaces[4..],
        ];
    }

    /// <summary>上面那两份逐家用例的前提：清单确实就是 <see cref="DbKind"/> 全家。少一家不是"少跑一家"，
    /// 是"这一档在那一家身上从来没被量过"，而消息里那一句 <c>没有这一家方言</c> 只防得住拼错、防不住漏写一整条。</summary>
    [Fact]
    public void Every_db_kind_has_an_orm_and_every_orm_is_in_the_list()
    {
        var kinds = Enum.GetNames<DbKind>().Select(Orm).Select(static orm => orm.Provider.Kind.ToString()).ToArray();

        Assert.Equal(Enum.GetNames<DbKind>().Order(StringComparer.Ordinal).ToArray(), kinds.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(19, kinds.Length);
    }

    /// <summary>被 <see cref="Surfaces"/> 砍掉的那三格，逐格点名：ClickHouse 的 UPDATE/DELETE 两整格
    /// 拒发（官方形状都在变异档【文档·ClickHouse：alter 页与
    /// https://clickhouse.com/docs/sql-reference/statements/delete 】），带契约索引的建表整格拒
    ///（这一族没有 CREATE INDEX 语句级格，索引是建表语句里的数据骨架声明）。三格的共同点：
    /// 拒的是<em>发射口</em>，"名字必须换算好再进"那条通路一格都没豁免 —— 所以砍命名出口量的是
    /// 文本产不出那一格，不是这一条纪律少了一家。</summary>
    [Fact]
    public void ClickHouse_refuses_mutation_and_indexed_ddl()
    {
        var orm = ClickHouse.Orm();

        var update = Checks.Raised<QuickSqlException>(() =>
            orm.Update<ShardLog>().SplitTable(January).Set(o => o.Level, 9).Where(o => o.Id == Guid.Empty).ToSql());

        Assert.Contains("UPDATE 不在写路径上", update.Message);

        var deletion = Checks.Raised<QuickSqlException>(() =>
            orm.Delete<ShardLog>().SplitTable(January).Where(o => o.Id == Guid.Empty).ToSql());

        Assert.Contains("lightweight DELETE", deletion.Message);

        var indexed = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = new SqlWriter(new StringBuilder(), [], orm.Provider.Dialect.ParameterPrefix);

            orm.Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<Order>());

            return null;
        });

        Assert.Contains("拒收契约索引", indexed.Message);
    }

    /// <summary>被 <see cref="Surfaces"/> 砍掉的那四格，逐格点名（TDengine 这一档）：UPDATE/DELETE
    /// 两整格拒发（3.x 无传统 UPDATE【文档·TDengine】、DELETE 只认首列时间窗
    ///（https://docs.tdengine.com/tdengine-sql/08-delete/））；带契约索引的建表整格拒
    ///（本家没有用户索引那一档）；<b>int Id 血统的建表在键形状校验面整格拒</b>——本家每一张表
    /// 第一列都必须是 TIMESTAMP 且是主键（系统自动设），TenantRow 的首列是整型主键，建不出表。
    /// 四格的共同点与前一家同款：拒的是<em>发射口</em>，"名字必须换算好再进"那条通路一格都没豁免
    /// —— 所以砍命名出口量的是文本产不出那一格，不是这一条纪律少了一家。</summary>
    [Fact]
    public void TDengine_refuses_mutation_and_family_shaped_ddl()
    {
        var orm = TDengine.Orm();

        var update = Checks.Raised<QuickSqlException>(() =>
            orm.Update<ShardLog>().SplitTable(January).Set(o => o.Level, 9).Where(o => o.Id == Guid.Empty).ToSql());

        Assert.Contains("没有 UPDATE 语句", update.Message);

        var deletion = Checks.Raised<QuickSqlException>(() =>
            orm.Delete<ShardLog>().SplitTable(January).Where(o => o.Id == Guid.Empty).ToSql());

        Assert.Contains("时间窗", deletion.Message);

        var indexed = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = new SqlWriter(new StringBuilder(), [], orm.Provider.Dialect.ParameterPrefix);

            orm.Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<Order>());

            return null;
        });

        Assert.Contains("拒收契约索引", indexed.Message);

        var intId = Checks.Raised<QuickSqlException>(() =>
        {
            var writer = new SqlWriter(new StringBuilder(), [], orm.Provider.Dialect.ParameterPrefix);

            orm.Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<TenantRow>());

            return null;
        });

        Assert.Contains("第一列都必须是 TIMESTAMP", intId.Message);
    }

    /// <summary>收 <see cref="TableInfo"/> 的接口成员是一个<em>闭集</em>，十二颗各有角色：DML 侧只有
    /// <see cref="ISqlDialect.EmitTableName"/> 一颗，<c>WriteTargetAlias</c> 是同一条分岔的别名那半
    /// （Oracle 血统的自引用名就是表名，所以它也得收这颗值）；<c>ICodeFirstDialect</c> 那六颗只可能被喂换算好的片，
    /// 那一头由 <see cref="Nothing_a_family_emits_ever_carries_a_placeholder"/> 的 DDL 两格量；
    /// <c>IdentityInsertToggle</c> 本版本不可达（§12.4），它现在是名单上的一处<em>已知敞口</em>而不是漏网；
    /// 读系统表那两颗把名字放进绑定参数、不进文本。新开第十一颗就是"又开了一条命名的路"，必须人来认账。
    /// <b>本条认的账第一颗是 <c>TryEmitDropIndex</c></b>：§9.5 结构同步"库里多出的那条索引要不要删"
    /// 那一档开的（<c>SchemaSyncDropIndexes</c>，默认关，用户 2026-10-06 当面定的那一颗）。
    /// 它带默认实现（false = 这一家不接），所以今天只有 SQL Server 一家覆写；它把表名写进的是
    /// <c>DROP INDEX … ON 表</c> 的表名那一格，走 <c>EmitTableName</c> 同一条发射路径，不另造口径。
    /// <para><b>第十二颗是 <c>ColumnHasNullsAsync</c></b>（v0.39）：结构同步要把一列<em>收紧</em>成
    /// <c>NOT NULL</c> 之前问的那一句"库里有没有空值"——问的必须是<em>这一张表的这一列</em>，所以表名进名单。
    /// 它收的是<em>带引号的发射字形</em>而不是绑定参数（对象名参数化不了），与读系统表那两颗同一格纪律：
    /// 拼的是标识符，拼不进值。带默认实现（<c>null</c> = 这一家答不出，那一句照发并欠一行"判不了"），
    /// 今天只有 PG 一族覆写。三臂的读数在 <see cref="NullabilityTighteningTests"/>。</para></summary>
    [Fact]
    public void The_abstraction_surface_takes_a_table_in_exactly_the_named_members()
    {
        var found = typeof(ISqlDialect).Assembly.DefinedTypes
            .Where(static type => type.IsInterface)
            .SelectMany(static type => type.GetMethods()
                .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TableInfo)))
                .Select(method => type.Name + "." + method.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "ICodeFirstDialect.EmitAddColumn",
            "ICodeFirstDialect.EmitAlterColumnPrecision",
            "ICodeFirstDialect.EmitCreateIndex",
            "ICodeFirstDialect.TryEmitColumnComment",
            "ICodeFirstDialect.TryEmitDropIndex",
            "ICodeFirstDialect.TryEmitTableComment",
            "IKeyStrategy.IdentityInsertToggle",
            "ISchemaReader.ColumnHasNullsAsync",
            "ISchemaReader.ProbeLengthSemanticsAsync",
            "ISchemaReader.ReadTableAsync",
            "ISqlDialect.EmitTableName",
            "ISqlDialect.WriteTargetAlias",
        ], found);
    }

    static TableInfo ShardTemplate(string family) => Orm(family).Schemas.Contract<ShardLog>().Table;

    static TableInfo TenantTemplate(string family) => Orm(family).Schemas.Contract<TenantRow>().Table;

    /// <summary><see cref="SqlWriter"/> 是 <c>ref struct</c>，装不进 lambda 的捕获，所以那一句过这颗本地函数。</summary>
    static void Emit(ISqlDialect dialect, TableInfo table)
    {
        var writer = new SqlWriter(new StringBuilder(), [], dialect.ParameterPrefix);

        dialect.EmitTableName(writer, table);
    }
}
