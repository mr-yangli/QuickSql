using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Core.Schema;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Providers.Gbase8s;
using QuickSql.Providers.MySqlBase;
using QuickSql.Providers.PostgreSqlBase;
using QuickSql.Providers.SqlServer;
using QuickSql.Providers.Sqlite;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§9.5 列级结构同步的离线半：把"库里现形状"手写成读数，比出计划，再断言发射文本。
/// 这一格必须能离线穷举——真库上一趟同步只走一种形状，而这里的十几种形状各只对应一句要发的 DDL
/// 或一句<em>不该</em>发的。幂等裁判（<see cref="The_current_shape_reads_back_as_no_change"/>）尤其如此：
/// 它断言的是归一，而"第二趟零动作"在库上只是这条归一的一个读数。</summary>
public sealed class StructureSyncTests
{
    [Table("QUICKSQL_SHAPE")]
    [Description("形状表")]
    [Index("Title")]
    sealed class Shape
    {
        public int Id { get; set; }

        [QuickColumn(Length = 32)]
        [Description("标题")]
        public string? Title { get; set; }

        public decimal Amount { get; set; }

        [QuickColumn(Precision = 10, Scale = 2)]
        public decimal Price { get; set; }

        public DateTime CreatedAt { get; set; }

        public int Quantity { get; set; }
    }

    /// <summary>契约里一个字备注都没写的实体：它专门盯"null 不等于要求清空"那一条。</summary>
    [Table("QUICKSQL_PLAIN")]
    sealed class Plain
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }

    /// <summary>默认值那一格的靶子：NOT NULL 整型与可空字符串各一列（System.ComponentModel 的
    /// <c>[DefaultValue]</c> —— 与反射侧同一颗注解，G1 侧同形）。只归本册的默认值两颗用例写。</summary>
    [Table("QUICKSQL_DEFAULT_SHAPE")]
    sealed class DefaultShape
    {
        public int Id { get; set; }

        [DefaultValue(7)]
        public int Reserved { get; set; }

        [DefaultValue("待定")]
        [QuickColumn(Length = 16)]
        public string? Note { get; set; }
    }

    static readonly IDbProvider Sql = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
    static readonly ISchemaReader Reader = new SqlServerSchemaReader();

    // PG 家那一档的两位被告：本家的判据（归一尺）与发射（两句 DDL）。provider 走 TestInfra 那颗组装根、
    // 工厂给 NoDriverFactory——离线这一册一句都不该连库，碰了就该红在这里。
    static readonly IDbProvider Pg = PostgreSql.Provider(SqlServer.NoDriverFactory.Instance);
    static readonly ISchemaReader PgReader = new PostgreSqlSchemaReader();

    // MySQL 家那一档的两位被告，同一对位。字段名取 <c>My</c> 是为了与 <c>Pg</c> 对称而<em>不</em>叫
    // <c>MySql</c>：类里放一颗和类型同名的字段会把 <c>MySql.Provider(…)</c> 那句组装遮掉。
    static readonly IDbProvider My = MySql.Provider(SqlServer.NoDriverFactory.Instance);
    static readonly ISchemaReader MyReader = new MySqlSchemaReader();

    static EntityContract Contract<T>() where T : class
        => new EntitySchemaRegistry(new ContractResolver(Sql)).Contract<T>();

    static EntityContract PgContract<T>() where T : class
        => new EntitySchemaRegistry(new ContractResolver(Pg)).Contract<T>();

    static EntityContract MyContract<T>() where T : class
        => new EntitySchemaRegistry(new ContractResolver(My)).Contract<T>();

    // ── 归一：现形状读回来必须算"没变化" ────────────────────────────────

    /// <summary>把契约的现形状按<em>目录会报的字形</em>读回来：小写、<c>numeric</c> 而不是 <c>decimal</c>、
    /// 时间类型的精度在标度那一格而不在类型名里。零动作是这一格的裁判，而它红一次就意味着下一版
    /// 真库同步会在一张根本没变过的表上发一句 <c>ALTER</c>。</summary>
    [Fact]
    public void The_current_shape_reads_back_as_no_change()
    {
        var plan = Diff(CurrentStore());

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.NotJudged);
        Assert.Empty(plan.Refused);
    }

    /// <summary>目录字形与发射字形的四种对应。折不进来的话每一家都会在自己的表上找到假差：</summary>
    [Theory]
    [InlineData("datetime2(7)", "DATETIME2(7)")]
    [InlineData("nvarchar(max)", "NVARCHAR(MAX)")]
    [InlineData("varbinary(max)", "VARBINARY(MAX)")]
    [InlineData("binary(8)", "rowversion")]
    [InlineData("numeric(18,4)", "DECIMAL(18,4)")]
    public void Catalog_spelling_and_emitted_spelling_are_the_same_shape(string fromCatalog, string emitted)
        => Assert.True(Reader.ColumnShapeMatches(Declared(emitted), Col("C", fromCatalog)));

    // ── 六格：哪种形状差发哪一句 ────────────────────────────────────────

    /// <summary>缺列补列，顺带补它欠的备注与落在它上面的那条索引——三句在同一趟里，
    /// 因为 <c>CREATE INDEX</c> 引用的列必须已经存在。</summary>
    [Fact]
    public void A_column_the_store_does_not_have_is_added_with_its_comment_and_index()
    {
        // 索引读数为空是这份夹具唯一自洽的摆法：库里那一列都不在，跨它的索引不可能存在。
        var plan = Diff(CurrentStore(drop: "Title"), indexes: []);

        Assert.Equal(["Title"], plan.AddColumns.Select(c => c.ColumnName));
        Assert.Empty(plan.AlterColumns);
        Assert.Equal(["Title"], plan.ColumnComments.Select(c => c.ColumnName));
        Assert.Equal(["IX_QUICKSQL_SHAPE_Title"], plan.CreateIndexes.Select(i => i.Name));
    }

    /// <summary>标度与长度走的都是 <c>ALTER COLUMN</c> 那一句：T-SQL 没有"只改标度"的语法，
    /// 整列定义重写是唯一的形状（所以可空性也一起被重写，见 <see cref="Nullability_alone_alters"/>）。</summary>
    [Fact]
    public void A_scale_change_is_an_alter()
        => Assert.Equal(["Amount"], Diff(CurrentStore(Amount: "numeric(18,2)")).AlterColumns.Select(c => c.ColumnName));

    [Fact]
    public void A_length_change_is_an_alter()
        => Assert.Equal(["Title"], Diff(CurrentStore(Title: "nvarchar(64)")).AlterColumns.Select(c => c.ColumnName));

    [Fact]
    public void Nullability_alone_alters()
        => Assert.Equal(["Title"], Diff(CurrentStore(titleNullable: false)).AlterColumns.Select(c => c.ColumnName));

    // ── 收紧那一档：哪几句 ALTER 要先到库里问一句"有没有空值" ────────────
    //
    // 这一份子集单独有测试是因为它的判据与 AlterColumns 那一格只差一个方向，而方向错了整格的后果
    // 不一样：放宽那一句永远撞不出 23502，收紧那一句在一列有存量空值时必撞。两者混在一份清单里，
    // 执行层就只能靠"发出去试试"分辨，而那一试落在启动那一趟上是宿主起不来。

    /// <summary>库里可空而契约要它非空 → 这一句 <c>ALTER</c> 是收紧，进要问库的那份清单。
    /// 形状两边一致，钉的是"只有可空性这一格差"也会走探针——那正是 <c>ConcurrencyStamp</c> 那一颗现场。</summary>
    [Fact]
    public void A_tightened_column_is_named_for_the_null_probe()
    {
        var plan = Diff(CurrentStore(amountNullable: true));

        Assert.Equal(["Amount"], plan.AlterColumns.Select(c => c.ColumnName));
        Assert.Equal(["Amount"], plan.NullabilityTightenings.Select(c => c.ColumnName));
    }

    /// <summary>反方向（契约可空而库里非空）<em>不</em>进这份清单：那一句是把约束放松，
    /// 库里有多少行都收得下。它仍然是一句 <c>ALTER</c>，只是不欠库里一句问话。</summary>
    [Fact]
    public void A_loosened_column_is_not_probed()
    {
        var plan = Diff(CurrentStore(titleNullable: false));

        Assert.Equal(["Title"], plan.AlterColumns.Select(c => c.ColumnName));
        Assert.Empty(plan.NullabilityTightenings);
    }

    /// <summary>主键列那一格被拒（不改主键）时<em>也不</em>进这份清单：执行层根本拿不到那一句，
    /// 为一个不会发的句子去库里数空值是白跑一趟。同一判据盖住"值归库给"那一族。</summary>
    [Fact]
    public void A_refused_column_is_neither_altered_nor_probed()
    {
        var columns = CurrentStore();
        columns[0] = columns[0] with { IsNullable = true };

        var plan = Diff(columns);

        Assert.Empty(plan.AlterColumns);
        Assert.Empty(plan.NullabilityTightenings);
        Assert.Contains(plan.Refused, r => r.Contains("列 Id ", StringComparison.Ordinal));
    }

    /// <summary>被 ALTER 的那一列<em>同时</em>进备注清单：T-SQL 的 <c>ALTER COLUMN</c> 会连带删掉那一列的
    /// 扩展属性。少了这一条，"改了长度"这一趟以"备注没了"收尾，而下一趟读到空备注只会补备注，
    /// 永远不会说这一趟丢过它——幂等裁判照样绿，那是假绿。</summary>
    [Fact]
    public void An_altered_column_carries_its_comment_back_into_the_plan()
        => Assert.Equal(["Title"], Diff(CurrentStore(Title: "nvarchar(64)")).ColumnComments.Select(c => c.ColumnName));

    [Fact]
    public void A_changed_comment_is_re_emitted_without_an_alter()
    {
        var plan = Diff(CurrentStore(titleComment: "旧标题"));

        Assert.Empty(plan.AlterColumns);
        Assert.Equal(["Title"], plan.ColumnComments.Select(c => c.ColumnName));
    }

    [Fact]
    public void A_changed_table_comment_is_re_emitted()
        => Assert.True(Diff(CurrentStore(), comment: "旧的表备注").EmitTableComment);

    /// <summary>契约没写备注（null）而库里写着一条：<b>一条都不发</b>。null 说的是"用户没说过"，
    /// 把它读成"要求清空"就是让同步去删人手写进库里的东西（列与表两格同一条判据）。</summary>
    [Fact]
    public void A_comment_the_contract_never_declared_is_left_alone()
    {
        var contract = Contract<Plain>();
        var store = new SchemaTable(contract.Table, [
            Col("Id", "int", identity: true, pk: true),
            Col("Name", "nvarchar(max)", nullable: true, comment: "库里手写的那一条"),
        ])
        { Comment = "库里手写的那一条", Indexes = [] };

        var plan = StructureDiff.Build(contract, store, Reader, IdentifierPolicy.SqlServer);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.ColumnComments);
        Assert.False(plan.EmitTableComment);
    }

    // ── 三条拒：读得出而故意不发 ────────────────────────────────────────

    /// <summary>非空的新列：库里已有的行没有值可填，而契约里根本没有默认值那一格（§10.3 的字段清单是闭集），
    /// 所以 Core 变不出一句 <c>DEFAULT</c>。硬发的后果是"这一趟在第 N 张表上响，而前 N-1 张已经改完"。</summary>
    [Fact]
    public void A_not_null_column_with_no_default_is_refused_and_named()
    {
        var plan = Diff(CurrentStore(drop: "Quantity"));

        Assert.Empty(plan.AddColumns);
        Assert.Single(plan.Refused, r => r.Contains("Quantity", StringComparison.Ordinal) && r.Contains("NOT NULL", StringComparison.Ordinal));
    }

    /// <summary>非空的新列<b>配了默认值</b>（2026-11-01 补的那一格）：
    /// <c>ADD COLUMN … DEFAULT … NOT NULL</c> 让库自己把存量行填上，那一格从"永远拒"翻成"放行"。
    /// 可空列配默认值同场放行 —— 默认值不是 NOT NULL 的专利，只是救得回 NOT NULL 那一档。</summary>
    [Fact]
    public void A_not_null_column_with_a_declared_default_is_added()
    {
        var contract = Contract<DefaultShape>();
        var plan = StructureDiff.Build(contract, new SchemaTable(contract.Table,
        [
            Col("Id", "int", identity: true, pk: true),
        ])
        { Indexes = [] }, Reader, IdentifierPolicy.SqlServer);

        Assert.Empty(plan.Refused);
        Assert.Equal(["Reserved", "Note"], plan.AddColumns.Select(c => c.ColumnName));

        // 发射半边：DEFAULT 落在补列那句里（T-SQL 的列定义序：NOT NULL 在前、DEFAULT 在后）。
        var reserved = Assert.Single(plan.AddColumns, c => c.ColumnName == "Reserved");
        var sql = Statement(w => Sql.CodeFirst.EmitAddColumn(w, contract.Table, reserved)).ToString();

        Assert.Contains("DEFAULT 7", sql, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", sql, StringComparison.Ordinal);
    }

    /// <summary>默认值不参与形状补差比对：库侧读不回默认值（<c>SchemaColumn</c> 没有这一格），
    /// 所以"契约写了默认值而列已在库里"就是没变化 —— 不为它发一句 ALTER，也不进任何清单。</summary>
    [Fact]
    public void A_declared_default_does_not_make_the_existing_column_drift()
    {
        var contract = Contract<DefaultShape>();
        var plan = StructureDiff.Build(contract, new SchemaTable(contract.Table,
        [
            Col("Id", "int", identity: true, pk: true),
            Col("Reserved", "int"),
            Col("Note", "nvarchar(16)", nullable: true),
        ])
        { Indexes = [] }, Reader, IdentifierPolicy.SqlServer);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void An_extra_store_column_is_never_dropped()
    {
        var plan = Diff([.. CurrentStore(), Col("Legacy", "ntext")]);

        Assert.Single(plan.Refused, r => r.Contains("Legacy", StringComparison.Ordinal) && r.Contains("不删列", StringComparison.Ordinal));
    }

    /// <summary>主键列与自增列的形状差一律拒：改它们要连约束、外键与取值机制一起重建，
    /// 那是比"改一颗标度"贵一个数量级的动作。两格分开钉：形状差、与主键成员差。</summary>
    [Fact]
    public void An_identity_column_with_a_different_type_is_refused()
    {
        var plan = Diff(CurrentStore(Id: "bigint"));

        Assert.Empty(plan.AlterColumns);
        Assert.Contains(plan.Refused, r => r.Contains("Id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_different_primary_key_membership_is_refused()
    {
        var plan = Diff(CurrentStore(pkOn: "Amount"));

        Assert.Contains(plan.Refused, r => r.Contains("主键不一致", StringComparison.Ordinal));
    }

    [Fact]
    public void An_extra_store_index_is_never_dropped()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("IX_extra_on_amount", false, ["Amount"]),
        ]);

        Assert.Empty(plan.CreateIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_extra_on_amount", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_name_already_taken_by_other_columns_is_not_overwritten()
    {
        var plan = Diff(CurrentStore(), indexes: [new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Amount"])]);

        Assert.Empty(plan.CreateIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_QUICKSQL_SHAPE_Title", StringComparison.Ordinal));
    }

    /// <summary>人给索引改过名而列没换：<b>那不算缺索引</b>。按名字判的那把尺会在每次改名后重建一条，
    /// 而库里从此有两条同列的索引。</summary>
    [Fact]
    public void An_index_renamed_but_on_the_same_columns_is_present()
    {
        var plan = Diff(CurrentStore(), indexes: [new SchemaIndex("IX_shape_title_v2", false, ["Title"])]);

        Assert.Empty(plan.CreateIndexes);
        Assert.Empty(plan.Refused);
    }

    // ── 删索引：另一颗开关，默认关 ─────────────────────────────────────

    /// <summary>开了那一档，库里多出的那一条就进删除清单，而<em>不再</em>进"要人工"：同一件事报两遍
    /// 会让"这一趟到底删不删"在报告里读不出来。补建那一半一格不动。</summary>
    [Fact]
    public void An_extra_store_index_is_dropped_when_the_switch_is_on()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("IX_extra_on_amount", false, ["Amount"]),
        ], dropIndexes: true);

        Assert.Equal(["IX_extra_on_amount"], plan.DropIndexes.Select(i => i.Name));
        Assert.Empty(plan.CreateIndexes);
        Assert.Empty(plan.Refused);
    }

    /// <summary>默认那一档下<em>删除清单必须是空的</em>：这一条与上面那一条是同一份读数、只差一颗开关，
    /// 它钉的是"没开就是不发"，而不是"没开也发了但没记进清单"。</summary>
    [Fact]
    public void The_default_switch_leaves_the_extra_index_in_the_manual_list()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("IX_extra_on_amount", false, ["Amount"]),
        ]);

        Assert.Empty(plan.DropIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_extra_on_amount", StringComparison.Ordinal));
    }

    /// <summary>唯一约束即便开了那一档也不删：<c>DROP INDEX</c> 删不动约束，发出去只会以报错收尾，
    /// 而这一趟的其余句子已经发出去了——那是"半套状态"，比不删坏得多。它进"要人工"并指名它是约束。</summary>
    [Fact]
    public void A_unique_constraint_is_not_dropped_even_with_the_switch_on()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("UQ_shape_amount", true, ["Amount"]) { IsConstraint = true },
        ], dropIndexes: true);

        Assert.Empty(plan.DropIndexes);
        Assert.Single(plan.Refused, r => r.Contains("UQ_shape_amount", StringComparison.Ordinal) && r.Contains("约束", StringComparison.Ordinal));
    }

    /// <summary>撞名的那一条（库里那颗同名索引管的是别的列）<b>不能被顺手删掉再按契约建一条</b>：
    /// 删了它那一列上的查询当场失去索引，而这一趟紧接着建的又是另一颗。开关开不开，这一格都是"要人工"。</summary>
    [Fact]
    public void A_name_clash_is_still_manual_with_the_switch_on()
    {
        var plan = Diff(CurrentStore(), indexes: [new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Amount"])], dropIndexes: true);

        Assert.Empty(plan.DropIndexes);
        Assert.Empty(plan.CreateIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_QUICKSQL_SHAPE_Title", StringComparison.Ordinal));
    }

    /// <summary>这一家读不回索引清单（<c>Indexes</c> 为 null）时，<b>开了删索引那一档也一条不删</b>：
    /// "读不出"不是"库里没有"，把 null 当成空清单就是删掉那一张表上的全部索引。判不了那一句照旧指名。</summary>
    [Fact]
    public void A_family_that_cannot_read_indexes_never_drops_any()
    {
        var contract = Contract<Shape>();
        var blind = new SchemaTable(contract.Table, CurrentStore()) { Comment = "形状表" };

        var plan = StructureDiff.Build(contract, blind, Reader, IdentifierPolicy.SqlServer, dropIndexes: true);

        Assert.Empty(plan.DropIndexes);
        Assert.Contains(plan.NotJudged, n => n.Contains("索引清单", StringComparison.Ordinal));
    }

    /// <summary>开了那一档而形状读回来正是契约那一份：幂等裁判不变红。这一条是"删了又建、建了又删"
    /// 那种来回摆动的钉——同列序列换了名也算同一条，所以改名不会因这一档而被删。</summary>
    [Fact]
    public void The_current_index_list_with_the_switch_on_is_still_no_change()
    {
        var plan = Diff(CurrentStore(), dropIndexes: true);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.DropIndexes);
    }

    // ── PG 家：五档从"判不了"换成有读数 ───────────────────────────────

    /// <summary>本家自这一笔起是<em>第二家</em>五档全读的库（第一家是上面那批用例用的 T-SQL）。
    /// 这一格钉的不是 diff 判据——判据与发射都是同一套 Core，与家无关——而是<em>本家那份归一</em>：
    /// 目录交回的字形（小写、<c>varchar(32)</c> 而不是 <c>VARCHAR(32)</c>、时刻的位数在
    /// <c>datetime_precision</c> 那一格）必须折成契约发射的那一个词，折不动就逐列假红，
    /// 折过头了（比如把 <c>numeric</c> 与 <c>numeric(18,4)</c> 也折成同一型）就是"改了标度永远同步不上"。
    /// <b>五档一份都不进清单</b>是这一格的正面断言：这一家现在主键、形状、备注、索引、表备注全都读得回来。</summary>
    [Fact]
    public void The_pg_family_with_a_full_catalog_reading_reports_nothing_not_judged()
    {
        var contract = PgContract<Shape>();
        var plan = StructureDiff.Build(contract, PgStore(contract.Table), PgReader, IdentifierPolicy.PostgreSQL);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.NotJudged);
        Assert.Empty(plan.Refused);
    }

    /// <summary>本家目录与发射形之间真正的落差只有三颗：大小写、<c>varchar</c> 那颗简称、
    /// 时刻位数藏在哪一格。<c>int4</c> 那一行是这一族的别名位（<c>pg_type</c> 交 <c>int4</c> 而
    /// <c>information_schema</c> 交 <c>integer</c>，这一族各家今天报的是哪一颗没有一台活库能证），
    /// 折进来是为了下一家叶子的目录换了字形时不必再改判据。</summary>
    [Theory]
    [InlineData("varchar(32)", "VARCHAR(32)")]
    [InlineData("text", "TEXT")]
    [InlineData("integer", "INTEGER")]
    [InlineData("int4", "INTEGER")]
    [InlineData("numeric(18,4)", "NUMERIC(18,4)")]
    [InlineData("boolean", "BOOLEAN")]
    [InlineData("timestamp(3)", "TIMESTAMP(3)")]
    [InlineData("timestamptz(3)", "TIMESTAMPTZ(3)")]
    [InlineData("time(3)", "TIME(3)")]
    [InlineData("double precision", "DOUBLE PRECISION")]
    [InlineData("char(1)", "CHAR(1)")]
    public void The_pg_catalog_spelling_and_emitted_spelling_are_the_same_shape(string fromCatalog, string emitted)
        => Assert.True(PgReader.ColumnShapeMatches(Declared(emitted), Col("C", fromCatalog)));

    /// <summary>反面三行：归一尺不能把<em>真有差</em>的两形折成同一形。裸 <c>numeric</c> 与
    /// <c>numeric(18,4)</c> 在库里确实是两种约束（前者不检位数），<c>timestamp(6)</c> 与
    /// <c>timestamp(3)</c> 是两位小数秒的差——放走任何一条，用户点名的"改了小数长度"就同步不上。</summary>
    [Theory]
    [InlineData("numeric", "NUMERIC(18,4)")]
    [InlineData("timestamp(6)", "TIMESTAMP(3)")]
    [InlineData("varchar(64)", "VARCHAR(32)")]
    public void The_pg_fold_keeps_a_real_difference_apart(string fromCatalog, string emitted)
        => Assert.False(PgReader.ColumnShapeMatches(Declared(emitted), Col("C", fromCatalog)));

    /// <summary>本家那份读数摆歪一颗标度：改的那一列进 <c>AlterColumns</c> 而不是进"要人工"，
    /// 而它同时进备注清单——这一条与 T-SQL 那一家同一判据，写在这里是证<em>本家的归一没有把
    /// "标度差"折成"没差"</em>。</summary>
    [Fact]
    public void A_pg_scale_change_is_an_alter_too()
    {
        var contract = PgContract<Shape>();
        var store = PgStore(contract.Table, amount: "numeric(18,2)");

        var plan = StructureDiff.Build(contract, store, PgReader, IdentifierPolicy.PostgreSQL);

        Assert.Equal(["Amount"], plan.AlterColumns.Select(c => c.ColumnName));
        Assert.Empty(plan.NotJudged);
    }

    /// <summary>本家的表备注是一句原生 <c>COMMENT ON TABLE</c>：与 T-SQL 那一大段"有则改、无则加"相反，
    /// 这一家没有<em>属性</em>这一层，备注是目录里的一颗字段，所以一句就幂等。整句写成等值断言，
    /// 是因为模式名那一颗（<c>public</c> 而不是 <c>dbo</c>）只有逐字比才看得见。</summary>
    [Fact]
    public void The_PG_table_comment_statement_is_a_single_COMMENT_ON_TABLE()
    {
        var sql = Statement(w => Assert.True(Pg.CodeFirst.TryEmitTableComment(w, PgContract<Shape>().Table, "形状表"))).ToString();

        Assert.Equal("""COMMENT ON TABLE "public"."QUICKSQL_SHAPE" IS '形状表'""", sql);
    }

    /// <summary>本家的删索引<em>没有</em> <c>ON 表名</c>那一截，而索引名带着模式：这一家的索引是目录里的
    /// 独立对象，不是表的一个成员。这一句与 T-SQL 那一形逐字比，是因为"顺手把 <c>ON</c> 抄过来"
    /// 是本家最容易写错的那一笔，而它在文本层看不出问题、只在执行时响。</summary>
    [Fact]
    public void The_PG_drop_index_statement_names_the_index_and_no_table()
    {
        var sql = Statement(w => Assert.True(
            Pg.CodeFirst.TryEmitDropIndex(w, PgContract<Shape>().Table, new SchemaIndex("IX_extra_on_amount", false, ["Amount"])))).ToString();

        Assert.Equal("DROP INDEX \"public\".\"IX_extra_on_amount\"", sql);
    }

    /// <summary>本家的开档删索引走的是同一个计划层：库里多出的那一条在开关开时进删除清单，
    /// 而唯一约束那条不进——本家的 <c>DROP INDEX</c> 同样删不动约束（它的支撑索引与约束同名，
    /// 删它要 <c>ALTER TABLE … DROP CONSTRAINT</c>）。这一格钉的是"读侧那颗 <c>IsConstraint</c>
    /// 在本家也交得回来"，判据本身在 Core 只有一份。</summary>
    [Fact]
    public void A_pg_unique_constraint_is_still_manual_with_the_switch_on()
    {
        var contract = PgContract<Shape>();
        var store = PgStore(contract.Table, indexes: [
            new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("UQ_shape_amount", true, ["Amount"]) { IsConstraint = true },
            new SchemaIndex("IX_extra_on_quantity", false, ["Quantity"]),
        ]);

        var plan = StructureDiff.Build(contract, store, PgReader, IdentifierPolicy.PostgreSQL, dropIndexes: true);

        Assert.Equal(["IX_extra_on_quantity"], plan.DropIndexes.Select(i => i.Name));
        Assert.Single(plan.Refused, r => r.Contains("UQ_shape_amount", StringComparison.Ordinal) && r.Contains("约束", StringComparison.Ordinal));
    }

    // ── MySQL 家：五档从"判不了"换成有读数 ────────────────────────────

    /// <summary>本家自这一笔起是<em>第三家</em>五档全读的库（前两家是上面那两批用例用的 T-SQL 与 PG）。
    /// 钉法与 PG 那一格同一套，但本家多一处只有它有的东西：<b>整数族那一条摘宽度</b>。契约侧的布尔发
    /// <c>TINYINT(1)</c>，而目录的 <c>data_type</c> 交回 <c>tinyint</c>（整数的显示宽度在 8.0.17 起已废弃、
    /// 目录里从来不含它），不摘那一颗括号就是"每一趟都在布尔列上发一句 <c>MODIFY COLUMN</c>、
    /// 而永远配不平"——这是五家里只有这一家会撞上的形状。反面那几行钉的是同一把尺子<em>不能</em>折过头。</summary>
    [Fact]
    public void The_mysql_family_with_a_full_catalog_reading_reports_nothing_not_judged()
    {
        var contract = MyContract<Shape>();
        var plan = StructureDiff.Build(contract, MyStore(contract.Table), MyReader, IdentifierPolicy.MySql);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.NotJudged);
        Assert.Empty(plan.Refused);
    }

    /// <summary>本家目录与发射形之间的落差有三颗：大小写、整数族的显示宽度、同义词
    /// （<c>integer</c>/<c>numeric</c>/<c>real</c>/<c>bool</c> 都是手工建表时人写得出来而目录不那么报的名字）。
    /// <c>INT(11)</c> 那一行是 5.7 时代留下的形状——<b>它出现在<em>契约</em>侧而库里读回 <c>int</c></b>，
    /// 不折就每一趟改一次那一列而永远配不平。时刻的位数在这一家同样不在型名里（在
    /// <c>datetime_precision</c>），与 PG 家同一处境、同一个折法。</summary>
    [Theory]
    [InlineData("varchar(32)", "VARCHAR(32)")]
    [InlineData("text", "TEXT")]
    [InlineData("int", "INT")]
    [InlineData("integer", "INT")]
    [InlineData("int", "INT(11)")]
    [InlineData("smallint", "SMALLINT")]
    [InlineData("bigint", "BIGINT")]
    [InlineData("tinyint", "TINYINT(1)")]
    [InlineData("tinyint", "BOOL")]
    [InlineData("decimal(18,4)", "DECIMAL(18,4)")]
    [InlineData("decimal(18,4)", "NUMERIC(18,4)")]
    [InlineData("datetime(3)", "DATETIME(3)")]
    [InlineData("time(3)", "TIME(3)")]
    [InlineData("double", "DOUBLE")]
    [InlineData("double", "REAL")]
    [InlineData("char(36)", "CHAR(36)")]
    public void The_mysql_catalog_spelling_and_emitted_spelling_are_the_same_shape(string fromCatalog, string emitted)
        => Assert.True(MyReader.ColumnShapeMatches(Declared(emitted), Col("C", fromCatalog)));

    /// <summary>非正数的声明宽度在收尾那一点折成"未声明"，六家发射侧因此拿不到负数。
    /// 这一格在 MySQL 上的形状是 <c>VARCHAR(-1)</c>——过不了语法关，一整趟结构同步以一句
    /// <c>MySqlException</c> 收尾（真库上撞过的那一种）；反射档更糟，<c>StringLengthAttribute</c>
    /// 的构造函数自己就拒负数，同一条注解在两条路上一条发非法 DDL、一条连契约都建不出来。
    /// 正面第三行钉的是折算<em>不能</em>过头：给了 32 就还是 <c>VARCHAR(32)</c>。</summary>
    [Theory]
    [InlineData(-1, "TEXT")]
    [InlineData(0, "TEXT")]
    [InlineData(32, "VARCHAR(32)")]
    public void A_non_positive_declared_width_is_no_width_at_all(int declared, string emitted)
    {
        var column = new ColumnFacts
        {
            MemberName = "Images",
            ClrType = typeof(string),
            ColumnName = "Images",
            ColumnNameLayer = MetadataLayer.Convention,
            ColumnNameNote = "约定：属性名",
            MaxChars = declared,
            LengthNote = $"[StringLength({declared})]",
            IsNullable = true,
            NullableNote = "C# 可空 ⇒ 可空列",
            IsPrimary = false,
        }.ToProvisional(new TableInfo(null, "QUICKSQL_SHAPE"));

        Assert.Equal(declared > 0 ? declared : (int?)null, column.MaxChars);
        Assert.Equal(emitted, MySqlTypes.Instance.GetStoreType(column));
    }

    /// <summary>反面六行：摘宽度那一手不能顺手把<em>语义</em>也摘掉。标度（两位小数秒与四位小数是两种约束）、
    /// 裸 <c>decimal</c> 与带位数的 <c>decimal(18,4)</c>（库里默认 <c>DECIMAL(10,0)</c>，与契约那份 18,4
    /// 是两个约束）、<c>varchar</c> 的宽度、整数族彼此（<c>int</c> 换 <c>bigint</c> 是真换型）——
    /// 放走任何一条，用户点名的"改了小数长度"就在这一家同步不上。
    /// <b>最后一行是长文本那一族的</b>：<c>tinytext/text/mediumtext/longtext</c> 四档在这一家是<em>四个型</em>
    /// 而不是排版（它们的上限差四个数量级），所以 <c>Normalize</c> 不在这里动手。库里手工建出一列
    /// <c>LONGTEXT</c> 而契约写的是无长度 <c>string</c>（本家发 <c>TEXT</c>）时，这一趟会把它进
    /// <c>AlterColumns</c>，而那句 <c>MODIFY COLUMN … TEXT</c> 撞上装不进 64 KB 的旧数据时会红在库而不是
    /// 红在这一趟的判据里（厂商文档口径：严格模式报"数据过长"；这一颗本家今天没量过）。判据这一侧要的只是
    /// "四档不折成一档"。</summary>
    [Theory]
    [InlineData("decimal(18,2)", "DECIMAL(18,4)")]
    [InlineData("decimal(10,0)", "DECIMAL(18,4)")]
    [InlineData("datetime(0)", "DATETIME(3)")]
    [InlineData("varchar(64)", "VARCHAR(32)")]
    [InlineData("int", "BIGINT")]
    [InlineData("longtext", "TEXT")]
    public void The_mysql_fold_keeps_a_real_difference_apart(string fromCatalog, string emitted)
        => Assert.False(MyReader.ColumnShapeMatches(Declared(emitted), Col("C", fromCatalog)));

    /// <summary>本家那份读数摆歪一颗标度：改的那一列进 <c>AlterColumns</c> 而不是进"要人工"，
    /// 而它同时进备注清单——因为本家改精度走 <c>MODIFY COLUMN</c> 那一句整列重写，而重写会连备注一起换，
    /// 所以备注必须跟着回补（同 T-SQL 与 PG 那两家的同一条判据，写在这里是证<em>本家的归一
    /// 没有把"标度差"折成"没差"</em>）。</summary>
    [Fact]
    public void A_mysql_scale_change_is_an_alter_too()
    {
        var contract = MyContract<Shape>();
        var store = MyStore(contract.Table, amount: "decimal(18,2)");

        var plan = StructureDiff.Build(contract, store, MyReader, IdentifierPolicy.MySql);

        Assert.Equal(["Amount"], plan.AlterColumns.Select(c => c.ColumnName));
        Assert.Empty(plan.NotJudged);
    }

    /// <summary>表备注在这一家是一句<em>表选项</em>，不是 <c>COMMENT ON</c>（那一句本家没有），
    /// 也不吃列侧那一套"重写整列"的代价。清备注走<b>空串</b>而不是 <c>NULL</c>：这一家
    /// <c>COMMENT = ''</c> 写的就是"没有备注"，而目录里那一颗读回来的也是空串
    /// （<c>MySqlSchemaReader.TableCommentSql</c>），发射与读数在这一格因此是闭合的。</summary>
    [Fact]
    public void The_mysql_table_comment_statement_is_a_table_option()
    {
        var table = MyContract<Shape>().Table;

        Assert.Equal("ALTER TABLE `QUICKSQL_SHAPE` COMMENT = '形状表'",
            Statement(w => Assert.True(My.CodeFirst.TryEmitTableComment(w, table, "形状表"))).ToString());
        Assert.Equal("ALTER TABLE `QUICKSQL_SHAPE` COMMENT = ''",
            Statement(w => Assert.True(My.CodeFirst.TryEmitTableComment(w, table, null))).ToString());
    }

    /// <summary>本家的删索引是 <c>… ON 表名</c>：PG 那一形（只吃带模式的索引名、<em>没有</em> <c>ON</c>）
    /// 在这里是语法错，而表名这一颗在本家不带模式限定（契约没写模式时 <c>EmitTable</c> 什么都不写，
    /// 那句"表在连接串选中的那个库里"）。整句等值断言钉的就是这两处——它们都在文本层看得见，
    /// 而写反了只在库上响。</summary>
    [Fact]
    public void The_mysql_drop_index_statement_names_the_index_and_its_table()
    {
        var sql = Statement(w => Assert.True(
            My.CodeFirst.TryEmitDropIndex(w, MyContract<Shape>().Table, new SchemaIndex("IX_extra_on_amount", false, ["Amount"])))).ToString();

        Assert.Equal("DROP INDEX `IX_extra_on_amount` ON `QUICKSQL_SHAPE`", sql);
    }

    /// <summary>库里多出的那条<em>外键支撑索引</em>开了删索引那一档也不删：本家对它的 <c>DROP INDEX</c>
    /// 当场报 <c>Cannot drop index …: needed in a foreign key constraint</c>，而删那一半走的是
    /// <c>ALTER TABLE … DROP FOREIGN KEY</c>（约束名与索引名在这一家是同一个词）。
    /// 这一格钉的是本家 reader 交回的那颗 <c>IsConstraint</c> 真被 Core 用上——判据本身只有一份。</summary>
    [Fact]
    public void A_mysql_foreign_key_support_index_is_still_manual_with_the_switch_on()
    {
        var contract = MyContract<Shape>();
        var store = MyStore(contract.Table, indexes: [
            new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("FK_QUICKSQL_SHAPE_Order", true, ["Amount"]) { IsConstraint = true },
            new SchemaIndex("IX_extra_on_quantity", false, ["Quantity"]),
        ]);

        var plan = StructureDiff.Build(contract, store, MyReader, IdentifierPolicy.MySql, dropIndexes: true);

        Assert.Equal(["IX_extra_on_quantity"], plan.DropIndexes.Select(i => i.Name));
        Assert.Single(plan.Refused, r => r.Contains("FK_QUICKSQL_SHAPE_Order", StringComparison.Ordinal) && r.Contains("约束", StringComparison.Ordinal));
    }

    // ── 判不了：四家今天的样子 ────────────────────────────────────────

    /// <summary>读不回来的那些档进"判不了"清单并指名，<b>既不响也不假装同步过</b>（§12.2）。
    /// 带默认值的 SPI 位让没接的各家一个字节不必改，代价是它们在列级这一档上今天只有这一份报告：
    /// 主键、列形状、列备注、索引清单、表备注五档一次报齐，而动作清单是空的。
    /// <b>可空性不在这一格</b>——它是唯一家家都读得回来的事实，所以这份夹具必须把它摆对，
    /// 摆歪了就是"在一家判不了形状库上发了一句 ALTER"。
    /// <b>本用例的被告是 <c>Sqlite</c> 那份 reader</b>：PG 家与 MySQL 家自这两笔起五档全有读数，
    /// 它们的那两份报告在上面两格（<see cref="The_pg_family_with_a_full_catalog_reading_reports_nothing_not_judged"/>
    /// 与 <see cref="The_mysql_family_with_a_full_catalog_reading_reports_nothing_not_judged"/>）。</summary>
    [Fact]
    public void A_family_that_cannot_read_reports_not_judged()
    {
        var contract = Contract<Shape>();
        var blind = new SchemaTable(contract.Table, [
            new SchemaColumn("Id", "integer", null, null, false, 1),
            new SchemaColumn("Title", "text", null, null, true, 2),
            new SchemaColumn("Amount", "numeric", null, null, false, 3),
            new SchemaColumn("Price", "numeric", null, null, false, 4),
            new SchemaColumn("CreatedAt", "timestamp", null, null, false, 5),
            new SchemaColumn("Quantity", "integer", null, null, false, 6),
        ]);

        var plan = StructureDiff.Build(contract, blind, Sqlite.Provider(SqlServer.NoDriverFactory.Instance).Schema, IdentifierPolicy.Sqlite);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Refused);
        Assert.Contains(plan.NotJudged, n => n.Contains("主键列", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("列形状", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("列备注", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("索引清单", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("表备注", StringComparison.Ordinal));
    }

    [Fact]
    public void A_family_that_cannot_read_table_comments_says_so()
    {
        var contract = Contract<Shape>();

        var plan = StructureDiff.Build(contract, new SchemaTable(contract.Table, CurrentStore()) { Indexes = [] }, Reader, IdentifierPolicy.SqlServer);

        Assert.False(plan.EmitTableComment);
        Assert.Contains(plan.NotJudged, n => n.Contains("表备注", StringComparison.Ordinal));
    }

    // ── 发射文本：只有 T-SQL 需要幂等的备注句 ─────────────────────────

    /// <summary>备注那一句在 T-SQL 里必须"有则改、无则加"：<c>sp_addextendedproperty</c> 在属性已经在的时候
    /// 直接报错，而 §9.5 的备注同步发的恰恰是"属性在而值变了"的那一列。新建表那一趟走的是 <c>ELSE</c> 那一支。</summary>
    [Fact]
    public void The_TSQL_comment_statement_updates_when_the_property_already_exists()
    {
        var contract = Contract<Shape>();
        var sql = Statement(w => Assert.True(Sql.CodeFirst.TryEmitColumnComment(w, contract.Table, contract.Columns[1]))).ToString();

        Assert.Contains("IF EXISTS (SELECT 1 FROM sys.extended_properties", sql, StringComparison.Ordinal);
        Assert.Contains("sp_updateextendedproperty @name=N'MS_Description', @value=N'标题'", sql, StringComparison.Ordinal);
        Assert.Contains(" ELSE ", sql, StringComparison.Ordinal);
        Assert.Contains("sp_addextendedproperty @name=N'MS_Description', @value=N'标题'", sql, StringComparison.Ordinal);
        Assert.Contains("@level2type=N'COLUMN', @level2name=N'Title'", sql, StringComparison.Ordinal);
        Assert.Contains("COLUMNPROPERTY(OBJECT_ID(N'[dbo].[QUICKSQL_SHAPE]'), N'Title', 'ColumnId')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_TSQL_table_comment_statement_has_no_column_level()
    {
        var sql = Statement(w => Assert.True(Sql.CodeFirst.TryEmitTableComment(w, Contract<Shape>().Table, "形状表"))).ToString();

        Assert.Contains("AND minor_id=0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@level2type", sql, StringComparison.Ordinal);
        Assert.Contains("@value=N'形状表'", sql, StringComparison.Ordinal);
    }

    /// <summary>T-SQL 的删索引是"索引名 ON 表名"那一形：索引名交回的是目录原文，表名走的是
    /// <c>EmitTableName</c> 那一条既有发射路径，两边都过这一家的方括号。这里不加 <c>IF EXISTS</c> 包裹
    /// （名字来自目录，"它不在"那件事在计划层就判掉了），所以整句就是这两颗名字。</summary>
    [Fact]
    public void The_TSQL_drop_index_statement_names_the_index_and_the_table()
    {
        var sql = Statement(w => Assert.True(
            Sql.CodeFirst.TryEmitDropIndex(w, Contract<Shape>().Table, new SchemaIndex("IX_extra_on_amount", false, ["Amount"])))).ToString();

        Assert.Equal("DROP INDEX [IX_extra_on_amount] ON [dbo].[QUICKSQL_SHAPE]", sql);
    }

    /// <summary>今天不接这一颗的两家交回 false 且不写一个字：Core 因此把那一条记进"判不了"，
    /// 而不是发一句空文本、也不是换一种形状硬凑（默认实现让六家在这一颗上不必改一个字节）。</summary>
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("Gbase8s")]
    public void A_family_that_does_not_drop_indexes_declines(string family)
    {
        var provider = (IDbProvider) (family == "Sqlite"
            ? Sqlite.Provider(SqlServer.NoDriverFactory.Instance)
            : Gbase8s.Provider(SqlServer.NoDriverFactory.Instance));

        var writer = Statement(w => Assert.False(
            provider.CodeFirst.TryEmitDropIndex(w, Contract<Plain>().Table, new SchemaIndex("IX_plain_name", false, ["Name"]))));

        Assert.Equal(string.Empty, writer.ToString());
    }

    /// <summary>没有列注释这一档的两家交回 false 且不写一个字——Core 因此把那一格记进"判不了"，
    /// 而不是发一句空文本、也不是改走别的形状（那一条退路从前写在注里，今天由结果清单兑现）。</summary>
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("Gbase8s")]
    public void A_family_without_column_comments_declines(string family)
    {
        var provider = (IDbProvider) (family == "Sqlite"
            ? Sqlite.Provider(SqlServer.NoDriverFactory.Instance)
            : Gbase8s.Provider(SqlServer.NoDriverFactory.Instance));

        var contract = Contract<Plain>();
        var writer = Statement(w => Assert.False(provider.CodeFirst.TryEmitColumnComment(w, contract.Table, contract.Columns[1])));

        Assert.Equal(string.Empty, writer.ToString());
    }

    // ── 夹具 ─────────────────────────────────────────────────────────

    /// <summary>契约的现形状，按<em>目录报的字形</em>写：小写、numeric、时间精度落在标度那一格。
    /// 每一格都能单独拧歪，拧歪的那一格就是这一趟要发的那一句。</summary>
    static List<SchemaColumn> CurrentStore(
        string? Id = null,
        string pkOn = "Id",
        string? Title = null,
        bool titleNullable = true,
        string titleComment = "标题",
        string? Amount = null,
        bool amountNullable = false,
        string? Price = null,
        string? CreatedAt = null,
        string? Quantity = null,
        string? drop = null)
    {
        var store = new List<SchemaColumn>
        {
            Col("Id", Id ?? "int", identity: true, pk: pkOn == "Id"),
            Col("Title", Title ?? "nvarchar(32)", nullable: titleNullable, comment: titleComment),
            Col("Amount", Amount ?? "numeric(18,4)", nullable: amountNullable, pk: pkOn == "Amount"),
            Col("Price", Price ?? "decimal(10,2)"),
            Col("CreatedAt", CreatedAt ?? "datetime2(3)"),
            Col("Quantity", Quantity ?? "int"),
        };

        return drop is null ? store : [.. store.Where(c => c.Name != drop)];
    }

    /// <summary>本家那份读数：型名按 <c>information_schema</c> 交回的<em>简称</em>写（reader 自己在
    /// <c>StoreType</c> 那一颗里把 <c>character varying</c> 折成 <c>varchar(n)</c>），时刻的位数写在型名的
    /// 括号里（<c>timestamp(3)</c>，同一个 <c>datetime_precision</c> 折的），主键与自增两格由本家读得回来。
    /// 这份夹具是<em>照 reader 的产出手写的</em>，不是照厂商文档：它红了就是 reader 与它自己的判据分岔。
    /// <b>每一列都显式写 <c>IsPrimaryKey</c></b>：那一格留空是 <c>null</c>，而 null 在 diff 里说的是
    /// "这一家读不回来"——少写一列就整档主键判不了，正面断言当场变红。</summary>
    static SchemaTable PgStore(TableInfo table, string? amount = null, List<SchemaIndex>? indexes = null)
        => new(table, [
            new SchemaColumn("Id", "integer", null, null, false, 1) { Comment = string.Empty, IsIdentity = true, IsPrimaryKey = true },
            new SchemaColumn("Title", "varchar(32)", 32, null, true, 2) { Comment = "标题", IsPrimaryKey = false },
            new SchemaColumn("Amount", amount ?? "numeric(18,4)", null, null, false, 3) { Comment = string.Empty, IsPrimaryKey = false, Scale = 4 },
            new SchemaColumn("Price", "numeric(10,2)", null, null, false, 4) { Comment = string.Empty, IsPrimaryKey = false, Scale = 2 },
            new SchemaColumn("CreatedAt", "timestamp(3)", null, null, false, 5) { Comment = string.Empty, IsPrimaryKey = false, Scale = 3 },
            new SchemaColumn("Quantity", "integer", null, null, false, 6) { Comment = string.Empty, IsPrimaryKey = false },
        ])
        {
            Comment = "形状表",
            Indexes = indexes ?? [new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"])],
        };

    /// <summary>本家那份读数：型名按 <c>information_schema.columns.data_type</c> 交回的那一形写
    /// （小写、<em>整数族不带显示宽度</em>），字符宽度与时刻位数由 <c>MySqlSchemaReader.StoreType</c>
    /// 从 <c>character_maximum_length</c> 与 <c>datetime_precision</c> 拼回型名里，备注两格是<b>空串</b>
    /// （这一家没有"没写过 = NULL"那一档），而 <c>MaxBytes</c> 那一格自这一笔起交回的是<em>字节</em>：
    /// <c>varchar(32)</c> 在 <c>utf8mb4</c> 下是 128 而不是 32 —— 那颗 128 钉的就是本笔改对的旧伤
    /// （从前注释写字节、代码读字符，见 <c>MySqlSchemaReader.ColumnsSql</c> 那段）。
    /// 与 PG 那份夹具同一条纪律：<b>照 reader 的产出手写</b>，它红了就是 reader 与它自己的判据分岔。</summary>
    static SchemaTable MyStore(TableInfo table, string? amount = null, List<SchemaIndex>? indexes = null)
        => new(table, [
            new SchemaColumn("Id", "int", null, null, false, 1) { Comment = string.Empty, IsIdentity = true, IsPrimaryKey = true },
            new SchemaColumn("Title", "varchar(32)", 32, 128, true, 2) { Comment = "标题", IsPrimaryKey = false },
            new SchemaColumn("Amount", amount ?? "decimal(18,4)", null, null, false, 3) { Comment = string.Empty, IsPrimaryKey = false, Scale = 4 },
            new SchemaColumn("Price", "decimal(10,2)", null, null, false, 4) { Comment = string.Empty, IsPrimaryKey = false, Scale = 2 },
            new SchemaColumn("CreatedAt", "datetime(3)", null, null, false, 5) { Comment = string.Empty, IsPrimaryKey = false, Scale = 3 },
            new SchemaColumn("Quantity", "int", null, null, false, 6) { Comment = string.Empty, IsPrimaryKey = false },
        ])
        {
            Comment = "形状表",
            Indexes = indexes ?? [new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"])],
        };

    static StructurePlan Diff(IReadOnlyList<SchemaColumn> columns, string? comment = "形状表", List<SchemaIndex>? indexes = null, bool dropIndexes = false)
    {
        var contract = Contract<Shape>();
        var store = new SchemaTable(contract.Table, columns)
        {
            Comment = comment,
            // 默认给一条与契约同列序列的读数：索引那一档因此不进任何一份清单，除非用例把它拧歪。
            Indexes = indexes ?? [new SchemaIndex("IX_QUICKSQL_SHAPE_Title", false, ["Title"])],
        };

        return StructureDiff.Build(contract, store, Reader, IdentifierPolicy.SqlServer, dropIndexes);
    }

    static SchemaColumn Col(string name, string type, bool nullable = false, string? comment = "", bool identity = false, bool pk = false)
        => new(name, type, null, null, nullable, 1) { Comment = comment, IsIdentity = identity, IsPrimaryKey = pk };

    /// <summary>只声明存储类型名的列契约：形状那一颗判据吃的正是它（<c>StoreTypeName</c> 非空即"显式声明"）。</summary>
    static ColumnContract Declared(string storeType) => new()
    {
        MemberName = "C",
        ClrType = typeof(string),
        ColumnName = "C",
        LengthSemantics = LengthSemantics.Char,
        Unicode = true,
        IsNullable = true,
        IsPrimary = false,
        IsIdentity = false,
        MaxChars = null,
        MaxBytes = null,
        Precision = null,
        Scale = null,
        StoreTypeName = storeType,
        Origin = new OriginTrace(MetadataLayer.Convention, "用例直给"),
    };

    static SqlWriter Statement(Action<SqlWriter> emit)
    {
        var writer = new SqlWriter(new(), [], '@');

        emit(writer);

        return writer;
    }
}
