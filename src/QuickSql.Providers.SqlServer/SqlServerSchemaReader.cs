using System.Data.Common;
using QuickSql.Metadata;

namespace QuickSql.Providers.SqlServer;

/// <summary>从 sys.columns 读结构。这里只有 ADO.NET 抽象层的虚调用：
/// 驱动类型由 DriverRegistry 在运行期解析，本文件不认识 SqlConnection。
/// 一趟读三样：列（含列备注、自增、标度）、表备注、索引清单（含主键列）——后两样从
/// sys.extended_properties 与 sys.indexes 来，缺了它们结构同步就判不出差别、同步不了。</summary>
public sealed class SqlServerSchemaReader : ISchemaReader
{
    /// <summary>class = 1 是 OBJECT_OR_COLUMN，minor_id 是列序（0 留给表本身）。
    /// 备注的值位是 sql_variant，所以必须 CAST 出来才读得成字符串。</summary>
    const string ColumnsSql = """
        SELECT c.column_id, c.name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable,
               c.is_identity, CAST(ep.value AS nvarchar(4000)) AS comment
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT OUTER JOIN sys.extended_properties ep
            ON ep.major_id = c.object_id AND ep.class = 1 AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
        WHERE c.object_id = OBJECT_ID(@table)
        ORDER BY c.column_id
        """;

    const string TableCommentSql = """
        SELECT CAST(ep.value AS nvarchar(4000))
        FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(@table) AND ep.class = 1 AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        """;

    /// <summary>type IN (1,2) 是有行的索引（聚集 / 非聚集），排掉 0（堆）与 3/4（XML、全文）——
    /// 后两样的形状契约里表达不了，读回来只会多一条假名。主键也一起读，不为补建，
    /// 为的是判出"库里的主键列与契约差哪一列"。is_unique_constraint 一起带回来：
    /// 那一颗是唯一约束不是索引，DROP INDEX 删不动它，所以它在计划层单独一格。</summary>
    const string IndexesSql = """
        SELECT i.name, i.is_unique, i.is_primary_key, i.is_unique_constraint, COL_NAME(ic.object_id, ic.column_id) AS column_name
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        WHERE i.object_id = OBJECT_ID(@table) AND i.type IN (1, 2) AND i.is_hypothetical = 0
        ORDER BY i.index_id, ic.key_ordinal
        """;

    /// <summary>量纲探测。只读目录视图与会话属性，不建临时表、不写数据：
    /// 信创生产库常禁 DDL 权限，探测工具不能反过来要求它。</summary>
    const string ProbeSql = """
        SELECT ty.name AS type_name, c.max_length, c.collation_name
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column
        """;

    public async Task<SchemaTable?> ReadTableAsync(
        DbConnection connection,
        TableInfo table,
        CancellationToken ct = default
    )
    {
        var columns = new List<SchemaColumn>();
        string? tableComment = null;

        await using (var command = Command(connection, ColumnsSql))
        {
            command.Parameters.Add(Parameter(command, "table", Qualified(table)));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(1);
                var storeType = reader.GetString(2);
                var byteLength = reader.GetInt16(3);

                columns.Add(
                    new SchemaColumn(
                        name,
                        StoreType(storeType, byteLength, reader.GetByte(4), reader.GetByte(5)),
                        MaxChars(storeType, byteLength),
                        MaxBytes(storeType, byteLength),
                        reader.GetBoolean(6),
                        reader.GetInt32(0)
                    )
                    {
                        // 与表备注那一格同一个折法：LEFT JOIN 没配上行 = 这一列没写备注，交回空串。
                        // 交 null 说的是"这一家没有列注释这个概念"，那会把"库里备注被删掉"读成"判不了"，
                        // 被删的那一条再也补不回去。
                        Comment = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        IsIdentity = reader.GetBoolean(7),
                        IsPrimaryKey = false,
                        Scale = reader.GetByte(5),
                    }
                );
            } while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        await using (var command = Command(connection, TableCommentSql))
        {
            command.Parameters.Add(Parameter(command, "table", Qualified(table)));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // 读不到那一行 = 这张表没有表备注扩展属性，交回空串而不是 null：null 说的是
            // "这一家没有表备注这个概念"，而那会把补备注那一档整格关掉。
            tableComment =
                await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0)
                    ? reader.GetString(0)
                    : string.Empty;
        }

        var indexes = await ReadIndexesAsync(connection, table, columns, ct).ConfigureAwait(false);

        return new SchemaTable(table, [.. columns]) { Comment = tableComment, Indexes = indexes };
    }

    /// <summary>索引一趟读两件事：非主键的那几条原样交回，主键那一条的列回写进 SchemaColumn.IsPrimaryKey。
    /// 列名按 key_ordinal 聚合成有序清单——索引的列序是它的内容的一部分（(a,b) 与 (b,a) 不是同一条）。</summary>
    static async Task<IReadOnlyList<SchemaIndex>> ReadIndexesAsync(
        DbConnection connection,
        TableInfo table,
        List<SchemaColumn> columns,
        CancellationToken ct
    )
    {
        var read =
            new List<(
                string Name,
                bool Unique,
                bool Primary,
                bool Constraint,
                List<string> Columns
            )>();

        await using (var command = Command(connection, IndexesSql))
        {
            command.Parameters.Add(Parameter(command, "table", Qualified(table)));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var unique = reader.GetBoolean(1);
                var primary = reader.GetBoolean(2);
                var constraint = reader.GetBoolean(3);
                var columnName = reader.IsDBNull(4) ? null : reader.GetString(4);

                if (columnName is null)
                    continue;

                if (read.Count > 0 && read[^1].Name == name)
                    read[^1].Columns.Add(columnName);
                else
                    read.Add((name, unique, primary, constraint, [columnName]));
            }
        }

        var keys = new HashSet<string>(
            read.Where(static x => x.Primary).SelectMany(static x => x.Columns),
            StringComparer.OrdinalIgnoreCase
        );

        for (var i = 0; i < columns.Count; i++)
            if (keys.Contains(columns[i].Name))
                columns[i] = columns[i] with { IsPrimaryKey = true };

        return
        [
            .. read.Where(static x => !x.Primary)
                .Select(x => new SchemaIndex(x.Name, x.Unique, [.. x.Columns])
                {
                    IsConstraint = x.Constraint,
                }),
        ];
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(
        DbConnection connection,
        TableInfo table,
        string columnName,
        CancellationToken ct = default
    )
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "table", Qualified(table)));
        command.Parameters.Add(Parameter(command, "column", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var storeType = reader.GetString(0);
        var definitionBytes = reader.GetInt16(1);
        var collation = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

        // nvarchar/nchar 的 max_length 是字节数而 n 是字符数（UTF-16，恒 2 倍）；
        // varchar/char 的 n 就是字节数，排序规则只决定"一个字节能装哪个字符"。
        // 所以量纲只看类型名，不看排序规则；UTF-8 与否从 collation_name 自己的后缀读得出。
        var charSemantics = storeType is "nvarchar" or "nchar";
        var declared =
            definitionBytes < 0 ? -1
            : charSemantics ? definitionBytes / 2
            : definitionBytes;

        return new LengthSemanticsReading(collation, declared, definitionBytes, charSemantics);
    }

    /// <summary>形状对得上吗——比的是归一后的存储类型文本，而归一只有本家做得起。
    /// 发射侧一律大写而目录一律小写；DATETIME2(3) 发出去而目录把标度单存一格；
    /// 手工建的表上 DECIMAL 读回 numeric。这三折全在这一个私有归一口里，所以建侧与读侧不可能各自折成两个样子。</summary>
    public bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual) =>
        string.Equals(
            Normalize(SqlServerTypes.Instance.GetStoreType(expected)),
            Normalize(actual.StoreType),
            StringComparison.Ordinal
        );

    static string Normalize(string storeType)
    {
        var lower = storeType.Trim().ToLowerInvariant();

        return lower.Replace("numeric", "decimal", StringComparison.Ordinal);
    }

    static string StoreType(string storeType, int byteLength, byte precision, byte scale) =>
        storeType switch
        {
            "nvarchar" or "nchar" or "varchar" or "char" or "varbinary" or "binary" => byteLength
            < 0
                ? $"{storeType}(max)"
                : $"{storeType}({(storeType.StartsWith('n') ? byteLength / 2 : byteLength)})",
            "decimal" or "numeric" => $"{storeType}({precision},{scale})",
            // 发射侧写的是 DATETIME2(3) 而目录把标度单存一格（不写精度时它是 7），
            // 不带出来就成了"每颗时间列都判不出差"。
            "datetime2" or "datetimeoffset" or "time" => $"{storeType}({scale})",
            _ => storeType,
        };

    static int? MaxChars(string storeType, int byteLength) =>
        byteLength < 0 ? null
        : storeType is "nvarchar" or "nchar" ? byteLength / 2
        : byteLength;

    static int? MaxBytes(string storeType, int byteLength) =>
        byteLength < 0 ? null
        : storeType is "nvarchar" or "nchar" ? byteLength
        : byteLength;

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    static DbParameter Parameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = '@' + name;
        parameter.Value = value;

        return parameter;
    }

    /// <summary>OBJECT_ID 吃的是带引号的两段名，所以这里必须与查询层用同一套标识符策略，
    /// 否则混合大小写的表在 doctor 里会读成"表不存在"。</summary>
    static string Qualified(TableInfo table) =>
        table.Schema is { Length: > 0 } schema
            ? $"{IdentifierPolicy.SqlServer.Quote(schema)}.{IdentifierPolicy.SqlServer.Quote(table.Name)}"
            : IdentifierPolicy.SqlServer.Quote(table.Name);
}
