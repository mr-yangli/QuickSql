using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Gbase8s;

/// <summary>从 systables/syscolumns/systypes 三张系统表读结构。它是这一族唯一"今天连一次都没有被走到"的
/// 代码：没有黄金样本、没有离线用例、也没有活库，每条 SQL 的字段名与 join 形状都只到手册档。
/// 位置形占位符让三件事第一次成为契约：绑参数的顺序就是语义（颠倒不报错，只会把表名绑到所有者位、
/// 交 0 行被读成"表不存在"）；"未点所有者"不能用 COALESCE(?, CURRENT USER) 表达（位置形 ? 没有类型
/// 推不出型），所以发两条文本、默认支写进 SQL；折叠方向是小写（六家第一份），策略那颗布尔表达不了
/// "折小写"，只能在读侧手写。
/// 模式在本家不是命名空间而是所有者（tabname 只在所有者之内唯一，不加 owner 闸会把同名表并成一份）。
/// 两颗读不出的字段：DECIMAL 的标度（width 那颗字段对"精度还是标度"的指代没读定，不选它——
/// DECIMAL(10,2) 读回 DECIMAL(10)，doctor 会报一条读法造成的真差，宁缺不错）；自增
/// （SERIAL 在目录里读回 INTEGER 加一颗 DEFAULT，SchemaColumn 没有默认值槽位）。</summary>
public sealed class Gbase8sSchemaReader : ISchemaReader
{
    /// <summary>"可空"编码在 coltype 的高位：coltype = 基本型号 + 256 × 可空 + 512 × 有默认。
    /// 判可空只看那颗位，不看整颗数的高字节奇偶——"还有一位装别的东西"时会读错，而这里没读到"没有别的位"。
    /// join 用的 MOD(coltype, 256) 就是 BaseTypeMask 的 SQL 形（一处 C#、一处文本，抽不出公共表达式）。</summary>
    const int NullableBit = 256;

    const int BaseTypeMask = 255;

    /// <summary>两颗参数的顺序就是语义：第一位表名、第二位所有者，各只出现一次（位置形没有"复用参数"那一手）。
    /// tabtype = 'T' 那道闸是本家特有：目录里表、视图、系统表共用 systables 一张，
    /// 不筛的话同名视图会进同一份读数，doctor 拿它比契约会报"这一列不存在"。</summary>
    const string ColumnsSql = """
        SELECT c.colno, c.colname, ty.name, c.coltype, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = ? AND t.tabtype = 'T'
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        ORDER BY c.colno
        """;

    /// <summary>未点所有者那一支：owner = CURRENT USER 写进 SQL 而不是绑 NULL（COALESCE 那条路在位置形里
    /// 推不出型）。与 ColumnsSql 只差谓词形状，两条形必须同读同改。</summary>
    const string ColumnsSqlForCurrentUser = """
        SELECT c.colno, c.colname, ty.name, c.coltype, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = CURRENT USER AND t.tabtype = 'T'
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        ORDER BY c.colno
        """;

    /// <summary>量纲探测，只读目录（不建临时表、不写数据）。两颗形各有自己的颗数（表名→〔所有者〕→列名），
    /// 调用点必须按选了哪条文决定绑几颗——这里把列名绑到所有者位上没人挡
    /// （列表那条绑多了还会报"参数过多"）。</summary>
    const string ProbeSql = """
        SELECT ty.name, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = ? AND c.colname = ?
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        """;

    /// <summary>探测的默认所有者支，与 ColumnsSqlForCurrentUser 同一对、同一理由。</summary>
    const string ProbeSqlForCurrentUser = """
        SELECT ty.name, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = CURRENT USER AND c.colname = ?
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        // "用哪条文"与"绑几颗"由同一个答案决定：先落可空的 owner（Fold 不接受 null，
        // 这是六份 Fold 里唯一要调用点自证的）。
        var owner = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, owner is null ? ColumnsSqlForCurrentUser : ColumnsSql);

        // 顺序即语义：① 表名 ② 所有者（只有点名那一支才有这一颗）。
        command.Parameters.Add(Parameter(command, Fold(table.Name)));

        if (owner is not null)
            command.Parameters.Add(Parameter(command, Fold(owner)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(1);
                var storeType = reader.GetString(2);
                // 强类型取数口（GetInt32）：coltype 与 width 在目录定义里都是 INT；
                // 真库若交回 Int16，红的应是这一句而不是下游判据。
                var colType = reader.GetInt32(3);
                var width = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);

                // 两颗上限的方向与五族相反：目录那颗数的单位是字节（恒 Byte 量纲），
                // 所以填 MaxBytes 而 MaxChars 留 null。"是不是自增"读不出（类注释末段）。
                columns.Add(new SchemaColumn(name, StoreType(storeType, width),
                    MaxChars(storeType, width), width, IsNullable(colType), ReadOrdinal(reader)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        var owner = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, owner is null ? ProbeSqlForCurrentUser : ProbeSql);

        // 三颗的顺序即语义：① 表名 ② 所有者（可空）③ 列名。
        command.Parameters.Add(Parameter(command, Fold(table.Name)));

        if (owner is not null)
            command.Parameters.Add(Parameter(command, Fold(owner)));

        command.Parameters.Add(Parameter(command, Fold(columnName)));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var storeType = reader.GetString(0);
        var width = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);

        // 只认"有宽度语义的型且目录真给了数"的才报长度，其余 -1（本家撞上的会是 DECIMAL 与
        // DATETIME/INTERVAL 两族——它们的全名里带字段范围）。这一道闸不是抄来的形式。
        // CharSemanticsProven 取 false：本家填的是"字节数、第一个数压根不存在"，
        // 与金仓/虚谷"字符数、第二个数问不出"同码异义——不是"没证实"，是"证实的是反方向"。
        var declared = IsText(storeType) && width > 0 ? width : -1;

        return new LengthSemanticsReading(string.Empty, declared, declared, false);
    }

    /// <summary>把目录两颗（型名、width）合成列定义字形；纪律是还回的字形要与 Gbase8sTypes.GetStoreType
    /// 发出的同一，否则 doctor 里每颗字符列都是假红。字符两支还原宽度（这一家是字节，建侧写的也是字节，
    /// 两侧同量纲）；定点只还原位数（标度不读，也不猜 0）；大对象与其余型原样还。
    /// SERIAL 读不回 SERIAL（自增在默认值上），不折——折了才是造假；INT/INT8 同义字也不折
    /// （建侧发的就是长形，还回来一个短形才是假红的来源）。</summary>
    static string StoreType(string storeType, int? width)
        => BaseTypeName(storeType) switch
        {
            "CHAR" when width > 0 => $"CHAR({width})",
            "VARCHAR" when width > 0 => $"VARCHAR({width})",
            "DECIMAL" when width > 0 => $"DECIMAL({width})",

            // 没有宽度语义的型，目录里那颗 width 可能是 0、可能压根不给：两种都不还原。
            _ => storeType,
        };

    /// <summary>字符上限一律 null：目录那颗数的单位是字节，抄进字符格会把"契约 200 字符、目录 800"
    /// 读成一条宽度差——与另两族恒 null 的 MaxBytes 是同一纪律的镜像（问不出第一个数所以留空）。
    /// 换算只在建侧发生一次，那是我们自己的声明而不是库的读数。</summary>
    static int? MaxChars(string storeType, int? width) => null;

    /// <summary>可空性只看 coltype 那颗位。读反的后果是可指认的（可空列报成非空，doctor 报"契约写得比库宽"），
    /// 不像"表不存在"那样静默。这一颗位没逐页回读，拿到库时第一位就问它。</summary>
    static bool IsNullable(int colType) => (colType & NullableBit) != 0;

    /// <summary>colno 是库给的列序，原样带出不进判据——0 基还是 1 基没读定，折了才是造假。</summary>
    static int? ReadOrdinal(DbDataReader reader) => reader.IsDBNull(0) ? null : reader.GetInt32(0);

    /// <summary>"有宽度语义的字符型" = CHAR/VARCHAR/LVARCHAR，不含 CLOB/BLOB：本家大对象没有可报的
    /// 声明宽度（CLOB 上的 width 是库的块上限），报出来是一条看着像答案的假数。与守卫那侧"可不可比"
    /// 是两判（LVARCHAR 可比也可量、TEXT 可比不可量），所以两处不共用一份名单。</summary>
    static bool IsText(string storeType)
        => BaseTypeName(storeType) is "CHAR" or "VARCHAR" or "LVARCHAR";

    /// <summary>型名基名，切在第一个空格或括号之前：DATETIME 与 INTERVAL 两族的目录名带字段范围全名，
    /// 不切的话它们在 StoreType 的 switch 里永不相认。与守卫的 BaseType 同判据同写法，两处各写一遍
    /// （下游不同——这里认型族、那里比大对象名——抽出来只会把两颗不同的判据并成一颗看起来通用的）。</summary>
    static string BaseTypeName(string storeType)
    {
        var cut = storeType.IndexOfAny(['(', ' ']);

        return (cut < 0 ? storeType : storeType[..cut]).Trim().ToUpperInvariant();
    }

    /// <summary>目录里的名字是小写形，绑进去的也必须是那一形——六家第一次方向朝下。本家唯一
    /// "加引号与不加引号在目录侧同解"（库里存小写、未加引号的写法折出来也是小写）。
    /// Schema 为 null 那一支在这里不处理：本方法是六份 Fold 里唯一不接受 null 的，
    /// 调用点必须先看过 table.Schema——那两道 if (hasOwner) 不是防御，是本颗签名的要求。</summary>
    static string Fold(string identifier) => identifier.ToLowerInvariant();

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>名字不给：ParameterName 留在驱动默认值上，文本里那一颗是裸问号——两侧都没有名字，
    /// 比"文本带前缀而对象名不带"那一手再退一格。</summary>
    static DbParameter Parameter(DbCommand command, string value)
    {
        var parameter = command.CreateParameter();

        parameter.Value = value;

        return parameter;
    }
}
