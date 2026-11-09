using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.ClickHouse;

/// <summary>从 system.tables ∶ system.columns 两张系统表读结构。这里只有 ADO.NET 抽象层的虚调用，
/// 不认识任何驱动类型。列清单一张表就够（目录把"列"与"形状"放同一行），表存在性由"0 行"给出；
/// 表备注走 system.tables.comment、列备注走 system.columns.comment，空串口径同格：本家 comment 默认 ''，
/// "有这概念而没写"折成空串而不是 null；IsIdentity 恒 false（本家没有"由库递增的列"那一档）；
/// IsPrimaryKey/Scale 两格不填——主键在引擎的 ORDER BY 里、Decimal(P,S) 的档位藏在 type 文本里，
/// 不作文本解析，null 是"读不出"不是"没有"。本文件最重要的一条：可空性长在型名里不在位里——
/// 可空列的 type 是 Nullable(T)、非空列是裸 T，读侧要剥掉最外层包裹并把参数化型里的空格压掉
/// 才对得上建侧的发射形；LowCardinality(Nullable(T)) 那类嵌套不在形状里，现场手工建出嵌套时按裸型读回，
/// 判据上会假红。system.columns 那一问在活库 26.9.21.9 的探针里走通过。</summary>
public sealed class ClickHouseSchemaReader : ISchemaReader
{
    /// <summary>点给了 database 的读法。@db/@table 各自独立（本家参数带名字，可以复用）。
    /// ORDER BY position：OrdinalPosition 用目录给的序，不折基（"折成 1 基就是造假"）。</summary>
    const string ColumnsSql = """
        SELECT name, type, position, comment
        FROM system.columns
        WHERE database = @db AND table = @table
        ORDER BY position
        """;

    /// <summary>未点 database 那一支：currentDatabase() 把"缺省那一格"写进 SQL 而不写进绑定值——
    /// 参数可以吃 null，但对"连个名都没有"那格的诚实写法是让目录函数自己答。</summary>
    const string ColumnsSqlForCurrentDatabase = """
        SELECT name, type, position, comment
        FROM system.columns
        WHERE database = currentDatabase() AND table = @table
        ORDER BY position
        """;

    /// <summary>表备注一对：与列清单同一对谓词形状。name 是 system.tables 那一侧的表名字段
    ///（system.columns 那一侧的表字段叫 table，两张目录不同名）。</summary>
    const string TableCommentSql = """
        SELECT comment
        FROM system.tables
        WHERE database = @db AND name = @table
        """;

    /// <summary>表备注的缺省 database 那一支（与 TableCommentSql 成对，同理由）。</summary>
    const string TableCommentSqlForCurrentDatabase = """
        SELECT comment
        FROM system.tables
        WHERE database = currentDatabase() AND name = @table
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        var database = table.Schema is { Length: > 0 } named ? named : null;

        var columns = new List<SchemaColumn>();

        await using (var command = Command(connection, database is null ? ColumnsSqlForCurrentDatabase : ColumnsSql))
        {
            command.Parameters.Add(Param(command, "db", database));
            command.Parameters.Add(Param(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            // 0 行 = 表不在。真读进来的每一行：名字、型（剥 Nullable 包裹）、目录给的序。
            do
            {
                columns.Add(new SchemaColumn(
                    reader.GetString(0),
                    StoreType(reader.GetString(1)),
                    (int?)null,
                    (int?)null,
                    IsNullable(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2))
                {
                    IsIdentity = false,

                    // 列备注与表备注同一空串口径：本家 comment 默认 ''，"有这概念而没写"折成空串，
                    // 不是 null（后者是"读不出"那一格，比差别时两形要分得开）。
                    Comment = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                });
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        string tableComment;

        await using (var command = Command(connection, database is null ? TableCommentSqlForCurrentDatabase : TableCommentSql))
        {
            command.Parameters.Add(Param(command, "db", database));
            command.Parameters.Add(Param(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // 空串 = 有这概念而没写（本家 comment 默认 ''），缺行 = 读不出；两形按 SchemaTable.Comment 的口径分开。
            // 列清单已经证明表在场，这里 0 行多半是目录的边界，按读不出记而不是兜一个空串。
            tableComment = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
        }

        return new SchemaTable(table, [.. columns]) { Comment = tableComment };
    }

    /// <summary>恒 null：String 那一档没有库侧第二把尺可问（本家"宽"只在仓库侧）。
    /// 交 null = "本家判不了"而不是"没有宽"。</summary>
    public Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
        => Task.FromResult<LengthSemanticsReading?>(null);

    /// <summary>发出去的字形对齐建侧的两步「剥皮→压空格」：只剥最外层一颗 Nullable( 包裹
    ///（Nullable(String) → String、Nullable(Decimal(18, 4)) → Decimal(18,4)，裸 DateTime64(3) 原样），
    /// 再把参数化型里的空格压掉——目录给 Decimal(18, 4) 而发射形 Decimal(18,4)，压掉才同一个文本口。
    /// 不做整条小写折名：本家型名大小写敏感，折之即假。</summary>
    static string StoreType(string storeType)
    {
        var text = storeType.Trim();

        if (text.StartsWith("Nullable(", StringComparison.Ordinal) && text.EndsWith(')'))
            text = text[9..^1].Trim();

        return text.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    static bool IsNullable(string storeType)
        => storeType.Trim().StartsWith("Nullable(", StringComparison.Ordinal);

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>具名参数：前缀 '@' 写在对象名上（与 MySql/PostgreSQL/SqlServer 三族同一形状）。
    /// 对象名要不要带前缀的那一半没有读数【推断·与前缀三方族同形】。</summary>
    static DbParameter Param(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = '@' + name;
        parameter.Value = value ?? (object)DBNull.Value;

        return parameter;
    }
}
