using QuickSql.Metadata;
using QuickSql.Sql;

namespace QuickSql.Providers.SqlServer;

/// <summary>T-SQL 的主键策略。OUTPUT inserted.* 让批量插入一次拿回全部主键，
/// 所以在 SQL Server 上选「保持数据库自增」毫无痛感，而达梦/Oracle 系没有可用的批量 RETURNING、
/// 同一份代码必须退到逐条——别把这里的 true 当成普遍事实。</summary>
public sealed class SqlServerKeys : IKeyStrategy
{
    public bool SupportsBulkKeyRetrieval => true;

    public bool SupportsExplicitIdentityInsert => true;

    /// <summary>调用方负责两侧的空格：这里只落地 OUTPUT inserted.[a], inserted.[b]。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) =>
        EmitReturning(writer, returning, "inserted");

    /// <summary>T-SQL 的 OUTPUT 在 INSERT/UPDATE 取 inserted、在 DELETE 取 deleted，
    /// 而 IKeyStrategy 只有一个不带前缀的入口，所以方言层走这个重载。</summary>
    public void EmitReturning(
        SqlWriter writer,
        IReadOnlyList<ColumnContract> returning,
        string prefix
    )
    {
        writer.Append("OUTPUT ");

        for (var i = 0; i < returning.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.Append(prefix).Append('.');
            writer.AppendIdentifier(IdentifierPolicy.SqlServer, returning[i].ColumnName);
        }
    }

    /// <summary>SET IDENTITY_INSERT 是一条独立的会话开关语句，不是查询的一部分，所以返回文本
    /// 让 Core 拼进同一个批次（SQL Server 声明了 MultiStatement）；writer 在这里用不上。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on)
    {
        var name = table.Schema is { Length: > 0 } schema
            ? $"{IdentifierPolicy.SqlServer.Quote(schema)}.{IdentifierPolicy.SqlServer.Quote(table.Name)}"
            : IdentifierPolicy.SqlServer.Quote(table.Name);

        return $"SET IDENTITY_INSERT {name} {(on ? "ON" : "OFF")}";
    }

    /// <summary>T-SQL 的 OUTPUT 跟着语句就把键发回客户端，"语句后回读"那一档在这一家用不到：
    /// Core 只在批量位为 false 时才问这一位。</summary>
    public bool SupportsPostStatementKeyRead => false;

    /// <summary>不可达（SupportsBulkKeyRetrieval 为 true 时 Core 不会走到这里）。
    /// 留抛不留空体：将来谁把批量位掰了而没想清楚这一位，要撞见一条消息而不是一颗 null。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key) =>
        throw new QuickSqlException("T-SQL 走 OUTPUT inserted.*，语句后回读那一档在这一家不可达。");
}
