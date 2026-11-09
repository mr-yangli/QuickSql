using System.Data.Common;
using QuickSql.Metadata;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>从 information_schema 与 pg_* 目录读结构。列清单走 information_schema（SQL 标准、跨各家最稳），
/// 量纲探测走 pg_attribute（atttypmod 只在那里）；索引走基表 pg_index 而不是 pg_indexes 便利视图
/// （后者要把 CREATE 语句文本解析一遍才拿得出列序，而列序是索引内容的一部分）。
/// 表达式索引（indkey 那格是 0）整条挡在读侧之外：契约表达不了它，读回来会在"库里多出一条索引"里
/// 多一条假名，开了删索引那档会被删掉。备注从 pg_description 读（表 objsubid=0、列等于 attnum），
/// 自增位读两半（is_identity 或 SERIAL 的 nextval 默认值）。</summary>
public sealed class PostgreSqlSchemaReader : ISchemaReader
{
    /// <summary>备注那颗 LEFT JOIN 不成的意思是"这一列没写备注"，交回空串而不是 null：null 说的是
    /// "这一家没有列注释这个概念"，那会把整档备注同步关掉，"库里备注被删掉"就永远读不回来。
    /// 自增位读两半：GENERATED … AS IDENTITY 落在 is_identity，SERIAL 那一支只有 nextval 默认值。</summary>
    const string ColumnsSql = """
        SELECT c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length,
               c.numeric_precision, c.numeric_scale, c.datetime_precision, c.is_nullable,
               CASE WHEN c.is_identity = 'YES' OR c.column_default LIKE 'nextval%' THEN true ELSE false END,
               COALESCE((SELECT d.description
                         FROM pg_description d
                         JOIN pg_class dc ON dc.oid = d.objoid
                         JOIN pg_namespace dn ON dn.oid = dc.relnamespace
                         WHERE dn.nspname = c.table_schema AND dc.relname = c.table_name AND d.objsubid = c.ordinal_position), '')
        FROM information_schema.columns c
        WHERE c.table_schema = @schema AND c.table_name = @table
        ORDER BY c.ordinal_position
        """;

    /// <summary>表备注同一次 JOIN 的三个条件，只是 objsubid = 0 换成表本身。COALESCE 让这一句恒交一行：
    /// 交回空串说的是"有这概念而库里没写"，零行会被读成"这一家没这概念"。</summary>
    const string TableCommentSql = """
        SELECT COALESCE((SELECT d.description
                         FROM pg_description d
                         JOIN pg_class dc ON dc.oid = d.objoid
                         JOIN pg_namespace dn ON dn.oid = dc.relnamespace
                         WHERE dn.nspname = @schema AND dc.relname = @table AND d.objsubid = 0), '')
        """;

    /// <summary>indkey 逐位展开成（列号, 序号）两列，再经 pg_attribute 换成列名——pg_get_indexdef 交回的
    /// 是带引号的发射形，名字匹配必须两侧同一个字形。唯一约束那颗位按"名字对名字"判而不用 conindid
    /// （那一颗要 PG 12.1+，叶子有 9.2 内核的）。仍留一处 9.4+ 依赖：WITH ORDINALITY 在 9.2 内核可能是
    /// 语法错（推断，没有读数不换写法——换 generate_series 要连累列序序号一起重写）。</summary>
    const string IndexesSql = """
        SELECT ic.relname, i.indisunique, i.indisprimary,
               CASE WHEN con.conname IS NULL THEN false ELSE true END AS is_constraint,
               a.attname
        FROM pg_class t
        JOIN pg_namespace ns ON ns.oid = t.relnamespace
        JOIN pg_index i ON i.indrelid = t.oid
        JOIN pg_class ic ON ic.oid = i.indexrelid
        CROSS JOIN LATERAL unnest(i.indkey::int[]) WITH ORDINALITY AS k(attnum, ord)
        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
        LEFT JOIN pg_constraint con ON con.conrelid = t.oid AND con.conname = ic.relname AND con.contype IN ('u', 'x')
        WHERE ns.nspname = @schema AND t.relname = @table AND 0 <> ALL (i.indkey::int[])
        ORDER BY ic.relname, k.ord
        """;

    /// <summary>量纲探测，只读目录表：不建临时表、不写数据（生产库常禁 DDL 权限）。
    /// atttypmod 是这一家唯一存"声明宽度"的地方，编码是 n + 4（那 4 是变长头的历史遗留），-1 表示没写宽度。</summary>
    const string ProbeSql = """
        SELECT ty.typname, a.atttypmod, COALESCE(co.collname, ''), a.attnotnull
        FROM pg_attribute a
        JOIN pg_class cl ON cl.oid = a.attrelid
        JOIN pg_namespace ns ON ns.oid = cl.relnamespace
        JOIN pg_type ty ON ty.oid = a.atttypid
        LEFT JOIN pg_collation co ON co.oid = a.attcollation
        WHERE ns.nspname = @schema AND cl.relname = @table AND a.attname = @column AND a.attnum > 0 AND NOT a.attisdropped
        """;

    public async Task<SchemaTable?> ReadTableAsync(
        DbConnection connection,
        TableInfo table,
        CancellationToken ct = default
    )
    {
        var schema = table.Schema ?? "public";
        var columns = new List<SchemaColumn>();
        string? tableComment = null;

        await using (var command = Command(connection, ColumnsSql))
        {
            command.Parameters.Add(Parameter(command, "schema", schema));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var dataType = reader.GetString(2);
                var chars = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);

                columns.Add(
                    new SchemaColumn(
                        reader.GetString(1),
                        StoreType(dataType, chars, reader),
                        chars,
                        MaxBytes(dataType, chars),
                        reader.GetString(7) is "YES",
                        reader.GetInt32(0)
                    )
                    {
                        Comment = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        IsIdentity = reader.GetBoolean(8),
                        IsPrimaryKey = false,
                        Scale = Scale(dataType, reader),
                    }
                );
            } while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        await using (var command = Command(connection, TableCommentSql))
        {
            command.Parameters.Add(Parameter(command, "schema", schema));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            tableComment =
                await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0)
                    ? reader.GetString(0)
                    : string.Empty;
        }

        var indexes = await ReadIndexesAsync(connection, schema, table, columns, ct)
            .ConfigureAwait(false);

        return new SchemaTable(table, [.. columns]) { Comment = tableComment, Indexes = indexes };
    }

    /// <summary>索引一趟读两件事：非主键的那几条原样交回，主键那一条的列回写进 SchemaColumn.IsPrimaryKey
    /// （那一格从前恒为"读不出"，"库里的主键换了一列"在本家连报都报不出来）。列名按 indkey 的原序聚合。</summary>
    static async Task<IReadOnlyList<SchemaIndex>> ReadIndexesAsync(
        DbConnection connection,
        string schema,
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
            command.Parameters.Add(Parameter(command, "schema", schema));
            command.Parameters.Add(Parameter(command, "table", table.Name));

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
            StringComparer.Ordinal
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

        command.Parameters.Add(Parameter(command, "schema", table.Schema ?? "public"));
        command.Parameters.Add(Parameter(command, "table", table.Name));
        command.Parameters.Add(Parameter(command, "column", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var typeName = reader.GetString(0);
        var typmod = reader.GetInt32(1);
        var collation = reader.GetString(2);

        var declared = typmod < 4 ? -1 : typmod - 4;

        // 这一家没有第二把尺子：varchar(n) 的 n 恒为字符数。DefinitionByteLength 填同一个数而不是 0——
        // 填 0 会把"问不出答案"写成"答案是没有"（doctor 会报成"这一列长度 0"）。
        return new LengthSemanticsReading(
            collation,
            declared,
            declared,
            typeName is "varchar" or "bpchar" or "text"
        );
    }

    /// <summary>这一列有没有存量空值——把一列收紧成 NOT NULL 前问的那一句。
    /// 取一行而不是数全表：COUNT(*) 在大表上是一次全表扫，这一问要的只是"有没有"，LIMIT 1 命中就收。
    /// 标识符只能拼文本、参数化吃不下对象名，所以这是本 reader 唯一不用 @param 的地方；
    /// 字形与建表、查询同一个口（Quote：双引号 + 内部引号翻倍），"探的是哪一列"与"要 ALTER 的是哪一列"不可能是两个名字。</summary>
    public Task<bool?> ColumnHasNullsAsync(
        DbConnection connection,
        TableInfo table,
        string columnName,
        CancellationToken ct = default
    )
    {
        var sql =
            $"SELECT 1 FROM {Qualified(table)} WHERE {IdentifierPolicy.PostgreSQL.Quote(columnName)} IS NULL LIMIT 1";

        return HasRowAsync(connection, sql, ct);
    }

    /// <summary>表名限定与发射侧同一颗默认模式（public）：结构同步连的那条会话可以带着别的
    /// search_path，而这一句探的必须是刚才那张要被 ALTER 的表。</summary>
    static string Qualified(TableInfo table) =>
        $"{IdentifierPolicy.PostgreSQL.Quote(table.Schema ?? "public")}.{IdentifierPolicy.PostgreSQL.Quote(table.Name)}";

    static async Task<bool?> HasRowAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        using var command = Command(connection, sql);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>形状对得上吗——比归一后的存储类型文本：发射侧一律大写而 information_schema 小写长名，
    /// 手工建的表上 NUMERIC 不带位数而契约侧永远带，别名 int4/bool 与正名同型。
    /// 本家交回 true/false 而不是 null（位数、标度与时间精度三格都读得回来）；
    /// MaxChars 与 Scale 两格是加料（给 doctor 与"缺列就补"用），判形状只看这一个文本口。</summary>
    public bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual) =>
        string.Equals(
            Normalize(PostgreSqlTypes.Instance.GetStoreType(expected)),
            Normalize(actual.StoreType),
            StringComparison.Ordinal
        );

    static string Normalize(string storeType)
    {
        var text = storeType.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');

        return open < 0
            ? Fold(text)
            : Fold(text[..open])
                + text[open..].Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>目录与发射形之间那几颗同义词，一张表收完（pg_type 别名与 information_schema 标准长名都指进来）。</summary>
    static string Fold(string name) =>
        name switch
        {
            "int4" or "int" or "serial" => "integer",
            "int2" or "smallserial" => "smallint",
            "int8" or "bigserial" => "bigint",
            "float4" => "real",
            "float8" => "double precision",
            "bool" => "boolean",
            "dec" or "decimal" => "numeric",
            "character varying" => "varchar",
            "character" or "bpchar" => "char",
            "timestamp without time zone" => "timestamp",
            "timestamp with time zone" => "timestamptz",
            "time without time zone" => "time",
            _ => name,
        };

    static string StoreType(string dataType, int? chars, DbDataReader reader) =>
        dataType switch
        {
            "character varying" => chars is { } n ? $"varchar({n})" : "text",
            "character" => chars is { } n ? $"char({n})" : "char",
            // 裸 NUMERIC 在这一支交回 NULL 位数（那是"不限定"，不是"0"）；无条件 GetInt32 读到不带括号的 numeric 会抛。
            "numeric" => reader.IsDBNull(4)
                ? "numeric"
                : $"numeric({reader.GetInt32(4)},{reader.GetInt32(5)})",
            "timestamp without time zone" => Timed("timestamp", reader),
            "timestamp with time zone" => Timed("timestamptz", reader),
            "time without time zone" => Timed("time", reader),
            _ => dataType,
        };

    static string Timed(string name, DbDataReader reader) =>
        reader.IsDBNull(6) ? name : $"{name}({reader.GetInt32(6)})";

    /// <summary>标度在小数族是 numeric_scale，时间族是 datetime_precision：两格在契约侧是同一个词，折成一颗。</summary>
    static int? Scale(string dataType, DbDataReader reader) =>
        dataType switch
        {
            "numeric" => reader.IsDBNull(5) ? null : reader.GetInt32(5),
            "timestamp without time zone"
            or "timestamp with time zone"
            or "time without time zone" => reader.IsDBNull(6) ? null : reader.GetInt32(6),
            _ => null,
        };

    /// <summary>字节上限在这家问不出来，所以只在 bytea 上给一个"按字节量"的事实，其余留 null。</summary>
    static int? MaxBytes(string dataType, int? chars) => dataType is "bytea" ? chars : null;

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
}
