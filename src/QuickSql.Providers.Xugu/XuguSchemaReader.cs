using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Xugu;

/// <summary>从 ALL_COLUMNS 读结构。坑全在字段名与目录形状上：ALL_COLUMNS 没有 COLUMN_NAME/DATA_TYPE/OWNER/TABLE_NAME
/// （列名是 COL_NAME、型名是 TYPE_NAME），它按 TABLE_ID 挂在 ALL_TABLES 上、模式名在 ALL_SCHEMAS.SCHEMA_NAME——
/// 不 join 连表名都问不到。数值列那颗共用字段是打包的：SCALE = 精度 × 65536 + 标度。字符三型（VARCHAR/CHAR/TEXT）
/// 在目录里同存型名 CHAR，分开它们靠 VARYING 布尔与 SCALE（TEXT = CHAR + VARYING + 无宽度）。
/// 同义字：INT 建出去目录回 INTEGER（折回 INT，读侧字形要与建侧同一，否则对账假红）；TIMESTAMP(3) 回 DATETIME（不折——
/// 同义关系只有一颗样本说过话）。目录里问不出字节量纲，量纲探测是退化实现。
/// 另两颗产物里有、这里不读的字段（IS_SERIAL、COMMENTS）没有槽位承接——doctor 读不出"是不是自增"与"列注释"。</summary>
public sealed class XuguSchemaReader : ISchemaReader
{
    /// <summary>数值列共用字段的拆包基：SCALE = 精度 × 65536 + 标度（实测三颗读数逐颗对上）。
    /// 只用于 Unpack；挡的是"某一档 NUMERIC 存的是未打包标度"那件库没说过不会发生的事，
    /// 落空的后果是那一颗数值列精度读不出来——不猜。</summary>
    const int PackedBase = 65536;

    /// <summary>字段名是活库答过的：COL_NAME/TYPE_NAME（不是 COLUMN_NAME/DATA_TYPE）、VARYING 必须带引号
    /// （它是解析器关键字，不带它就分不出 VARCHAR 与 CHAR）。join 走 TABLE_ID 与 SCHEMA_ID；
    /// 模式那道闸是 s.schema_name = NVL(:sch, UPPER(USER))：给 NULL 走当前会话用户，给不存在的名字交 0 行。</summary>
    const string ColumnsSql = """
        SELECT c.col_no, c.col_name, c.type_name, c.scale, c.not_null, c."VARYING"
        FROM all_columns c, all_tables t, all_schemas s
        WHERE t.table_name = :tbl AND c.table_id = t.table_id AND s.schema_id = t.schema_id
          AND s.schema_name = NVL(:sch, UPPER(USER))
        ORDER BY c.col_no
        """;

    /// <summary>量纲探测，只读目录（不建临时表、不写数据）。CollationName 刻意给空串：这一族的目录里没有
    /// "量纲出处"那颗字段，塞一颗名字相近含义不同的东西会让 doctor 报错出处——
    /// doctor 在这一家报得出"声明 200 字符"，报不出"这一列最多 800 字节"。</summary>
    const string ProbeSql = """
        SELECT c.type_name, c.scale
        FROM all_columns c, all_tables t, all_schemas s
        WHERE t.table_name = :tbl AND c.table_id = t.table_id AND c.col_name = :col
          AND s.schema_id = t.schema_id AND s.schema_name = NVL(:sch, UPPER(USER))
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "tbl", Fold(table.Name)));
        command.Parameters.Add(Parameter(command, "sch", Fold(table.Schema)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                // 序号在第一格（COL_NO 从 0 起数，另四族的 ordinal_position 是 1 基）；列名必须是折叠过的那一份。
                // 比对按名字不按序号，所以不折成 1 基——折了才是造假。
                var name = reader.GetString(1);
                var dataType = reader.GetString(2);
                var scale = reader.GetInt32(3);
                var varying = reader.GetBoolean(5);

                columns.Add(new SchemaColumn(name, StoreType(dataType, varying, scale),
                    MaxChars(dataType, scale), MaxBytes(dataType, scale), IsNullable(reader.GetValue(4)), reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "tbl", Fold(table.Name)));
        command.Parameters.Add(Parameter(command, "sch", Fold(table.Schema)));
        command.Parameters.Add(Parameter(command, "col", Fold(columnName)));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var dataType = reader.GetString(0);
        var scale = reader.GetInt32(1);

        // 填 -1 不填 0：0 会把"问不出"写成"答案是没有"。只认"有宽度语义的型且真的写了宽度"的那颗数，其余一律 -1。
        var declared = MaxChars(dataType, scale) ?? -1;

        return new LengthSemanticsReading(string.Empty, declared, declared, IsText(dataType));
    }

    /// <summary>拆包：SCALE = 精度 × 65536 + 标度。只在 NUMERIC/DECIMAL 两支被调用——要不要打包是型名说的，
    /// 不是数的大小说的。返回 null 说的是"这颗数不像打包形，不敢拆"：精度读不出来，而不是读成一个错的。</summary>
    static (int Precision, int Scale)? Unpack(int packed)
        => packed >= PackedBase ? (packed / PackedBase, packed % PackedBase) : null;

    /// <summary>把目录的三颗字段（型名、VARYING、SCALE）合成列定义字形；纪律是还回的字形要与
    /// XuguTypes.GetStoreType 发出的同一。SCALE = -1 说的是"这一型没有声明值"——把"没有声明"读成
    /// 一个负的声明是这一格里最坏的错：不红，还给对账一条看着像答案的数。
    /// CHAR + VARYING + 无宽度回 TEXT（那是"不写长度的 VARCHAR"，TEXT 与它同源；建侧现在无宽度发 CLOB，
    /// 这一支留给历史表与外部建的表）；
    /// INTEGER 折回 INT；TIMESTAMP 不折——它与 DATETIME 的同义关系只有一颗样本说过话；
    /// 时刻两支的精度限定词要还原（TIME(3)/DATETIME(3) 建出去 SCALE=3 回来）。</summary>
    static string StoreType(string dataType, bool varying, int scale) => dataType.ToUpperInvariant() switch
    {
        // 字符三型共用一个型名，分开它们的是 VARYING 与有没有宽度。
        "CHAR" when varying && scale > 0 => $"VARCHAR({scale})",
        "CHAR" when varying => "TEXT",
        "CHAR" when scale > 0 => "CHAR(" + scale + ')',

        // 数值：拆得开就还原成 NUMERIC(p,s)，拆不开（不是打包形）就原样还型名 —— 精度读不出，不猜。
        "NUMERIC" or "DECIMAL" when Unpack(scale) is { } packed => $"{dataType}({packed.Precision},{packed.Scale})",

        // 时刻：这一支的 scale 是直读的精度（不是打包），所以 > 0 才带限定词，0 与 -1 都还裸型名。
        "TIME" or "DATETIME" when scale > 0 => $"{dataType}({scale})",

        // INTEGER→INT 那一颗同义字折在这里（与 PostgreSql 那一族把 catalog 名折回建侧名同一条纪律）。
        "INTEGER" => "INT",

        _ => dataType,
    };

    /// <summary>字符上限只对"有宽度语义"的那几型有意义（NUMERIC 的精度不是字符数）；scale 大于 0 那半道闸
    /// 挡的是 -1——"没有声明"而不是"声明了 -1"（TEXT 的宽度曾因此报成 -1）。</summary>
    static int? MaxChars(string dataType, int scale)
        => IsText(dataType) && scale > 0 ? scale : null;

    /// <summary>字节上限在这一家问不出来，一律 null——与 MySQL 那格是同一条纪律（不替库补一个换算）而不是同一件事。</summary>
    static int? MaxBytes(string dataType, int scale) => null;

    /// <summary>"有宽度语义的字符型" = CHAR（字符三型同源）、CLOB、TEXT。认 CLOB 是因为建侧超宽发的就是它，
    /// 认得它 doctor 才说得出"这一列是超宽文本"。不看 varying：它在 CLOB/BLOB 上是 False，
    /// 拿它当判据会把 BLOB 带进来——那颗布尔只用于分开 CHAR 的两副面孔。</summary>
    static bool IsText(string dataType)
        => dataType.Equals("CHAR", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("TEXT", StringComparison.OrdinalIgnoreCase);

    /// <summary>NOT_NULL 列名方向自明（为真即非空）。活库给的是 CLR Boolean——GetString 会直接抛
    /// InvalidCastException，所以拿 GetValue 把布尔、'Y'/'1' 文本与数字三形都接住，其余照旧抛：
    /// 宁可红在重读，不要红在"doctor 多报一条可空"。null 读成可空是保守方向（由库自己的约束去挡）。</summary>
    static bool IsNullable(object? notNull) => notNull switch
    {
        bool flag => !flag,
        string text => text is not ("Y" or "1"),
        null => true,
        _ => Convert.ToInt64(notNull) == 0,
    };

    /// <summary>目录里存的是折叠后的大写形，绑进去的也必须是大写形（'sysdba' 0 行、'SYSDBA' 19 行）。
    /// null 原样留 DBNull，接 SQL 里的 NVL(:sch, UPPER(USER))——与建侧"模式为空就不限定"指向同一个默认。</summary>
    static object Fold(string? identifier)
        => identifier is { Length: > 0 } name ? name.ToUpperInvariant() : DBNull.Value;

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>参数名不带前缀（带冒号会报"未绑定变量"；SQL 文本里那颗才是 :tbl）。主链路那个摘前缀的口
    /// 在驱动边界收，而目录读侧自己 CreateParameter 不走那条路——摘前缀必须在这里再写一遍，
    /// 改一处不改另一处会留下"主链路绿、doctor 红"的形状。名字也不能叫 table（关键字，解析期就驳回）。</summary>
    static DbParameter Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = value;

        return parameter;
    }
}
