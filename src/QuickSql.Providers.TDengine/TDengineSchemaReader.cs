using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.TDengine;

/// <summary>从 INFORMATION_SCHEMA.INS_COLUMNS ∶ INS_TABLES 读结构（VendorDoc·TDengine：系统信息页，
/// 列名逐颗对过官方 01-meta 清单）。这里只有 ADO.NET 抽象层的虚调用，不认识任何驱动类型。
/// 列清单一张表就够（目录把"列"与"形状"放同一行），表存在性由"0 行"给出；表备注走
/// INS_TABLES.table_comment，空串口径同格：没写的备注读回 NULL，折成空串；
/// 列备注没有读数——INS_COLUMNS 没有备注列，SchemaColumn.Comment 留 null（"读不出"那一格，
/// 与"建侧本就没有这一格"同果）。IsIdentity 恒 false（本家没有"由库递增的列"那一档）；
/// IsPrimaryKey 不填——主键是"首列 TIMESTAMP 系统自动设"的隐含事实，目录没有那一颗位；
/// Scale 走 col_scale（DECIMAL 的档位目录有专门的 INT 列，不用作文本解析）；
/// MaxBytes 走 col_length（本家 VARCHAR 的 n 量字节，字符数没有第二把尺）。参数是位置形 ?，
/// 参数名以 @ 打头（stmt2 的 data 分类，与 binder 那颗 DriverName 同一纪律）。
/// 本文件产出的每一条问句都没有在一台活库上跑过。</summary>
public sealed class TDengineSchemaReader : ISchemaReader
{
    /// <summary>点给了 db 的读法。两个 ? 各自独立（位置形，顺序即绑定序）。ORDER BY col_id：
    /// OrdinalPosition 用目录给的序，不折基（"折成 1 基就是造假"）。</summary>
    const string ColumnsSql = """
        SELECT col_name, col_type, col_length, col_scale, col_nullable, col_id
        FROM INFORMATION_SCHEMA.INS_COLUMNS
        WHERE db_name = ? AND table_name = ?
        ORDER BY col_id
        """;

    /// <summary>未点 db 那一支：DATABASE() 把"缺省那一格"写进 SQL 而不写进绑定值——
    /// 参数可以吃 null，但对"连个名都没有"那格的诚实写法是让目录函数自己答。</summary>
    const string ColumnsSqlForCurrentDatabase = """
        SELECT col_name, col_type, col_length, col_scale, col_nullable, col_id
        FROM INFORMATION_SCHEMA.INS_COLUMNS
        WHERE db_name = DATABASE() AND table_name = ?
        ORDER BY col_id
        """;

    /// <summary>表备注：与列清单同一对谓词形状。table_comment 是 INS_TABLES 里备注那颗的官方列名。</summary>
    const string TableCommentSql = """
        SELECT table_comment
        FROM INFORMATION_SCHEMA.INS_TABLES
        WHERE db_name = ? AND table_name = ?
        """;

    /// <summary>表备注的缺省 db 那一支（与 TableCommentSql 成对，同理由）。</summary>
    const string TableCommentSqlForCurrentDatabase = """
        SELECT table_comment
        FROM INFORMATION_SCHEMA.INS_TABLES
        WHERE db_name = DATABASE() AND table_name = ?
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        var database = table.Schema is { Length: > 0 } named ? named : null;

        var columns = new List<SchemaColumn>();

        await using (var command = Command(connection, database is null ? ColumnsSqlForCurrentDatabase : ColumnsSql))
        {
            if (database is not null)
                command.Parameters.Add(Param(command, database));

            command.Parameters.Add(Param(command, table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            // 0 行 = 表不在。真读进来的每一行：名字、型、字节宽度、标度、可空、目录给的序。
            do
            {
                columns.Add(new SchemaColumn(
                    reader.GetString(0),
                    reader.GetString(1).Trim(),
                    null,
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    !reader.IsDBNull(4) && reader.GetInt32(4) != 0,
                    reader.IsDBNull(5) ? null : (int)reader.GetInt16(5))
                {
                    IsIdentity = false,

                    // 列备注没有读数：INS_COLUMNS 没有备注列。null = "读不出"，
                    // 与"建侧本就没有这一格"（官方 column_definition 文法里没有 COMMENT 项）同果。
                    Comment = null,

                    // 主键是"首列 TIMESTAMP 系统自动设"的隐含事实，目录没有那一颗位。
                    IsPrimaryKey = null,

                    Scale = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                });
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        string tableComment;

        await using (var command = Command(connection, database is null ? TableCommentSqlForCurrentDatabase : TableCommentSql))
        {
            if (database is not null)
                command.Parameters.Add(Param(command, database));

            command.Parameters.Add(Param(command, table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // 空串 = 有这概念而没写（目录里没写的备注读回 NULL），缺行 = 读不出；两形按 SchemaTable.Comment 的口径分开。
            // 列清单已经证明表在场，这里 0 行多半是目录的边界，按读不出记而不是兜一个空串。
            tableComment = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
        }

        return new SchemaTable(table, [.. columns]) { Comment = tableComment };
    }

    /// <summary>恒 null：VARCHAR 那一档没有库侧第二把尺可问（本家"宽"只有字节一把尺，已在 col_length 里）。
    /// 交 null = "本家判不了"而不是"没有宽"。</summary>
    public Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
        => Task.FromResult<LengthSemanticsReading?>(null);

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>位置形参数：值按顺序进集合，名字只承担 stmt2 的 data 分类那一颗语义
    ///（@ 前缀 → data，VendorDoc·TDengine·驱动源码 TDengineCommand.Statement 的分类规则）。</summary>
    static DbParameter Param(DbCommand command, string value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = "@p";
        parameter.Value = value ?? (object)DBNull.Value;

        return parameter;
    }
}
