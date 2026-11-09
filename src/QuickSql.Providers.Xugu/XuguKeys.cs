using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的主键策略。有 RETURNING 的语法（句尾、与金仓同位置），但实测那一形"执行成功、行进去了、回包却是空结果集"，
/// 所以批量与单行的 RETURNING 都不可信；单颗自增键走语句后回读 SELECT LAST_INSERT_ID()（真库跑通的那条路，
/// 同命令同会话、事务内同样成立）。
/// SupportsExplicitIdentityInsert 取 false：这一家的自增写法是 IDENTITY(1,1)，产物里没有 SET IDENTITY_INSERT
/// 那类会话开关。刻意不为 IdentityInsertToggle 留覆写点——Core 今天没有调用点，覆写只会得到一颗永远不被调用的虚函数。</summary>
public sealed class XuguKeys : IKeyStrategy
{
    /// <summary>false。实测不是"被库驳回"而是"执行成功、回包 FIELDS=0 ROWS=0"——比驳回更险的是这种"绿着交出 0"。
    /// 批量位假了就是假了：退到逐条，键走 SupportsPostStatementKeyRead。</summary>
    public bool SupportsBulkKeyRetrieval => false;

    /// <summary>true，带真库出处：回读句与多语句批的接收形状都在活库上量过。覆盖面窄：单颗自增列、逐条形、同命令同会话——
    /// 带并发列的回填撞不出去（那一格红在 RETURNING 交空结果集上）。</summary>
    public bool SupportsPostStatementKeyRead => true;

    /// <summary>发 SELECT LAST_INSERT_ID()：与夹具裁判在虚谷上跑通的是同一颗函数、同一种多语句批形状。
    /// 不带参数、不引列名；前面的分号与空格由 Core 补。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key) => writer.Append("SELECT LAST_INSERT_ID()");

    public bool SupportsExplicitIdentityInsert => false;

    /// <summary>发裸 RETURNING 列名，落在语句末尾（与金仓同位置，ReturningPrecedesValues 保持 false）；不带 INTO——
    /// 那一形要 OUT 绑定，而 AdoParameter 没有方向位。列名过 IdentifierPolicy.Xugu：折叠与引号由策略给，
    /// 回填清单与建表清单不会写出两个字形。调用方负责自己那侧的空格——少一个就会得到"…= 1RETURNING"紧贴列名的形状。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
    {
        writer.Append("RETURNING ");

        for (var i = 0; i < returning.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.AppendIdentifier(IdentifierPolicy.Xugu, returning[i].ColumnName);
        }
    }

    /// <summary>没有开关，且刻意不是 virtual（Core 今天没有调用点）。返回 null 让 Core 走"这条语句不需要前置/后置语句"那一支。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => null;
}
