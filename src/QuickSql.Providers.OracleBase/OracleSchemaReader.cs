using QuickSql.Metadata;
using System.Data.Common;

namespace QuickSql.Providers.OracleBase;

/// <summary>从 ALL_TAB_COLUMNS 读结构。这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型。
/// 两处分岔：目录里存的是折叠后的大写（OWNER/TABLE_NAME/COLUMN_NAME 三颗都是），所以绑进去的值要自己折——
/// 参数值不进 SQL 文本，EmitIdentifier 那条路够不着，漏折会读成"这张表没有列"；
/// 模式为 null 说的是"当前会话用户"：绑一颗 NULL 让 SQL 里的 NVL(:owner, USER) 接住，读侧与建侧指向同一个默认——
/// 补一个假模式名（dbo/public 那两家的写法）在这一族会得到 ORA-00942。
/// 好消息：CHAR_USED 那一列写的就是 'C'（字符）或 'B'（字节）——它说的是"这一列当初按什么声明"，
/// 是少数"列上写着"量纲的一家（VARCHAR2(200 CHAR) 的 CHAR_LENGTH 是 200 而 DATA_LENGTH 是 800）。
/// 整份读法没有在一台活库上跑过，定位是按厂商文档写出的只读探测。</summary>
public sealed class OracleSchemaReader : ISchemaReader
{
    const string ColumnsSql = """
        SELECT c.column_id, c.column_name, c.data_type, c.char_length, c.data_length, c.char_used, c.data_precision, c.data_scale, c.nullable
        FROM all_tab_columns c
        WHERE c.owner = NVL(:owner, USER) AND c.table_name = :table
        ORDER BY c.column_id
        """;

    /// <summary>量纲探测，只读目录视图与会话参数：不建临时表、不写数据（生产库常禁 DDL 权限）。
    /// 尾巴那颗子查询取的是 NLS_LENGTH_SEMANTICS，填进 CollationName 那一格是有意的错位：
    /// 这一族决定"裸 VARCHAR2(n) 里的 n 是什么"的不是排序规则而是这一颗会话参数，
    /// doctor 要报的是"这一列的量纲从哪来"，所以这里带的是出处而不是一个同名不同义的字。</summary>
    const string ProbeSql = """
        SELECT c.data_type, c.char_length, c.data_length, NVL(c.char_used, '?'),
               (SELECT NVL(p.value, '?') FROM nls_session_parameters p WHERE p.parameter = 'NLS_LENGTH_SEMANTICS')
        FROM all_tab_columns c
        WHERE c.owner = NVL(:owner, USER) AND c.table_name = :table AND c.column_name = :column
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "owner", Fold(table.Schema)));
        command.Parameters.Add(Parameter(command, "table", Fold(table.Name)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                // 序号在前（与 T-SQL 那一份同一个列序习惯），列名在第二格 —— 而它必须是折叠过的那一份：
                // 契约侧比对的是 C# 成员映射出的列名，读回来一个 "name" 就对不上。
                var name = reader.GetString(1);
                var dataType = reader.GetString(2);
                var chars = reader.GetInt32(3);
                var bytes = reader.GetInt32(4);

                columns.Add(new SchemaColumn(name, StoreType(dataType, chars, bytes, reader),
                    MaxChars(dataType, chars), MaxBytes(dataType, bytes), reader.GetString(8) is "Y", reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "owner", Fold(table.Schema)));
        command.Parameters.Add(Parameter(command, "table", Fold(table.Name)));
        command.Parameters.Add(Parameter(command, "column", Fold(columnName)));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var dataType = reader.GetString(0);
        var chars = reader.GetInt32(1);
        var bytes = reader.GetInt32(2);
        var used = reader.GetString(3);

        // 'C' 是字符、'B' 是字节、'?' 是这一列压根没有长度语义（NUMBER/DATE）。
        // 这一族的 DATA_LENGTH 真的是字节，填出来的两个数是两把尺子，"声明 200 字符 / 800 字节"在这一家有意义。
        return new LengthSemanticsReading(reader.GetString(4), chars, bytes, used is "C");
    }

    static string StoreType(string dataType, int chars, int bytes, DbDataReader reader) => dataType.ToUpperInvariant() switch
    {
        // 量纲限定词一起还原：读回来的类型名要能与建出去的列定义逐字对上，doctor 的比对才是同一份字形。
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" when chars > 0 && chars != bytes => $"{dataType}({chars} CHAR)",
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" when chars > 0 => $"{dataType}({chars})",
        "NUMBER" => reader.IsDBNull(6)
            ? dataType
            : $"NUMBER({reader.GetInt32(6)},{reader.GetInt32(7)})",

        // TIMESTAMP(3) 的 3 在 DATA_SCALE 里；DATE 那一型在这一族本来就带时分秒，
        // 还原时不给它加限定词，留给类型表按名字认。
        "TIMESTAMP" or "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE"
            => reader.IsDBNull(7) ? dataType : $"{dataType}({reader.GetInt32(7)})",
        _ => dataType,
    };

    /// <summary>字符上限只对"有长度语义"的那几型有意义；NUMBER 的 precision 不是字符数，
    /// 带进去会让 FitsBytes 那类判据读出一个假上限。</summary>
    static int? MaxChars(string dataType, int chars)
        => chars > 0 && IsText(dataType) ? chars : null;

    static int? MaxBytes(string dataType, int bytes)
        => bytes > 0 && IsText(dataType) || dataType.Equals("BLOB", StringComparison.OrdinalIgnoreCase) || dataType.Equals("RAW", StringComparison.OrdinalIgnoreCase)
            ? bytes
            : null;

    static bool IsText(string dataType)
        => dataType.Equals("VARCHAR2", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NVARCHAR2", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CHAR", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NCHAR", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NCLOB", StringComparison.OrdinalIgnoreCase);

    /// <summary>目录里的名字是大写形，所以绑进去的也必须是大写形。
    /// null 原样留 null：OracleCodeFirst 那句"模式为空就不限定"在这里对应 NVL(:owner, USER)。</summary>
    static object? Fold(string? identifier)
        => identifier is { Length: > 0 } name ? name.ToUpperInvariant() : DBNull.Value;

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>参数名前缀是冒号而不是 @（ParameterPrefix 在目录查询这一侧的同一件事）：
    /// 这一族的驱动认 :name，带 @ 的写法会被当成一个名字里含 @ 的绑定变量。</summary>
    static DbParameter Parameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = ':' + name;
        parameter.Value = value ?? DBNull.Value;

        return parameter;
    }
}
