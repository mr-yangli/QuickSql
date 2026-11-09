using QuickSql.TestInfra;
using QuickSql.Write;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>orm.InsertOrUpdate&lt;T&gt;()</c> 那条独立链（§5.4 写侧、§12.1 写侧扩展那句"含 MERGE"那一档）
/// 在离线上一问一答的册子。脊柱这一格先钉两件事：<b>句尾那一族复用 <c>UpsertClause</c> 发出的文本与
/// <c>UpdateRange(...).WithBulk(Upsert)</c> 同形</b>（同一条内核出路、同一把列尺子），以及<b>发射形态的分岔
/// 走通</b>——接了句尾的家走句尾，没接的家交给方言的整语句 emit。
///
/// <para>与 <see cref="BulkWriteLayerTests"/> 的分工：那一册量的是 <c>UpdateRange</c> 那条链的档位选择，
/// 本册量的是这条<em>新独立链</em>。两册共用同一套纪律：全程离线、<see cref="SqlServer.NoDriverFactory"/>，
/// 断言失败的消息里带整条产出的 SQL。逐家的 MERGE 文本在 <c>MergeLayerFacts</c>（task #9 落）。</para></summary>
public sealed class InsertOrUpdateLayerTests
{
    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    /// <summary><see cref="Order"/> 的一行，主键给值：这一条链按主键判冲突，那颗键必须是实的
    /// （Id 空着会被"库给值而没带键"那道当场拒挡住）。列序与 <see cref="BulkWriteLayerTests"/> 那份同源。</summary>
    static Order row(int n) => new()
    {
        Id = n,
        CustomerName = "客户" + n,
        CustomerId = n,
        Amount = 10m + n,
        State = OrderState.Paid,
        CreatedAt = Moment,
        Note = "备注" + n,
    };

    static Order[] rows(int count) => [.. Enumerable.Range(1, count).Select(row)];

    /// <summary>按名取一家的离线组装根（本册用到的那几家；默认分支响，新增一家忘了登记要报错不是少跑）。</summary>
    static QuickSqlOrm OrmFor(string family) => family switch
    {
        "sqlserver" => SqlServer.Orm(),
        "sqlite" => Sqlite.Orm(),
        "oracle" => Oracle.Orm(),
        "dameng" => Dameng.Orm(),
        "shentong" => ShenTong.Orm(),
        "xugu" => Xugu.Orm(),
        "gbase8s" => Gbase8s.Orm(),
        _ => throw new NotSupportedException($"InsertOrUpdate 册里没有 {family} 这一家的探针。"),
    };

    /// <summary>逐字符黄金样本：只给"整语句那一族 + Sqlite"这五家立基线，因为它们是这一趟<em>新产出的文本</em>
    /// （T-SQL <c>MERGE</c>、Oracle 血统的 <c>dual UNION ALL</c>、虚谷那颗、GBase 8s 的两条形、Sqlite 的 <c>ON CONFLICT</c>）。
    /// 句尾那两族（PG ∶ MySQL 血统）复用已经跑绿的 <c>UpsertClause</c>、本册不另立基线（那条尾巴的形状已由
    /// <see cref="BulkWriteLayerTests"/> 的两颗钉住）。<b>基线的档位口径同方言那颗：文本层已钉、除 SQL Server 外无活库见证，
    /// 生成后逐条人工读过——黄金样本把"文本变了要有人显式承认"这件事钉住，不替"这一句在真库上跑得动"背书。</b></summary>
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("dameng")]
    [InlineData("xugu")]
    [InlineData("gbase8s")]
    [InlineData("sqlite")]
    public void The_new_upsert_text_is_byte_pinned_per_family(string provider)
    {
        var text = OrmFor(provider).InsertOrUpdate<Order>().SetSource(rows(2)).ToSql();

        Golden.Verify("insert-or-update", text, provider);
    }

    // ── 整语句 MERGE 那一族 ────────────────────────────────────────────

    [Fact]
    public void SqlServer_emits_a_TSql_MERGE_with_a_holdlock_target_and_a_values_source()
    {
        var text = SqlServer.Orm().InsertOrUpdate<Order>().SetSource(rows(2)).ToSql();

        // 目标位带 WITH (HOLDLOCK)（那条并发缺口的文档缓解），并给一个独立别名。
        Checks.Has("MERGE INTO [QuickSqlOrder] WITH (HOLDLOCK) AS [t0]", text);

        // 源是一颗带列名清单的多行 VALUES；两行 = 两个括号组。
        Checks.Has("USING (VALUES (", text);
        Assert.Equal(2, Checks.Occurrences(text, "(@p"));
        Checks.Has(") AS [qsrc] ([CustomerName], [CustomerId], [Amount], [State], [CreatedAt], [PaidAt], [Note], [Id])", text);

        // ON 配键，MATCHED 改可写列（键不进 SET），NOT MATCHED 插全清单并引源别名。
        Checks.Has("ON ([t0].[Id] = [qsrc].[Id])", text);
        Checks.Has("WHEN MATCHED THEN UPDATE SET [t0].[CustomerName] = [qsrc].[CustomerName]", text);
        Checks.Absent("[t0].[Id] = [qsrc].[Id] SET", text); // Id 不进 SET 段（只进 ON 与 INSERT）
        Checks.Has("WHEN NOT MATCHED THEN INSERT (", text);
        Checks.Has(");", text); // T-SQL MERGE 以分号收尾
    }

    [Theory]
    [InlineData("oracle")]
    [InlineData("dameng")]
    [InlineData("shentong")]
    [InlineData("xugu")]
    public void The_Oracle_lineage_and_Xugu_merge_from_a_dual_union_source_without_a_semicolon(string family)
    {
        // 这四家共用同一份"行集只能从 USING (SELECT … FROM dual UNION ALL …) 带"的形状：Oracle 血统三家共用
        // OracleDialect 那一份产出（达梦 ∶ 神通是它的叶子），虚谷同形而多一分骨架实测（F-19）。
        // 尾巴（ON ∶ WHEN MATCHED ∶ WHEN NOT MATCHED）三家共用 DialectBase.EmitMergeTail，其可读形状已在
        // SqlServer 那颗钉过；这里按各家标识符折法无关的结构量这一族的分岔（dual 源、不收分号）。
        var text = OrmFor(family).InsertOrUpdate<Order>().SetSource(rows(2)).ToSql();

        Checks.Has("MERGE INTO ", text);
        Checks.Has("USING (SELECT ", text);
        Checks.Has(" FROM dual UNION ALL SELECT ", text);
        Checks.Has(") ", text);
        Checks.Has("WHEN MATCHED THEN UPDATE SET ", text);
        Checks.Has("WHEN NOT MATCHED THEN INSERT (", text);

        // Oracle 血统与虚谷的 MERGE 都不以分号收尾（那是 T-SQL 才要的）。
        Checks.Absent(";", text);
        // 前缀是冒号（这一族的占位符），行别名不带 AS 之外也别混进 VALUES 关键字。
        Checks.Absent("VALUES (SELECT", text);
    }

    [Fact]
    public void Gbase8s_emits_an_update_plus_a_not_exists_guard_because_it_has_no_merge()
    {
        // 本家没有 MERGE 的行集形状，用 UPDATE…WHERE + INSERT…SELECT…WHERE NOT EXISTS 两条（类注释第五条那条等价路）。
        // 字面单行源是 systables WHERE tabid = 1（与 Gbase8sKeys 那颗 DBINFO 同源）。这里只量分岔骨架——
        // 目标不起别名、参数是位置形 ?、两条语句一起走一个分号批（三处未证在方言那颗的抬头写全）。
        var text = Gbase8s.Orm().InsertOrUpdate<Order>().SetSource(rows(1)).ToSql();

        Checks.Has("UPDATE ", text);
        Checks.Has(" WHERE ", text);
        Checks.Has("; INSERT INTO ", text);
        Checks.Has(" FROM systables WHERE tabid = 1 AND NOT EXISTS (SELECT 1 FROM ", text);
        Checks.Has("?)", text); // 位置形：NOT EXISTS 里那颗键参数落成一个 ?

        // 本家不发 MERGE、不发 USING。
        Checks.Absent("MERGE", text);
        Checks.Absent("USING", text);
    }

    // ── 句尾那一族：与 UpdateRange 的 Upsert 档同形 ─────────────────────

    [Fact]
    public void Sqlite_emits_the_ON_CONFLICT_tail_from_the_dialect_not_a_Bulk_clause()
    {
        // 本家整颗 Bulk 交回 null（没有可举证的参数预算），所以句尾长在方言的整语句 emit 上，
        // 而它发出的文本与 PG 血统那一份逐字同形——同一句 ON CONFLICT (键) DO UPDATE SET … = excluded.…。
        var text = Sqlite.Orm().InsertOrUpdate<Order>().SetSource(rows(2)).ToSql();

        Checks.Has("INSERT INTO \"QuickSqlOrder\" (\"CustomerName\"", text);
        Checks.Has("ON CONFLICT (\"Id\") DO UPDATE SET ", text);
        Checks.Has("\"CustomerName\" = excluded.\"CustomerName\"", text);

        // 键进冲突目标而不进 SET。
        Assert.Equal(2, Checks.Occurrences(text, "\"Id\""));
        Checks.Absent("\"Id\" =", text);
    }

    [Fact]
    public void The_PG_family_tail_names_the_conflict_target_and_reads_EXCLUDED()
    {
        var text = PostgreSql.Orm().InsertOrUpdate<Order>().SetSource(rows(2)).ToSql();

        // VALUES 那一半是内核拼的，与 InsertRange 共用一条出路：列清单 = 可写 7 列 ++ 主键 1 列。
        Checks.Has("INSERT INTO \"QuickSqlOrder\" (\"CustomerName\"", text);
        Checks.Has("ON CONFLICT (\"Id\") DO UPDATE SET ", text);
        Checks.Has("\"CustomerName\" = EXCLUDED.\"CustomerName\"", text);

        // 键进冲突目标而不进 SET：改自己的键不是整行更新（与句尾族那一条判据同形）。
        Assert.Equal(2, Checks.Occurrences(text, "\"Id\""));
        Checks.Absent("\"Id\" =", text);

        // PG 用 EXCLUDED 那伪表，没有行别名可起。
        Checks.Absent(" AS ", text);
    }

    [Fact]
    public void The_MySQL_family_tail_is_the_row_alias_shape()
    {
        var text = MySql.Orm().InsertOrUpdate<Order>().SetSource(rows(2)).ToSql();

        Checks.Has("INSERT INTO `QuickSqlOrder` (`CustomerName`, `CustomerId`, `Amount`, `State`, `CreatedAt`, `PaidAt`, `Note`, `Id`) VALUES (", text);

        // 两行 = 两个括号组，一句多值。
        Assert.Equal(2, Checks.Occurrences(text, "(@p"));

        // 行别名那一形（8.0.19+）而不是 VALUES(col)：与 BulkWriteLayerTests 那颗同源。
        Checks.Has("AS `fs`(", text);
        Checks.Has(") ON DUPLICATE KEY UPDATE ", text);
        Checks.Has("`CustomerName` = `fs`.`CustomerName`", text);
        Checks.Absent("= VALUES(", text);
        Checks.Absent("`Id` = `fs`", text);
    }

    // ── 发射段那条计划：块宽、参数编号、0 行、null 实体 ─────────────────

    [Fact]
    public void The_plan_carries_one_command_per_chunk_and_shared_parameter_numbering()
    {
        var plan = PostgreSql.Orm().InsertOrUpdate<Order>().SetSource(rows(2)).Explain();

        Assert.True(plan.IsSingleCommand);
        Assert.Equal(2, plan.RowCount);

        // 两行 × 8 颗 = 16 颗，一张参数表；列序 = 可写 ++ 主键（写入清单），Id 在第 8 与第 16 颗。
        var parameters = plan.Commands[0].Parameters;
        Assert.Equal(16, parameters.Count);
        Assert.Equal("CustomerName", parameters[0].Hint!.ColumnName);
        Assert.Equal("Id", parameters[7].Hint!.ColumnName);
        Assert.Equal("Id", parameters[^1].Hint!.ColumnName);
    }

    [Fact]
    public void Zero_rows_emits_no_statement_and_is_not_a_family_support_question()
    {
        // 0 行在没接 upsert 的那一家也不该先问"这一家接不接"——没有语句可发，形态分岔走不到。
        var plan = Oracle.Orm().InsertOrUpdate<Order>().SetSource(Array.Empty<Order>()).Explain();

        Assert.Empty(plan.Commands);
        Assert.Equal(0, plan.RowCount);
        Checks.Has("0 行", plan.Reason);
    }

    [Fact]
    public void A_null_entity_in_the_source_is_refused_before_any_text_is_built()
    {
        // 集合本身非空、里面夹一颗 null：取值段逐行读时才撞上，判据与"看到的那份=发出去的那份"同一条纪律。
        var source = new Order[] { row(1), null! };

        var refused = Checks.Raised<QuickSqlException>(
            () => PostgreSql.Orm().InsertOrUpdate<Order>().SetSource(source).ToSql());

        Checks.Has("null 实体", refused.Message);
    }
}
