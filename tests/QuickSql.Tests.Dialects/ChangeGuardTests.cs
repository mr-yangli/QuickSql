using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>变更守卫与合并语句的<em>文本</em>（§12.4 第 2 项、§12.5）在<b>离线</b>能判的那一半。
///
/// <para>分界线跟 <see cref="ConcurrencyTokenTests"/> 同一条：这里一句 SQL 都不上线，全程
/// <see cref="SqlServer.NoDriverFactory"/>。于是"守卫长什么样"与"守卫挡不挡得住"是两件事 ——
/// <b>判得到的只有前者</b>。后者（<c>@@ROWCOUNT</c> 真的小于行数、无变化的那几行确实没被刷新）
/// 只有活库答得了，见证在集成层 <c>TrackingPathTests</c>。</para>
///
/// <para><b>这里判不到、也只能在集成层判的第二件事</b>：跟踪器<em>自己</em>拼出来的那一句。下面的 AST 是手搭的，
/// 所以黄金样本钉的是方言那一段（<see cref="SourceRows"/> 怎么成文、守卫怎么落进 <c>WHERE</c>），
/// 而"跟踪器把手搭的那份形状换成实体里的值之后文本不变"要靠真跑一次。</para></summary>
public sealed class ChangeGuardTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => SqlServer.Orm();

    static QuickSqlOrm ormWith(ModelConfiguration model)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));

    /// <summary>模型上的 <c>Note</c>/<c>CustomerName</c>/<c>Amount</c> 各掰成一种"库里不接受等号"的类型。
    /// 用 Fluent 覆写而不是新建一个实体：G1 只认 <c>[Table]</c>，新增实体会把整套等价性对拍牵连进来，
    /// 而这一格要的只是"这一列在 SQL Server 上不可比"这一个事实。</summary>
    static ModelConfiguration Lobbed()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.Note).HasDbType("xml");
        model.Entity<Order>().Property(o => o.CustomerName).HasDbType("text");
        model.Entity<Order>().Property(o => o.Amount).HasDbType("sql_variant");

        return model;
    }

    // ── 哪一列进得了守卫 ─────────────────────────────────────────────

    [Fact]
    public void A_column_the_database_cannot_compare_is_refused_rather_than_hashed()
    {
        var columns = ormWith(Lobbed()).Schemas.Contract<Order>().Columns;

        foreach (var member in new[] { nameof(Order.Note), nameof(Order.CustomerName), nameof(Order.Amount) })
            Assert.False(Provider.ChangeGuard.Compares(columns.Single(c => c.MemberName == member)),
                $"{member} 掰成了不接受等号的类型，守卫必须说不 —— 答『是』就是发一句库里执行不了的语句，或者更糟：拿哈希近似它。");

        // 同一张表上其余各列不受牵连：判据是逐列的，不是逐表的，更不是"看到 LOB 就把整型退回逐条"。
        foreach (var member in new[] { nameof(Order.Id), nameof(Order.State), nameof(Order.CreatedAt), nameof(Order.PaidAt), nameof(Order.CustomerId) })
            Assert.True(Provider.ChangeGuard.Compares(columns.Single(c => c.MemberName == member)));
    }

    [Fact]
    public void The_types_that_do_compare_include_the_ones_a_hash_based_shortcut_would_have_dropped()
    {
        var order = orm().Schemas.Contract<Order>().Columns;
        var item = orm().Schemas.Contract<InventoryItem>().Columns;

        // 无上限的字符串是 nvarchar(max)，而 MAX 在 T-SQL 里可以等值比较（只有遗留 LOB 不行）——
        // 把 MAX 也排除掉的话，最常见的宽文本列实体整批退回逐条，合并就白做了。
        Assert.Equal("NVARCHAR(MAX)", order.Single(c => c.MemberName == nameof(Order.Note)).StoreTypeName);
        Assert.True(Provider.ChangeGuard.Compares(order.Single(c => c.MemberName == nameof(Order.Note))));

        // rowversion 就站在 LOB 家族旁边的二进制那一档，而并发谓词天天在比它（v0.18 两个实例都跑通）。
        Assert.True(Provider.ChangeGuard.Compares(item.Single(c => c.MemberName == nameof(InventoryItem.RowVersion))));
    }

    // ── 守卫的形状 ───────────────────────────────────────────────────

    [Fact]
    public void A_not_null_column_needs_only_the_inequality()
    {
        var sql = Merge(nameof(Order.Amount)).Text;

        Golden.Verify("update-merged-not-null", sql);

        Checks.Has("WHERE [t0].[Amount] <> [t1].[Amount]", sql);

        // 三段展开在这里是纯噪音：NOT NULL 列上永不可能出现"只有一侧为空"，
        // 而多出来的两段会让谓词更难走索引。
        Checks.Absent("IS NULL", sql);
    }

    [Fact]
    public void A_nullable_column_expands_into_three_terms_because_T_SQL_has_no_distinct_from()
    {
        var sql = Merge(nameof(Order.PaidAt)).Text;

        Golden.Verify("update-merged-nullable", sql);

        // 顺序是不等、只有表侧空、只有行集侧空。两侧同空的行是"没变"，所以那一档不在守卫里 ——
        // 写进去就成了每一格 NULL 都被判成有变化，守卫的全部用处就此抹掉。
        Checks.Has("WHERE [t0].[PaidAt] <> [t1].[PaidAt] OR ([t0].[PaidAt] IS NULL AND [t1].[PaidAt] IS NOT NULL) OR ([t0].[PaidAt] IS NOT NULL AND [t1].[PaidAt] IS NULL)", sql);
    }

    [Fact]
    public void Two_guarded_columns_are_one_flat_OR_because_the_operator_is_the_same()
    {
        var sql = Merge(nameof(Order.Amount), nameof(Order.PaidAt)).Text;

        // 同种连接词不加括号（§13.2 的扁平链），所以这条读起来跟手写的一样。
        Checks.Has("WHERE [t0].[Amount] <> [t1].[Amount] OR [t0].[PaidAt] <> [t1].[PaidAt] OR", sql);

        // 两列之间不是 AND：合并语句的语义是"任何一列有变化就写这一行"，与 AND 相差一整批行。
        Checks.Absent(" AND [t0].[PaidAt]", sql);
    }

    // ── 合并语句的版式 ───────────────────────────────────────────────

    [Fact]
    public void A_merged_update_joins_the_session_rows_as_a_literal_table()
    {
        var merged = Merge(nameof(Order.Amount), nameof(Order.PaidAt));

        Golden.Verify("update-merged-two-columns", merged.Text);

        // 三件事各钉一处：行集的列清单（键在前、改动列在后）、连接条件用键、SET 右侧引行集而不是参数。
        Checks.Has("SET [Amount] = [t1].[Amount], [PaidAt] = [t1].[PaidAt]", merged.Text);
        Checks.Has(") AS [t1] ([Id], [Amount], [PaidAt]) ON [t0].[Id] = [t1].[Id]", merged.Text);
    }

    [Fact]
    public void Every_row_keeps_its_own_parameters_and_the_statement_takes_no_extra_one()
    {
        // 串列是这一格里最坏的错：列清单与行的顺序错开一格，语句在每家库都执行得动，写的却是别人的值。
        // 所以断言落在参数名与参数值两侧 —— 名字对上而值对不上，同样是串列。
        var merged = Merge(nameof(Order.Amount), nameof(Order.PaidAt));

        Assert.Equal(["@p0", "@p1", "@p2", "@p3", "@p4", "@p5"], merged.Parameters.Select(static p => p.Name).ToArray());
        Assert.Equal(
            ["7", "值1", "值2", "8", "值1", "值2"],
            merged.Parameters.Select(static p => p.Value?.ToString()).ToArray());

        Checks.Has("(VALUES (@p0, @p1, @p2), (@p3, @p4, @p5))", merged.Text);

        // 定位全靠 ON 上的列对列，所以参数一颗都不多：合并省下的是往返，不是参数（分块账见 §12.3）。
        Checks.Absent("@p6", merged.Text);
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    /// <summary>手搭一份"两行、键是 <c>Id</c>、改动列是 <paramref name="members"/> 提名的那几列"的合并语句。
    /// 形状与 <c>EntityTracker.WriteMergedAsync</c> 逐字同构（列清单同序、守卫同式、<c>ON</c> 同键），
    /// 而值在这里是常量 —— 为的是让文本能被黄金样本钉住。</summary>
    DialectSql Merge(params string[] members)
    {
        var contract = orm().Schemas.Contract<Order>();
        var stored = new TableRef(0, "t0", contract.Table, contract);
        var side = new TableRef(1, "t1", contract.Table);

        var keys = contract.PrimaryKey;
        ColumnContract[] sets = [.. members.Select(m => contract.Columns.Single(c => c.MemberName == m))];
        ColumnContract[] columns = [.. keys, .. sets];

        var rows = new IReadOnlyList<SqlExpression>[2];

        for (var row = 0; row < rows.Length; row++)
        {
            var cells = new SqlExpression[columns.Length];

            for (var i = 0; i < cells.Length; i++)
                cells[i] = new ParameterValue(i == 0 ? 7 + row : (object?) ("值" + i), columns[i]);

            rows[row] = cells;
        }

        SqlExpression? on = null;

        foreach (var key in keys)
        {
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(stored, key), new ColumnExpression(side, key));

            on = on is null ? equal : new SqlBinaryExpression(BinaryOperator.And, on, equal);
        }

        SqlExpression[] differences = [.. sets.Select(column => Provider.ChangeGuard.BuildDifference(
            column, new ColumnExpression(stored, column), new ColumnExpression(side, column)))];

        SqlExpression where = differences[^1];

        for (var i = differences.Length - 2; i >= 0; i--)
            where = new SqlBinaryExpression(BinaryOperator.Or, differences[i], where);

        var ast = new UpdateStatement
        {
            Table = stored,
            Sets = [.. sets.Select(column => new SetClause(column, new ColumnExpression(side, column)))],
            Where = where,
            Joins = [new JoinClause(JoinKind.Inner, new SourceRows(side, columns, rows), on)],
        };

        var writer = new SqlWriter(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

        Provider.Dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }
}
