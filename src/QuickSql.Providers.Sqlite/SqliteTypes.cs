using System.Collections.Frozen;
using QuickSql.Metadata;

namespace QuickSql.Providers.Sqlite;

/// <summary>CLR ↔ SQLite 类型名。最重要的一条：这里的声明型名不是存储形状，是一颗 affinity 配方——
/// 存储只有 TEXT/NUMERIC/REAL/INTEGER/BLOB/无 affinity 六档，由声明名里的子串决定，而宽度与标度
/// 从不参与（VARCHAR(4) 装得下 20 个字符，DECIMAL(18,4) 里插一句文本会静默变成 REAL），所以发出的
/// 型名要按它触发的 affinity 选，而不是按它看起来像什么。七处分岔见各成员注释：Unicode 恒真
/// （驱动写 UTF-8、N'' 是语法错，这一族压根没有窄字符型可声明）；布尔落 INTEGER（库里没有布尔这一型，
/// 声明 BOOLEAN 只是换一串回显的字）；decimal 落 TEXT（定点没有无损的落处——两条写入路都会抹标度，
/// 绑不变形文本保的是我们自己的形状；代价是钱这一列在库里不能比：排序走词法、聚合过滤各走各的尺，
/// 缺口只在比较与排序、不在求值）；ulong 落 TEXT（驱动会把 64 位无符号塞进 long，NUMERIC 那一形
/// 又会被 affinity 改写成 REAL）；时刻四型一律 TEXT（DateTimeOffset 的偏移原样进原样出；驱动写的
/// ISO 文本可变宽而词法序不翻——同一列混带偏移与不带偏移两形时文本序不可信）；TimeSpan/TimeOnly
/// 落 INTEGER 走 Ticks（跨度要能被减）；浮点只有 REAL 一档（float 进去无损、出来是 double）。
/// 自增不在本文件：AUTOINCREMENT 必须挂在 INTEGER PRIMARY KEY 内联主键上、表级 PRIMARY KEY
/// 接不住，所以 IsIdentity 在本家换的是整条键的写法，见 SqliteCodeFirst。</summary>
public sealed class SqliteTypes : ITypeMap
{
    public static SqliteTypes Instance { get; } = new();

    /// <summary>读侧的型名表。它登记的键有两族来源：我们自己发出去的那几颗，以及"别人建的列会回显的
    /// 字形"——affinity 让声明名与实际存储解耦，一张老表可能写着任何一形。整数一族按声明名给窄形
    /// 是给 doctor 对账用的（本家的存储只有一颗 64 位整数，声明名窄而存储宽是这一族的常态）；
    /// boolean/bool/bit 三行认而不发——认它是读别人的表，不发它是自己的表。这张表今天在仓库里
    /// 零调用，是给 doctor 预备的读侧知识而不是现状。</summary>
    static readonly FrozenDictionary<string, Type> StoreToClr = new Dictionary<string, Type>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // 字符与文本：本家自己发的三形（TEXT / VARCHAR / CHAR）加上大对象那一形。
        ["text"] = typeof(string),
        ["clob"] = typeof(string),
        ["character"] = typeof(string),
        ["char"] = typeof(string),
        ["varchar"] = typeof(string),
        ["nvarchar"] = typeof(string),
        ["string"] = typeof(string),

        // 整数一族（含各种窄声明）。
        ["integer"] = typeof(int),
        ["int"] = typeof(int),
        ["tinyint"] = typeof(sbyte),
        ["smallint"] = typeof(short),
        ["mediumint"] = typeof(int),
        ["bigint"] = typeof(long),
        ["int64"] = typeof(long),
        ["unsigned big int"] = typeof(long),

        // 布尔三形，见类注释里"认而不发"那一句。
        ["boolean"] = typeof(bool),
        ["bool"] = typeof(bool),
        ["bit"] = typeof(bool),

        // 浮点一档，定点那一族的声明名也在这里（它们在库里其实是 affinity 配方）。
        ["real"] = typeof(double),
        ["double"] = typeof(double),
        ["double precision"] = typeof(double),
        ["float"] = typeof(double),
        ["numeric"] = typeof(decimal),
        ["decimal"] = typeof(decimal),

        ["blob"] = typeof(byte[]),

        // 时刻四形。注意它们与 GetStoreType 的对应是错位的：本家发的是
        // TEXT 与 INTEGER（见 GetStoreType），这三行认的是别人建的表。
        // 所以这一族读回的 DateTime 列在库里其实是什么形状，本表说不出——那是 affinity 那一格的后果。
        ["date"] = typeof(DateOnly),
        ["datetime"] = typeof(DateTime),
        ["datetimev2"] = typeof(DateTimeOffset),
        ["time"] = typeof(TimeOnly),
        ["timestamp"] = typeof(DateTime),

        // 两颗方言侧的惯用名：json 在这一族的生态里是文本加一道 CHECK【文档】，而本仓库
        // 没有任何一格问"这一列是不是 json"，所以认成 string 与 PostgreSqlTypes 同判。
        ["json"] = typeof(string),
        ["jsonb"] = typeof(string),
        ["uuid"] = typeof(Guid),
        ["guid"] = typeof(Guid),
    }.ToFrozenDictionary();

    /// <summary>UTF-8 下一颗字符最多 4 字节（代理对）。这一颗在本家是纯尺子、不进任何文本：
    /// 它只喂给 FitsBytes，因为这一族的宽度从不参与存储。取 4 与 PG 血统同判：
    /// 库里没有字节上限那一扇闸门，要一把永不误拒的宽松尺子，而不是一把看起来精确的。</summary>
    const int Utf8WorstCaseBytesPerChar = 4;

    /// <summary>CLR 型 → 声明字形；人已点名的那一格原样 echo。本族这一格多一句诚实的话：
    /// echo 出去的那串字在这一家不改变存储，只改变 affinity 配方与被点名时的那次比对——
    /// [Column(TypeName="DECIMAL(18,4)")] 能得到"目录回显这一串"而得不到"定点存储"。
    /// 与 IsIdentity 无关（共用层先定型后判自增），而本家的自增换的是键的写法，见 SqliteCodeFirst。</summary>
    public string GetStoreType(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (column.StoreTypeName is { Length: > 0 } declared)
            return declared;

        if (clrType == typeof(string))
            return String(column);

        if (clrType == typeof(byte[]))
            return "BLOB";

        // 枚举存整数，七族同一决定。这一家多一层：整数是这一族最"实"的一档（INTEGER affinity，
        // 存储就是 64 位整数），CLR 枚举底层是 long 也接得住。
        if (clrType.IsEnum)
            return "INTEGER";

        return clrType switch
        {
            // 类注释第二条：库里没有布尔这一型，声明 BOOLEAN 只是换一串回显的字。
            var t when t == typeof(bool) => "INTEGER",

            var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) => "SMALLINT",
            var t when t == typeof(ushort) || t == typeof(int) => "INTEGER",
            var t when t == typeof(uint) || t == typeof(long) => "BIGINT",

            // 无符号 64 位落文本而不是 NUMERIC(20,0)：驱动侧会绕成负数，而 NUMERIC 那一形触发的
            // affinity 会把大整数改写成 REAL。
            var t when t == typeof(ulong) => "TEXT",

            // 只有一档浮点，所以 CLR float 与 double 同落 REAL：float 进去无损，出来是 double
            // 而由具体化侧窄化——本家压根没有窄浮点可照抄（FLOAT 与 DOUBLE 触发同一颗 affinity）。
            var t when t == typeof(float) || t == typeof(double) => "REAL",

            // 定点落文本：这一族没有无损的定点落处，而 Precision/Scale 两颗在这里不参与文本——
            // 它们留在契约里，被绑定侧的"按标度补零"用掉一次。本家是七家里第一颗
            // "精度与标度是纯应用侧事实"的家。
            var t when t == typeof(decimal) => "TEXT",

            // 时刻三型 + 带偏移那一型一律 TEXT：驱动自己写的就是 ISO 文本，可变宽的小数位
            // 保住 100 纳秒。声明成 DATETIME 不会换存储，只会换一整套行为。
            var t when t == typeof(DateTime) => "TEXT",
            var t when t == typeof(DateTimeOffset) => "TEXT",
            var t when t == typeof(DateOnly) => "TEXT",

            // TimeOnly 在这里是 INTEGER 而时刻那一族是 TEXT：本文件唯一一处"两个 CLR 时刻型故意
            // 不同形"，理由是绑定侧两型都走 Ticks，而在 INTEGER 上"比大小"与"加减"都不问库。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => "INTEGER",

            // Guid 落 TEXT 而不落 CHAR(36)：这一家的 CHAR(n) 不会带来任何检查（宽度从不强制），
            // 写 36 只会得到一句看起来像约束的注释。
            var t when t == typeof(Guid) => "TEXT",
            var t when t == typeof(char) => "TEXT",

            // 本家的兜底话术与另六家不同一条：这里未登记的型多半也能建（affinity 认任何字形），
            // 报的不是"这一族没有那一型"而是"本仓库不知道往哪一臂绑值"。
            _ => throw new QuickSqlException(
                $"SQLite 未登记 CLR 类型 {clrType.Name} 的存储类型：这一族没有"
                    + "「库里不存在这一型」那种缺（声明任何字形都会被接受），所以缺的是本仓库的绑定臂。"
                    + "请显式给 [Column(TypeName=...)]，并注意声明名在这家决定的是 affinity 而不是存储。"
            ),
        };
    }

    /// <summary>string → 有宽度声明时给 VARCHAR(n)，无宽度时给 TEXT。
    /// n 在这一家是注释而不是约束，本方法发出它的唯一理由是对账可读：目录原样回显声明字形，
    /// doctor 能把"契约说 200 字符"与"库声明 200"对上——另六家这一格对的是库的容量，
    /// 本家对的是库的自述。它同时是一颗风险：建侧的宽度不会替你挡超长值——挡它的是
    /// FitsChars 那道闸，而那道闸只在本仓库负责写入的路径上生效，换一条手写 SQL 就没有闸门了。</summary>
    static string String(ColumnContract column) =>
        column.MaxChars is { } chars ? $"VARCHAR({chars})" : "TEXT";

    public ColumnContract ApplyStorageDefaults(ColumnContract column)
    {
        var clrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (clrType != typeof(string) && clrType != typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? GetStoreType(column) };

        if (clrType == typeof(byte[]))
            return column with { StoreTypeName = column.StoreTypeName ?? "BLOB" };

        // 逐格（这一支是本文件与另六家读法差异最大的一处，因为两颗位在这里都不改变库的行为）：
        //   Unicode = true——驱动写 UTF-8、而 N'' 是语法错；与 GBase 8s 那颗假值相反一侧。
        //   LengthSemantics = Char——实测 length() 数的是字符而不是字节，本家是"库里压根没有
        //     字节尺"的结论（PG 血统同档而不同来路：那一家是 codeset 的结论）。
        //   MaxBytes = 字符数 × 4——一把宽松尺子，不是库里存在的约束；它不参与 DDL 文本。
        return column with
        {
            Unicode = true,
            LengthSemantics = LengthSemantics.Char,
            MaxBytes = column.MaxChars is { } chars ? chars * Utf8WorstCaseBytesPerChar : null,
            StoreTypeName = column.StoreTypeName ?? GetStoreType(column),
        };
    }

    public bool TryGetClrType(string storeTypeName, out Type? clrType) =>
        StoreToClr.TryGetValue(storeTypeName, out clrType);

    /// <summary>恒 Char，与 PG 血统那两家同值。差别在这一家连"第二个数"都不存在：
    /// n 是字符而它从不被强制，所以取 Char 不是"问到了字符那一把"，而是"契约里那一格
    /// 说的只能是字符"——两把尺都不在库里时唯一诚实的一档。</summary>
    public LengthSemantics GetLengthSemantics(ColumnContract column) => LengthSemantics.Char;
}
