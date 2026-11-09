using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.MySqlBase;

/// <summary>从 information_schema 读结构。索引走 statistics 而不是 show index：后者是命令不是视图、另要权限，
/// 代价是把一条索引摊成每列一行，列序按 seq_in_index 自己聚（列序是索引内容的一部分）。
/// 两颗特有挡法：column_name 为 NULL 的行是函数索引，契约表达不了它，整条挡在外面（与 PG 家挡 indkey=0 同一取向）；
/// 外键的支撑索引带 IsConstraint = true 交回（DROP INDEX 删它会当场报 needed in a foreign key constraint）。
/// 两把尺子在同一张视图里：CHARACTER_MAXIMUM_LENGTH 给声明的字符数、CHARACTER_OCTET_LENGTH 给字节上限——
/// 问得出，不用建临时表。模式为空给 DATABASE()：目录视图必须按 schema 过滤，
/// information_schema.columns 是全库并列的，省略过滤是静默串表而不是报错。
/// 一处已知不读的：tinyint(1) 还是 tinyint(4) 只有 column_type 分得出，而本层型名从 data_type 拼（缺口，不靠猜）。</summary>
public sealed class MySqlSchemaReader : ISchemaReader
{
    /// <summary>比基础列多读四颗：datetime_precision（时刻族的位数，类型名里没有它）、column_comment
    /// （没写过就是空串而不是 NULL——"库里备注被删掉"才判得出差别）、extra（自增位写在这颗文本里，
    /// 取值还有 on update CURRENT_TIMESTAMP，判自增只能包含着判）、character_octet_length。
    /// numeric_scale 不能无条件读：整数列交回 0 而不是 NULL（实测），标度要看 data_type 才定得了（见 Scale）。</summary>
    const string ColumnsSql = """
        SELECT c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.character_octet_length,
               c.numeric_precision, c.numeric_scale, c.datetime_precision, c.is_nullable, c.column_comment, c.extra
        FROM information_schema.columns c
        WHERE c.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND c.table_name = @table
        ORDER BY c.ordinal_position
        """;

    /// <summary>表备注：那一颗字段同样是"没写过 = 空串"。表不存在时这一句交零行，而调用它的地方
    /// 已先确认有列（列零行整张表就返回 null），所以零行读作"问不出"而不是"这张表不存在"。</summary>
    const string TableCommentSql = """
        SELECT t.table_comment
        FROM information_schema.tables t
        WHERE t.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND t.table_name = @table
        """;

    /// <summary>statistics 是每索引每列一行，ORDER BY index_name, seq_in_index 让同一条索引的行
    /// 相邻且按列序回来，C# 一侧顺着聚合即可。主键在这一家没有名字：建表写的 PK_x 被库丢掉，
    /// 目录里永远叫 PRIMARY（厂商文档明写），主键那一半走列回写、不走名字。</summary>
    const string IndexesSql = """
        SELECT s.index_name, s.non_unique, s.column_name,
               CASE WHEN fc.constraint_name IS NULL THEN 0 ELSE 1 END AS is_constraint
        FROM information_schema.statistics s
        LEFT JOIN information_schema.table_constraints fc
            ON fc.constraint_name = s.index_name AND fc.table_schema = s.table_schema AND fc.table_name = s.table_name
            AND fc.constraint_type = 'FOREIGN KEY'
        WHERE s.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND s.table_name = @table AND s.column_name IS NOT NULL
        ORDER BY s.index_name, s.seq_in_index
        """;

    /// <summary>量纲探测，只读目录表（information_schema 是视图，不需要写权限）。与 ColumnsSql 分两条
    /// 而不是并成一条，是因为要读的列不同——并成一条会让列表那一趟带上永远用不上的三列。</summary>
    const string ProbeSql = """
        SELECT c.data_type, c.character_maximum_length, c.character_octet_length, c.collation_name, c.character_set_name
        FROM information_schema.columns c
        WHERE c.table_schema = COALESCE(NULLIF(@schema, ''), DATABASE()) AND c.table_name = @table AND c.column_name = @column
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        var columns = new List<SchemaColumn>();
        string? tableComment = null;

        await using (var command = Command(connection, ColumnsSql))
        {
            command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var dataType = reader.GetString(2);
                var chars = CatalogLength(reader, 3);

                columns.Add(new SchemaColumn(
                    reader.GetString(1),
                    StoreType(dataType, chars, reader),
                    chars,
                    MaxBytes(reader),
                    reader.GetString(8) is "YES",
                    reader.GetInt32(0))
                {
                    // column_comment 恒交一行，没写过就是空串；交 null 会把"库里备注被删掉"判成没这概念。
                    Comment = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    // 自增位是 extra 里的子串不是相等：那一格同时装着 on update CURRENT_TIMESTAMP。
                    IsIdentity = reader.GetString(10).Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                    IsPrimaryKey = false,
                    Scale = Scale(dataType, reader),
                });
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        await using (var command = Command(connection, TableCommentSql))
        {
            command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            tableComment = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
        }

        var indexes = await ReadIndexesAsync(connection, table, columns, ct).ConfigureAwait(false);

        return new SchemaTable(table, [.. columns]) { Comment = tableComment, Indexes = indexes };
    }

    /// <summary>索引那一趟读两件事：非主键的那几条原样交回，主键（目录里那颗 PRIMARY）的列回写进
    /// SchemaColumn.IsPrimaryKey。名字不能用来认主键——这一家把建表写的 PK_x 丢掉换成 PRIMARY，
    /// 认它只认 index_name = 'PRIMARY' 这颗常量，比较器也因此是 Ordinal。</summary>
    static async Task<IReadOnlyList<SchemaIndex>> ReadIndexesAsync(
        DbConnection connection,
        TableInfo table,
        List<SchemaColumn> columns,
        CancellationToken ct)
    {
        var read = new List<(string Name, bool Unique, bool Primary, bool Constraint, List<string> Columns)>();

        await using (var command = Command(connection, IndexesSql))
        {
            command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
            command.Parameters.Add(Parameter(command, "table", table.Name));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);

                // 一条索引的每一行都带着同三个事实，聚合只看"名字换没换"。
                var unique = reader.GetInt32(1) == 0;
                var columnName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var constraint = reader.GetInt32(3) == 1;

                if (columnName is null)
                    continue;

                if (read.Count > 0 && read[^1].Name == name)
                    read[^1].Columns.Add(columnName);
                else
                    read.Add((name, unique, name == PrimaryKeyName, constraint, [columnName]));
            }
        }

        var keys = new HashSet<string>(read.Where(static x => x.Primary).SelectMany(static x => x.Columns), StringComparer.Ordinal);

        for (var i = 0; i < columns.Count; i++)
            if (keys.Contains(columns[i].Name))
                columns[i] = columns[i] with { IsPrimaryKey = true };

        return [.. read.Where(static x => !x.Primary)
            .Select(x => new SchemaIndex(x.Name, x.Unique, [.. x.Columns]) { IsConstraint = x.Constraint })];
    }

    /// <summary>目录里主键那一条索引的固定名——这一家不给它建表时写的那颗名字。</summary>
    const string PrimaryKeyName = "PRIMARY";

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "schema", table.Schema ?? string.Empty));
        command.Parameters.Add(Parameter(command, "table", table.Name));
        command.Parameters.Add(Parameter(command, "column", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var dataType = reader.GetString(0);
        var chars = CatalogLength(reader, 1) ?? -1;
        var bytes = CatalogLength(reader, 2) ?? -1;
        var collation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var charset = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

        // 第二把尺子在这一家问得出来，所以不填同一个数两遍：填出来的差（4 倍或 1 倍）本身就是字符集事实。
        // 只有问不出的型才退回"答案是没有"的写法——那与另三族的"没有第二把尺子"是两种不同的退化，必须能分辨。
        var definitionByteLength = bytes >= 0 ? bytes : chars;

        // CharSemanticsProven 的判据比另三家强：不是"没有反证"，是目录里同时躺着字符数与字节数。
        // 空串字符集 = 这一列不是文本型，那一档不"proven"而是无关。
        return new LengthSemanticsReading(collation, chars, definitionByteLength,
            charset.Length > 0 && IsText(dataType));

        static bool IsText(string type) => type is "varchar" or "char" or "tinytext" or "text" or "mediumtext" or "longtext";
    }

    /// <summary>型名从 data_type 拼出，而那一颗字段不带宽度：字符族宽度从 character_maximum_length 接回，
    /// 小数从 numeric_precision/numeric_scale，时刻族从 datetime_precision。整数族什么都不接：
    /// int(11) 是显示宽度，8.0.17 起已废弃，data_type 从来不含它（折法见 Normalize）。</summary>
    static string StoreType(string dataType, int? chars, DbDataReader reader) => dataType switch
    {
        "varchar" => chars is { } n ? $"varchar({n})" : "varchar",
        "char" => chars is { } n ? $"char({n})" : "char",
        "decimal" => reader.IsDBNull(5) ? "decimal" : $"decimal({reader.GetInt32(5)},{reader.GetInt32(6)})",
        "datetime" or "timestamp" or "time" => Timed(dataType, reader),
        _ => dataType,
    };

    /// <summary>时刻族三型共用 datetime_precision；裸写那一形读回 0（不是 NULL），
    /// 所以 datetime 与 datetime(0) 本来就是同一个型。</summary>
    static string Timed(string name, DbDataReader reader)
        => reader.IsDBNull(7) ? name : $"{name}({reader.GetInt32(7)})";

    /// <summary>标度在小数族是 numeric_scale，时刻族是 datetime_precision（契约侧同一个词）。
    /// 整数族必须按 data_type 挡：那一档 numeric_scale 交回 0 而不是 NULL（实测），
    /// 不设闸门等于把"这一列标度 0"写成库的事实，而契约侧整数没有标度格。</summary>
    static int? Scale(string dataType, DbDataReader reader) => dataType switch
    {
        "decimal" => reader.IsDBNull(6) ? null : reader.GetInt32(6),
        "datetime" or "timestamp" or "time" => reader.IsDBNull(7) ? null : reader.GetInt32(7),
        _ => null,
    };

    /// <summary>字节上限是目录里现成的那颗：CHARACTER_OCTET_LENGTH 对文本列给"字符数 × 字符集最大字节宽"，
    /// 对 binary/varbinary 与各档 BLOB 给真实字节数。数值列两颗都是 NULL → null。
    /// 装得下才交回：LONGBLOB/LONGTEXT 交回 4G-1 装不进 int?，填 null 而不是截断成假数。</summary>
    static int? MaxBytes(DbDataReader reader) => CatalogLength(reader, 4);

    /// <summary>两颗长度字段的共用量法：先按 64 位取，装得下才折成 int?，装不下交回 null。
    /// 必读纪律：DbDataReader.GetInt32 在 4294967295 那格直接抛 OverflowException（实测）——
    /// 一张有 LONGTEXT 列的表会红掉整趟 ReadTableAsync 而不是那一列缺格。null 在这一格够用：
    /// 长文本族的型名从 data_type 拼、从不带括号，结构同步的判据就是那条形名。</summary>
    static int? CatalogLength(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var wide = Convert.ToInt64(reader.GetValue(ordinal));

        return wide > int.MaxValue ? null : (int) wide;
    }

    /// <summary>形状对得上吗——比归一后的存储类型文本。归一三处不同源：发射侧大写而目录侧小写；
    /// 整数的显示宽度只在发射侧存在（不摘掉它就是"每一趟都想 MODIFY COLUMN 一次而永远配不平"）；
    /// 同义词 numeric/decimal、integer/int、real/double、bool/tinyint 折一。摘宽度只摘整数族：
    /// decimal(18,4)/datetime(3)/varchar(32) 的括号是语义。tinyint(1) 与 tinyint(4) 折成同型是已知代价
    /// （目录层分不开，真判据在 column_type），换来的是同步在布尔列上不空转。</summary>
    public bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual)
        => string.Equals(Normalize(MySqlTypes.Instance.GetStoreType(expected)), Normalize(actual.StoreType), StringComparison.Ordinal);

    static string Normalize(string storeType)
    {
        var text = storeType.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');

        if (open < 0)
            return Fold(text);

        var name = text[..open];

        if (IsIntegerWidth(name))
            return Fold(name);

        return Fold(name) + text[open..].Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>整数那一族的名字，Normalize 靠它决定"括号里那一颗是排版还是语义"。</summary>
    static bool IsIntegerWidth(string name)
        => name is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";

    /// <summary>目录与发射形之间那几颗同义词，一张表收完（data_type 交的就是 SQL 标准名，
    /// 这里收的都是"手工建表时人能写的那个名字"）。</summary>
    static string Fold(string name) => name switch
    {
        "integer" => "int",
        "bool" or "boolean" => "tinyint",
        "dec" or "numeric" => "decimal",
        "real" => "double",
        _ => name,
    };

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
