using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的建表与改表（整层厂商文档口径，本机没有引擎）。分岔逐条：
/// 契约索引整格拒——本家没有用户索引那一档（查询面全靠引擎自建的数据骨架），IndexContract 是
/// "表建成之后另有对象"的 OLTP 形状，进不去；主键不发任何子句——本家的主键是
/// "首列 TIMESTAMP 系统自动设为主键"的隐含事实（VendorDoc·TDengine：建表页原话），
/// 所以建表前先把契约的键形状校验成与本家隐含事实同形（恰一颗、是首颗数据列、型名 TIMESTAMP；
/// 复合键没有落点），校验不过当场红而不是发一张"键语义对不上"的表；
/// 列定义只有 "名" 型名一段——官方 column_definition 文法里没有 NULL/NOT NULL、没有 DEFAULT、
/// 没有列注释，可空性与默认值在 DDL 上没有落点（有 DefaultValue 的契约在发射点拒收）；
/// identity 列拒收（本家没有"由库递增的列"那一档，主键值由调用方自己给）；
/// rowversion 在发射点拒收——发出去是一句库不认识的型名。改列走 MODIFY COLUMN 完整列定义
///（VendorDoc·TDengine：改可变长类型长度的官方形）；表注释走 ALTER TABLE … COMMENT '注'
///（置值形，幂等）；列注释没有这一格（TryEmitColumnComment 恒 false）。
/// 稳定表分岔（v0.39 第六笔）：契约带 <see cref="EntityContract.Stable"/> 的实体发
/// CREATE STABLE … TAGS (…)（VendorDoc·TDengine：超级表页——TAGS 子句在列定义之后、表选项之前），
/// 首列 TIMESTAMP 的校验对象跟着换成"首颗数据列"（TAG 可声明在前）；TAG 列的类型门禁
///（DECIMAL 不可作 TAG、变长 TAG 上限 16 382 字节【VendorDoc·TDengine：数据类型页】）在发射点判；
/// 稳定表与分表声明不并用（两套"一个实体对多张表"，子表命名走 Subtable 模板那一档）。
/// 补差那一档（ADD COLUMN/MODIFY COLUMN/表注释）发的是 ALTER TABLE 文本——对稳定表
/// 服务端会拒（那一句要的是 ALTER STABLE，SPI 只给表名不给契约，本层拿不到稳定这一格），
/// 与接口注释里"没接这一颗的家以库的拒绝收尾"同一档，记 notes。</summary>
public sealed class TDengineCodeFirst(TDengineTypes types, TDengineValues values) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.TDengine;

    /// <summary>变长 TAG 的库侧字节上限（VendorDoc·TDengine：数据类型页——BINARY/VARBINARY/NCHAR
    /// 的标签列最大 16 382 字节；数据列那颗 65 517 的缺省上限在 TAG 这一侧收窄）。</summary>
    const int TagVarMaxBytes = 16_382;

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        if (contract.Indexes.Count > 0)
            throw new QuickSqlException(
                "TDengine 的建表拒收契约索引：本家没有用户索引那一档（查询面全靠引擎自建的数据骨架），"
                + "契约里的 IndexContract 是'表建成之后另有对象'那一格的形状（唯一约束/行定位器），进不去 —— "
                + "普通 [Index] 注解请摘掉再建这一族的表。");

        if (contract.SkipIndexes.Count > 0)
            throw new QuickSqlException(
                "TDengine 的建表拒收跳过索引声明：[ClickHouseSkipIndex] 是 ClickHouse 那一族的数据骨架声明，"
                + "本家没有对应的那一档 —— 这份契约里的跳过索引声明搬错了家。");

        if (contract.Stable is { } stable)
        {
            EmitCreateStable(writer, contract, stable);
            return;
        }

        ValidateKeyShape(contract.Columns, contract, stable: false);

        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column, isTag: false));

        writer.Append(string.Join(", ", parts)).Append(')');

        // 表注释不发这里：TryEmitTableComment 那一条 Core 语句管（两处一起发就是同一句话发两遍）。
    }

    /// <summary>稳定表建表：CREATE STABLE 数据列 TAGS 标签列。校验次序与普通表同形
    /// （先键形状后发射），校验对象是"数据列"——TAG 可声明在任何位置，
    /// "首列 TIMESTAMP"那颗厂商硬性事实数的是数据列的第一颗。稳定与分表并用在契约期
    /// （EntityContractFactory.Build）已经整格拒，这里的同款守卫只对"手工拼契约绕过收口"
    /// 那条路说话——虚口保真，与 Keys 那两颗同一样式。</summary>
    void EmitCreateStable(SqlWriter writer, EntityContract contract, StableDeclaration stable)
    {
        if (contract.Table.Split is not null)
            throw new QuickSqlException(
                "TDengine 的稳定表不接分表声明（契约期已拒，走到这里说明有人绕过了 EntityContractFactory 拼契约）："
                + "两套\"一个实体对多张表\"不并用——分表的片名模板落在 [Table] 名上，"
                + "而稳定表实体那里那颗名字是稳定名，子表命名走 [TDengineStable(Subtable = …)] 的模板那档"
                + "（插入时用行内 TAG 值解析）。");

        var dataColumns = DataColumns(contract, stable);
        var tagColumns = TagColumnsOf(contract, stable);

        ValidateKeyShape(dataColumns, contract, stable: true);

        writer.Append("CREATE STABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(dataColumns.Length);

        foreach (var column in dataColumns)
            parts.Add(Definition(column, isTag: false));

        writer.Append(string.Join(", ", parts)).Append(") TAGS (");

        var tagParts = new List<string>(tagColumns.Length);

        foreach (var column in tagColumns)
            tagParts.Add(Definition(column, isTag: true));

        writer.Append(string.Join(", ", tagParts)).Append(')');
    }

    /// <summary>这一颗列是不是稳定声明里的 TAG（按成员名）。</summary>
    static bool IsTag(StableDeclaration stable, ColumnContract column)
    {
        for (var i = 0; i < stable.Tags.Count; i++)
            if (stable.Tags[i].MemberName == column.MemberName)
                return true;

        return false;
    }

    /// <summary>数据列清单：声明序里不是 TAG 的那些。"首列 TIMESTAMP"与键形状校验都数这一份。</summary>
    static ColumnContract[] DataColumns(EntityContract contract, StableDeclaration stable)
        => Array.FindAll(contract.Columns, c => !IsTag(stable, c));

    /// <summary>TAG 列清单：按声明序（TAGS 子句的列序就是它），从契约里取回整颗列契约。</summary>
    static ColumnContract[] TagColumnsOf(EntityContract contract, StableDeclaration stable)
        => [.. stable.Tags.Select(t => contract.ByMember[t.MemberName])];

    /// <summary>键形状校验：把契约的键折成与本家隐含事实同形的那一形。本家主键 = 首列 TIMESTAMP
    ///（系统自动设），所以契约里的主键必须恰一颗、是首颗数据列、且 CLR 型映射 TIMESTAMP；
    /// 无主键放行（首列时间戳仍然必须——insert-only 日志表的合法形状，写路径要主键的地方内核本来就拒）；
    /// 复合键没有落点（本家的 COMPOSITE KEY 是"时间戳之外再加一颗"的扩展形，契约里没有
    /// "时间戳 + 复合"那一形可表达，重开面记 notes）。</summary>
    void ValidateKeyShape(ColumnContract[] columns, EntityContract contract, bool stable)
    {
        if (contract.PrimaryKey.Length == 0)
        {
            RequireTimestampFirst(columns, contract, stable);
            return;
        }

        if (contract.PrimaryKey.Length > 1)
            throw new QuickSqlException(
                "TDengine 的建表拒收复合主键：本家的主键是'首列 TIMESTAMP 系统自动设为主键'的隐含事实，"
                + "复合形状没有落点（官方的 COMPOSITE KEY 是在时间戳之外再加一颗键列的扩展形，"
                + "契约里没有'时间戳 + 复合'那一形可表达）。请把主键收敛成首列时间戳那一颗。");

        var key = contract.PrimaryKey[0];

        if (!ReferenceEquals(key, columns[0]))
            throw new QuickSqlException(
                $"TDengine 的主键必须是首颗数据列，而这份契约把主键点在了 {key.ColumnName} 上：本家的主键是"
                + "'首列 TIMESTAMP 系统自动设为主键'的隐含事实【VendorDoc·TDengine：建表页】，"
                + "契约里的键形状要与它同形 —— 把时间戳列放成实体的第一个成员并声明为主键"
                + "（[Key] 或 [QuickColumn(IsPrimary = Yes)]），int Id 血统的模型在这一家建不出表。");

        RequireTimestampFirst(columns, contract, stable);
    }

    /// <summary>每一张表的首颗数据列都必须是 TIMESTAMP（本家的硬性事实，与有没有声明主键无关）。
    /// 稳定表上 TAG 可声明在前，数的是数据列。</summary>
    void RequireTimestampFirst(ColumnContract[] columns, EntityContract contract, bool stable)
    {
        var first = columns[0];
        var storeType = types.GetStoreType(types.ApplyStorageDefaults(first));

        if (!string.Equals(storeType.Trim(), "TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException(
                $"TDengine 的每一张表第一列都必须是 TIMESTAMP，而这份契约的{(stable ? "首颗数据列" : "首列")}"
                + $" {first.ColumnName} 的存储型是 {storeType}"
                + "【VendorDoc·TDengine：建表页——\"表的第一个字段必须是 TIMESTAMP，系统会自动将其设为主键\"】。"
                + "把一个 DateTime 成员放成实体的第一个成员"
                + "（insert-only 的日志表可以不声明主键，写路径要主键的地方内核会自己拒）；"
                + "int Id 血统的模型在这一家建不出表。");
    }

    /// <summary>加列：ALTER TABLE … ADD COLUMN 列定义。本家的加列一次一条、只能加在尾部
    ///（首列时间戳的位置是建表时定的），Core 的补列计划照这个形状发。稳定表上这一句
    /// 要的是 ALTER STABLE（SPI 只给表名不给契约，本层拿不到稳定这一格）——以库的拒绝收尾，记 notes。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column, isTag: false));
    }

    /// <summary>改列：ALTER TABLE … MODIFY COLUMN 完整列定义（VendorDoc·TDengine：改可变长类型
    /// 长度的官方形；"完整"的代价是把列定义整份重写）。能力位刻意不声明 Alter_Column_Precision：
    /// 改宽的官方句在，但收窄/改型的行为边界（数据截断？拒回？）没有读数，活库复核再翻。
    /// 稳定表上同 EmitAddColumn 那一格：以库的拒绝收尾，记 notes。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column, isTag: false));
    }

    /// <summary>本家没有列注释这一格：官方 column_definition 文法里没有 COMMENT 项、
    /// INS_COLUMNS 目录也没有备注列——写不进也读不回，交 false 让那一格进"判不了"清单。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) => false;

    /// <summary>表注释：ALTER TABLE … COMMENT '注'（VendorDoc·TDengine：表注释可用于普通表与超级表，
    /// ALTER 修改形同页；置值形幂等，重发多少遍都把备注顶成同一个值）。comment 为 null 按清文字处理
    ///（发空串），与读回那一侧的空串口径闭合。稳定表上同 EmitAddColumn 那一格：以库的拒绝收尾，记 notes。</summary>
    public bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" COMMENT ");
        writer.AppendStringLiteral(comment ?? "", unicode: false);

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
        => throw new QuickSqlException(
            "TDengine 没有 CREATE INDEX 这种独立语句：本家没有用户索引那一档，查询面全靠引擎自建的"
            + "数据骨架（时序引擎按主键时间戳与标签自动组织），而契约里的 IndexContract 是"
            + "唯一约束/行定位器那一格的 OLTP 形状 —— 这里红说明有人把 [Index] 那一家的语义搬到了本家。");

    /// <summary>子表建表脚本口（宿主手工口，照 ClickHouse EmitAddSkipIndex 的先例：不在 SPI 上，
    /// Core 的结构同步不走这一句）。官方形 CREATE TABLE 子表 USING 稳定 TAGS (…)
    ///（VendorDoc·TDengine：超级表页——与 INSERT USING 自动建表同语义，区别只有这句不写数据）；
    /// TAG 值按声明序给、颗数对齐，值一律内联（脚本口不发参数）。要预建子表的现场从这里拿文本，
    /// 走 orm.Ado 发出去。</summary>
    public void EmitCreateSubtable(SqlWriter writer, EntityContract contract, string subtable, params object?[] tagValues)
    {
        if (contract.Stable is not { } stable)
            throw new QuickSqlException(
                $"{contract.Table.Name} 不是稳定表实体（没有 [TDengineStable] 声明）：子表只能挂在稳定表下建，"
                + "普通表的建表走 EmitCreateTable 那一条。");

        if (tagValues.Length != stable.Tags.Count)
            throw new QuickSqlException(
                $"子表 {subtable} 的 TAG 值给了 {tagValues.Length} 颗，稳定表声明的是 {stable.Tags.Count} 颗："
                + "一颗不多一颗不少（缺省那一档显式给 null，走部分标签列形式的场景请用插入面的 USING 形）。");

        // 子表与稳定表同库：稳定带 schema 限定时子表跟同一颗限定——不跟的话，连接串缺省库
        // 不是这一格时子表落错库，"看起来建好了"。
        writer.Append("CREATE TABLE ");

        if (contract.Table.Schema is { Length: > 0 } schema)
            writer.Append(Identifiers.Quote(schema)).Append('.');

        writer.Append(Identifiers.Quote(subtable)).Append(" USING ");
        EmitTable(writer, contract.Table);
        writer.Append(" TAGS (");

        for (var i = 0; i < tagValues.Length; i++)
        {
            if (i > 0)
                writer.Append(", ");

            if (!values.TryInlineLiteral(tagValues[i], null, out var literal) || literal.Length == 0)
                throw new QuickSqlException(
                    $"子表 {subtable} 的第 {i + 1} 颗 TAG 值（{stable.Tags[i].MemberName}）不是能内联的那一档："
                    + "脚本口不发参数，byte[] 这类形状请先在声明侧定文本形（[Column(TypeName=…)] 或换成员型）。");

            writer.Append(literal);
        }

        writer.Append(')');
    }

    /// <summary>列的 DDL 字形：只有 "名" 型名一段。官方 column_definition 文法里没有
    /// NULL/NOT NULL、没有 DEFAULT、没有列注释——可空性与默认值在 DDL 上没有落点，
    /// 契约里带的那些格在这里都是"发不出去"而不是"可以不发"。TAG 列另过发射点门禁。</summary>
    string Definition(ColumnContract column, bool isTag)
    {
        var writer = new SqlWriter(new(), [], '?');

        EmitColumnDefinition(writer, column, isTag);

        return writer.ToString();
    }

    /// <summary>列定义本体：与 Definition 同一段文本（一个用中间 buffer、一个直发 writer），改这一块看两处。</summary>
    void EmitColumnDefinition(SqlWriter writer, ColumnContract column, bool isTag)
    {
        var stored = types.ApplyStorageDefaults(column);
        var storeType = types.GetStoreType(stored);

        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("TDengine 不建 rowversion 列：这一族的类型清单里没有那一型（按缺席读"
                + "【文档·TDengine：类型页清单没列出任何与 SQL Server rowversion 同实务的一型】）"
                + "，而且这一族连\"由库自动递增的列\"那一档都没有 —— 发出去是一句库不认识的型名。"
                + "请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另几族逐字同形（TDengineChangeGuard）。");

        if (column.IsIdentity)
            throw new QuickSqlException(
                $"TDengine 不建 identity 列（{column.ColumnName}）：本家没有\"由库自动递增的列\"那一档，"
                + "主键就是那颗时间戳、值由调用方自己给 —— 声明里的自增那一格请摘掉。");

        if (column.DefaultValue is { } value)
            throw new QuickSqlException(
                $"TDengine 的建表拒收列默认值（{column.ColumnName} = {value}）：官方 column_definition 文法里"
                + "没有 DEFAULT 项【VendorDoc·TDengine：建表页】，这一格在 DDL 上没有落点 —— "
                + "请摘掉 HasDefaultValue/[DefaultValue] 声明，缺省语义放到应用侧。");

        if (isTag)
            storeType = TagStoreType(storeType, column.ColumnName);

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(storeType);
    }

    /// <summary>变长 TAG 的库侧上限（VendorDoc·TDengine：数据类型页——BINARY/VARBINARY/NCHAR 的
    /// 标签列最大 16 382 字节）。ApplyStorageDefaults 给数据列的那颗 65 517 上限在 TAG 这一侧收窄；
    /// DECIMAL 不可作 TAG（厂商明文"仅支持普通列"）当场红。其余型名原样穿过。</summary>
    static string TagStoreType(string storeType, string columnName)
    {
        var open = storeType.IndexOf('(');

        if (storeType.StartsWith("DECIMAL", StringComparison.Ordinal))
            throw new QuickSqlException(
                $"TAG 列 {columnName} 的存储型是 {storeType}：DECIMAL 仅支持普通列、暂不支持标签列"
                + "【VendorDoc·TDengine：数据类型页】—— 换成员型或摘掉 [TDengineTag]。");

        if (open > 0
            && (storeType.StartsWith("VARCHAR", StringComparison.Ordinal) || storeType.StartsWith("VARBINARY", StringComparison.Ordinal))
            && int.TryParse(storeType[(open + 1)..^1], out var width) && width > TagVarMaxBytes)
            return storeType[..(open + 1)] + TagVarMaxBytes + ")";

        return storeType;
    }

    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
