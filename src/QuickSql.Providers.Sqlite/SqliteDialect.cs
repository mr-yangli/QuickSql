using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的 SQL 文本层。分页与 PG 血统逐字同形（LIMIT n OFFSET m）而分岔在缺省那一支：
/// 本家不许光写 OFFSET，缺上限时落 LIMIT -1——那不是一颗数量而是库认得的"不设上限"记号。
/// 占位符前缀本家四选一（@ : $ #）都不挑，取 '@' 是为了与共用层同形少一处特判；具名参数驱动
/// 两侧都认，而匿名 ? 驱动直接拒（引擎自己支持 ?1 那一族）。SupportsJoinedWrites=false 是
/// "两半各缺一次"：UPDATE … FROM 在而列名清单 AS t(cols) 被拒（合并档整形不可用），DELETE …
/// FROM/USING 两样都没有；共用层那支"null 格落 DEFAULT 关键字"在本家是语法错（今天不可达，
/// 每格都参数化——绑 NULL 代替 DEFAULT 语义不同，NOT NULL DEFAULT 列上差一整个约束）；
/// N'…' 在这一族是语法错，所以 PrefixUnicodeLiterals 恒 false；RETURNING、IS DISTINCT FROM、
/// DROP COLUMN 各有引擎版本门槛而 DbCapabilities 没有"库版本"那一格，本文件所有"能用"的读数
/// 都自带一个没地方声明的前提，STRICT 表与生成列也不发。</summary>
public sealed class SqliteDialect(
    SqliteTypes types,
    SqliteKeys keys,
    SqliteValues values,
    SqliteExpressions expressions
) : DialectBase(IdentifierPolicy.Sqlite, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.Sqlite;

    /// <summary>类注释第二段：四种记号里挑了一颗，而这一颗是被共用层的便利选中的，不是被库要求的。</summary>
    public override char ParameterPrefix => '@';

    /// <summary>本家驱动收带前缀的参数名（四种前缀各绑定成功量过），所以执行器不必摘前缀，
    /// 那笔每颗参数一次的 Substring 在本家不用付。</summary>
    public override bool DriverAcceptsPrefixedParameterNames => true;

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>定点存 TEXT 而 SUM/AVG 返回 INTEGER——SQLite 存储类排序 INTEGER &lt; TEXT
    /// 让 1700 &gt; '1000' 得 0。在比较表达式入口把 decimal 参数包 CAST(@p AS NUMERIC)，
    /// 库会把另一侧的 TEXT 列值按数值比较（NUMERIC 是 CAST 专用名，与 DDL 的 TEXT 不冲突）。
    /// MAX/MIN 保留列的 TEXT 类型，两侧同型不受影响，本方法只对比较运算符生效。</summary>
    protected override SqlExpression PrepareForEmission(SqlExpression node) =>
        node is SqlBinaryExpression binary && IsComparison(binary.Operator)
            ? WrapDecimalParameters(binary)
            : node;

    static SqlBinaryExpression WrapDecimalParameters(SqlBinaryExpression binary)
    {
        var left = IsDecimalParameter(binary.Left)
            ? new CastExpression(binary.Left, "NUMERIC")
            : binary.Left;
        var right = IsDecimalParameter(binary.Right)
            ? new CastExpression(binary.Right, "NUMERIC")
            : binary.Right;

        return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
            ? binary
            : new SqlBinaryExpression(binary.Operator, left, right);
    }

    static bool IsComparison(BinaryOperator op) =>
        op
            is BinaryOperator.Equal
                or BinaryOperator.NotEqual
                or BinaryOperator.LessThan
                or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan
                or BinaryOperator.GreaterThanOrEqual;

    static bool IsDecimalParameter(SqlExpression expr) => expr is ParameterValue { Value: decimal };

    /// <summary>类注释第五段：UPDATE 半边有子句而列名清单发不出，DELETE 半边两样都没有。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>句尾 RETURNING（与 PG 血统同形而四宿主都有活库读数）；
    /// 文本本体归 SqliteKeys.EmitReturning，与另六家同一条分工。</summary>
    protected override void EmitReturning(
        SqlWriter writer,
        IReadOnlyList<ColumnContract> returning
    ) => keys.EmitReturning(writer, returning);

    /// <summary>LIMIT n OFFSET m，与 PostgreSqlDialect 那一份逐字同形而差一颗哨兵：
    /// 那一家两支各自可缺省，本家光写 OFFSET 是语法错，所以有偏移而无上限时必须先落一颗 LIMIT -1。
    /// 哨兵取 InlineLiteral 而不是参数：写进文本的常数不参与绑定，而它不是用户给的任何数。
    /// 两支都缺省时整段不发；PagingRequiresOrdering 保持 false（无 ORDER BY 的 LIMIT 直接通过）。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        writer.Append(" LIMIT ");

        EmitExpression(select.Limit ?? new InlineLiteral("-1"), writer);

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");

            EmitExpression(offset, writer);
        }
    }

    /// <summary>与 Gbase8sDialect 那一份同一立场（发得出子句而不发得出列名清单，等于发不出这一档）：
    /// 那一家是"压根没有多表更新的子句"，本家是有子句而 AS t(cols) 被拒。
    /// 消息里两条等价路都有实测——读到这条错的人下一步就是问"那这一家怎么写"，
    /// 本家能答"有两条路、都量过，只是共用层不发"。</summary>
    protected override void EmitJoinedSources(
        SqlWriter writer,
        string keyword,
        IReadOnlyList<JoinClause> joins,
        out SqlExpression? folded
    )
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句接不下 §12.5 合并档那一形（{keyword.Trim()} 那一段有 {joins.Count} 个表源）：这一家的 "
                + "UPDATE … FROM 只认源自己带出来的列名，而 (VALUES …) 出来的隐式名是 column1..N，给它改名那一形 "
                + "AS t(cols) 在本家被拒（五形五拒，2107 是 UPDATE 侧的第六次）；DELETE 那半边连 FROM/USING 子句都没有。"
                + "本家量过的两条等价路是 UPDATE … SET v = (SELECT … FROM 源 WHERE …) 的标量子查询形与 "
                + "… WHERE EXISTS (SELECT 1 FROM 源 WHERE …) 的关联形，而共用层不发那两种形状。"
                + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
                + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。"
        );
    }

    /// <summary>InsertOrUpdate 在本家的发射：复用共用层那颗 INSERT … VALUES（列清单、VALUES 形状与
    /// 参数登记与 InsertRange 同一处真相），本家只补它后面那一段 ON CONFLICT … DO UPDATE SET 列 = excluded.列。
    /// 句尾写在这里而不是 PG 血统那一族的 UpsertClause 槽，是因为本家整颗 Bulk 交回 null、拿不到长在
    /// 另一颗接口上的那个槽（不是不支 upsert——库确实有 ON CONFLICT、四臂各量过）；这一形与
    /// PostgreSqlBulk.UpsertClause 逐字同形，两家各写一份是因为 provider 之间禁止互引。
    /// 档位口径：它到"文本层已钉 + 同形语法有活库旁证"为止——由本层发出的那一条具体语句
    /// 还没有在一台活 SQLite 上跑过一次（探针量的是手写语句），集成册补一场才算结清。</summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        // 共用层那份 INSERT：本家不发 Returning（回填那一档不在这条链上），所以 Returning 留空 = 逐字一条普通 INSERT … VALUES。
        var insert = new InsertStatement
        {
            Table = upsert.Table,
            Columns = upsert.Columns,
            Rows = upsert.Rows,
        };

        base.Emit(in insert, writer);

        writer.Append(" ON CONFLICT (");

        for (var i = 0; i < upsert.Keys.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, upsert.Keys[i].ColumnName);
        }

        writer.Append(") DO UPDATE SET ");

        for (var i = 0; i < upsert.Settable.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            var column = upsert.Settable[i].ColumnName;

            EmitIdentifier(writer, column);
            writer.Append(" = excluded.");
            EmitIdentifier(writer, column);
        }
    }
}
