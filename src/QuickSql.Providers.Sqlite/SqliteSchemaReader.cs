using System.Data.Common;
using QuickSql.Metadata;

namespace QuickSql.Providers.Sqlite;

/// <summary>从 pragma_table_info 这一张表值函数读结构，只有 ADO.NET 抽象层的虚调用。
/// 最重要的一条：本家的目录不认标识符、认文本——表名是函数参数（走引擎的标识符解析、大小写不敏感），
/// 行集里的 "name" 是普通文本列、等号比字面，ProbeSql 那颗 COLLATE NOCASE 正是把列名的比法
/// 搬到与表名同一档。其余要点见各成员："表不存在"与"这表没有列"是同一份读数（函数交回空集
/// 不报错，而空表那支被库的语法关掉）；名字里装点的表目录侧会安静地交回 0 行而数据侧是响的；
/// temp 库的同名表遮住 main 的表且目录与数据都被遮——"建出来的表与查的表必须是同一张"靠的是
/// 读侧与写侧用同一颗 TableInfo.Schema，未点模式时发裸名（绑 DBNull）。四颗读不出的字段各有后果：
/// 宽度只活在 type 文本里（DeclaredChars 从文本读出那颗数）；排序规则不在目录字段里
/// （CollationName 恒空串，列上声明 NOCASE 读侧看不见）；自增读不出；生成列刻意不读；
/// 声明宽度不参与存储——CharSemanticsProven 取 false，两颗长度填同一个数（我们自己写进 DDL 的自述）。</summary>
public sealed class SqliteSchemaReader : ISchemaReader
{
    /// <summary>列表那一格。一颗绑值两种语义：@tbl 进函数参数（标识符解析，大小写不敏感），
    /// @sch 绑 DBNull 时等于"不点"。ORDER BY "cid" 那颗不可换成 "pk"：复合键上两序不同解，
    /// 按 pk 排会把契约的列序读乱。这里没有 tabtype 那道闸：同一库里表与视图共用一个名字空间，
    /// "同名视图混进读数"这个形状构造不出来。</summary>
    const string ColumnsSql = """
        SELECT "cid", "name", "type", "notnull"
        FROM pragma_table_info(@tbl, @sch)
        ORDER BY "cid"
        """;

    /// <summary>量纲探测那一格：只取 type 一颗（本家的宽度只活在文本里）。
    /// COLLATE NOCASE 是本文件唯一一颗"改比较规则"的加法：没有它，契约里写 name 而库声明 NAME
    /// 的一颗列会读成"问不出"；有了它，非 ASCII 的名字仍然读成"问不出"（NOCASE 只折 ASCII）。
    /// 与 ColumnsSql 同一颗表名解析，所以两处都吃同一份 (@tbl, @sch) 绑值。</summary>
    const string ProbeSql = """
        SELECT "type"
        FROM pragma_table_info(@tbl, @sch)
        WHERE "name" = @col COLLATE NOCASE
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        // 空串当"未点"处理，不绑进去：空串点的是一颗存在的问句，pragma_table_info('t', '')
        // 报 unknown database ''，它不静默。
        var schema = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "@tbl", table.Name));
        command.Parameters.Add(Parameter(command, "@sch", schema));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(1);
                var declaredType = reader.GetString(2);

                // 两颗数字都走 GetInt32（本驱动在 Int64 列上接受它）。
                // notnull 按"那位为 0 才可空"读，但要知道库报的是声明形状而不是强制形状：
                // INTEGER PRIMARY KEY 不带 NOT NULL 时这里给 0，同一约束在 WITHOUT ROWID 表上给 1，
                // 两形都原样带出去、一折不折。序号取 cid 而不取 pk。
                columns.Add(new SchemaColumn(name, declaredType, DeclaredChars(declaredType), MaxBytes(declaredType),
                    reader.GetInt32(3) == 0, reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        var schema = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "@tbl", table.Name));
        command.Parameters.Add(Parameter(command, "@sch", schema));
        command.Parameters.Add(Parameter(command, "@col", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        // 三格各自的读法（本家与 GBase 8s 落在同一档而不同因）：
        //   CollationName 给空串——这一列的排序规则不在目录的字段里。
        //   两颗长度填同一个数，而它是我们从 DDL 里读回来的自述，不是库的容量。
        //   CharSemanticsProven 取 false：没有任何一环用这颗数，所以正反都无从观察。
        var declared = DeclaredChars(reader.GetString(0)) ?? -1;

        return new LengthSemanticsReading(string.Empty, declared, declared, false);
    }

    /// <summary>这一列有没有存量空值——结构同步把一列收紧成 NOT NULL 前问的那一句。
    /// 这一句是数据侧的问句，不是目录侧的：本家的目录答得出"这一列声明成什么"，
    /// 永远答不出"存量行里有没有 NULL"。表名与列名走 IdentifierPolicy.Sqlite 那一份字形
    /// （恒加双引号、内部引号翻倍），与建表、查询共用同一颗 Quote，"探的哪一列"与
    /// "要 ALTER 的哪一列"不可能是两个名字。限定形与发射侧同一格：契约没点模式就发裸名，
    /// 点了就带 库.——读侧与写侧用同一颗 Schema，本家的 TEMP 遮蔽才不构成错位。
    /// LIMIT 1 命中第一颗空值就收，不把脏表整个数一遍。</summary>
    public async Task<bool?> ColumnHasNullsAsync(
        DbConnection connection,
        TableInfo table,
        string columnName,
        CancellationToken ct = default)
    {
        var target = table.Schema is { Length: > 0 } named
            ? $"{IdentifierPolicy.Sqlite.Quote(named)}.{IdentifierPolicy.Sqlite.Quote(table.Name)}"
            : IdentifierPolicy.Sqlite.Quote(table.Name);

        var sql = $"SELECT 1 FROM {target} WHERE {IdentifierPolicy.Sqlite.Quote(columnName)} IS NULL LIMIT 1";

        using var command = Command(connection, sql);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>声明宽度：从型名文本里读出来的一颗数，七家里只有本家这么办。
    /// 判据两处：基名含 CHAR（用 CHAR 收尾判会把 CHARACTER VARYING(50) 认进来而把 NUMERIC(10,2)
    /// 挡在外面），以及括号里恰好是一颗整数（两颗数的那一形是精度与标度，不是宽度）。
    /// 读不出就交回 null，不猜（型名是空串时走的也是同一条出口）。
    /// 与 ProbeLengthSemanticsAsync 共用本方法，两处那颗数的读法不可能分岔。</summary>
    static int? DeclaredChars(string declaredType)
    {
        if (!BaseTypeName(declaredType).Contains("CHAR", StringComparison.Ordinal))
            return null;

        var open = declaredType.IndexOf('(');
        var close = open < 0 ? -1 : declaredType.IndexOf(')', open + 1);

        if (close < 0)
            return null;

        return int.TryParse(declaredType.AsSpan(open + 1, close - open - 1), out var chars) && chars > 0
            ? chars
            : null;
    }

    /// <summary>字节上限在这一家恒 null：目录里没有第二个数，而声明的那一颗也不参与存储。
    /// 与 Gbase8sSchemaReader.MaxChars 那颗恒 null 是同一颗纪律的两面——那一家问得出字节
    /// 而问不出字符，本家两问都问不出，两侧都不替库补一个换算。
    /// 签名与 DeclaredChars 逐颗对齐，让"两颗上限出自同一份读法"在签名上看得出来。</summary>
    static int? MaxBytes(string declaredType) => null;

    /// <summary>型名的基名：切在第一个空格或括号之前，折成大写。折大写只服务本文件的判断，
    /// 不作用于交回的读数——StoreType 是目录原样给的那串。为什么必须切：声明形带括号
    /// （VARCHAR(4)）也可能带空格（CHARACTER VARYING(50)），不切则第二形永不相认。
    /// 为什么必须折：Contains 走 Ordinal 而声明侧的大小写由建表的人决定，读侧要自己折一次。</summary>
    static string BaseTypeName(string declaredType)
    {
        var cut = declaredType.IndexOfAny(['(', ' ']);

        return (cut < 0 ? declaredType : declaredType[..cut]).Trim().ToUpperInvariant();
    }

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>名字要给，而且带前缀给：本驱动对没有名字的参数是直接拒的。
    /// 本文件的参数名必须与 SQL 文本里那三颗一一对上（@tbl ∶ @sch ∶ @col）——
    /// 这一处写错是响的（具名形绑错名字会报缺参数），位置形只会把值放错位（静的）。</summary>
    static DbParameter Parameter(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = (object?)value ?? DBNull.Value;

        return parameter;
    }
}
