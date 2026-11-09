using QuickSql.Metadata;
using QuickSql.Sql;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的主键策略，四个宿主（INSERT/UPDATE/DELETE/UPSERT … DO UPDATE，加多列、多行）
/// 全在活库上逐条问过：批量 RETURNING 取回主键可用。第二档可指认的事实是"回填不改变计数"：
/// 带 RETURNING 的写入语句，驱动的 ExecuteNonQuery 仍然交回受影响行数——若它变成"回传的行数"，
/// ExecuteAffrowsAsync 那一族调用会全体错位。自增与 T-SQL 同侧而理由不同一条：显式值合法、
/// 且库会把序列挪到它上面，任何时候都允许写，所以 IdentityInsertToggle 返回 null
/// （这件事不需要开关，不是"没读到有这能力"）。SupportsPostStatementKeyRead 取 false 说的是
/// "做得到但没有入口"：last_insert_rowid() 是连接级的、且一条多行 INSERT 只回最后一行的号。</summary>
public sealed class SqliteKeys : IKeyStrategy
{
    public bool SupportsBulkKeyRetrieval => true;

    /// <summary>显式值合法、且库会把序列挪到它上面。取 true 的下游后果与 T-SQL 同一条
    /// （"带主键插入"那一档可行），差别只在不需要开关。</summary>
    public bool SupportsExplicitIdentityInsert => true;

    /// <summary>句尾 RETURNING a, b，与 PG 系同形；调用方负责自己那侧的空格。
    /// 一颗本家特有的取舍：这一族的 RETURNING 对任何表达式都成立，理论上能回填
    /// "库里刚算出来的任何一格"（DEFAULT 值、CURRENT_TIMESTAMP），而契约那条链
    /// 今天只有 SqlServerTypes 会置位——本家有承接的语法而契约没有那一格的调用方。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
    {
        writer.Append("RETURNING ");

        for (var i = 0; i < returning.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.AppendIdentifier(IdentifierPolicy.Sqlite, returning[i].ColumnName);
        }
    }

    /// <summary>没有开关，而且这一口刻意不是 virtual（Core 今天没有调用点）。
    /// 返回 null 让 Core 走"这条语句不需要前置/后置语句"那一支。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => null;

    /// <summary>做得到，但没有入口。</summary>
    public bool SupportsPostStatementKeyRead => false;

    /// <summary>不可达（SupportsBulkKeyRetrieval 为 true 时 Core 不会走到这里）；留抛不留空体。
    /// 抛的话里多说一句"其实做得到"，因为这一条与 GBase 8s 那一条只差一颗开关的读法，容易抄错。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key) =>
        throw new QuickSqlException(
            "SQLite 走批量 RETURNING，语句后回读那一档在这一家没有入口"
                + $"（本次要回填的是「{key.ColumnName}」）。这一家确实有 last_insert_rowid()、也支持一条文本里放两句"
                + "（两处都有实测，见 SqliteKeys 类注释第四段），但 Core 只在批量回填位为 false 时才问这颗开关；"
                + "真要退到那一条，注意它是连接级的、且多行插入只回最后一行的号。"
        );
}
