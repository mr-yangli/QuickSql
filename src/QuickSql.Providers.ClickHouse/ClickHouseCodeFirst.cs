using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的建表与改表。发射形有活库出处（server 26.9.21.9 ∶ 驱动 ClickHouse.Client 7.14.0，
/// golden 与 replay 逐字回放）。分岔逐条：契约索引整格拒——本族的索引不是独立语句，是建表语句里列/表级
/// INDEX 名 表达式 TYPE 档 GRANULARITY n 的数据骨架声明（IndexContract 是"表建成之后另有对象"的 OLTP 形状，
/// 进不了那一格；跳过索引走 [ClickHouseSkipIndex] 声明，后补一颗走 ALTER TABLE … ADD INDEX）；
/// 主键不发列内子句也不发表级约束，而是发进 ENGINE——MergeTree 的"主键"是引擎参数
///（ORDER BY (cols) PRIMARY KEY (cols)，官方写明列内/列外两法不可并用；无主键发 ORDER BY tuple()）；
/// 可空列发 Nullable(T) 且不带 NULL/NOT NULL 修饰——"给 Nullable 型再上修饰"是官方页当场报错的那一类，
/// 空的许可由型包裹表达、非空的义务由 NOT NULL 表达，没有第三形；表注释走 ALTER TABLE … MODIFY COMMENT
/// 单路（裸 COMMENT 形被服务端拒回 Expected COLUMN，MODIFY 形过了），列注释走 COMMENT COLUMN，建表趟不内联；
/// ALTER 走 MODIFY COLUMN 完整列定义（不带括号）。Definition 在发射点拒收 rowversion——发出去是
/// 一句库不认识的型名，比不写注释更严重。</summary>
public sealed class ClickHouseCodeFirst(ClickHouseTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.ClickHouse;

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        if (contract.Indexes.Count > 0)
            throw new QuickSqlException(
                "ClickHouse 的建表拒收契约索引：这一族的索引不是独立语句，而是建表语句里"
                + "列/表级 INDEX 名 表达式 TYPE 档 GRANULARITY n 的数据骨架声明，"
                + "契约里的 IndexContract 是'表建成之后另有对象'那一格的形状（唯一约束/行定位器），进不去那一格 —— "
                + "要跳过索引那一档用 [ClickHouseSkipIndex] 声明（建表骨架里带出），"
                + "普通 [Index] 注解请摘掉再建这一族的表。");

        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length + contract.SkipIndexes.Count);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        // 跳过索引跟列一起进括号清单（活库兼收列式与表达式式）。
        // 不发契约索引那一格（上面整格拒）：IndexContract 的语义在这里没有落点。
        foreach (var skip in contract.SkipIndexes)
            parts.Add("INDEX " + SkipIndexBody(skip));

        writer.Append(string.Join(", ", parts)).Append(')');

        EmitStorage(writer, contract);

        // 表注释不发这里：TryEmitTableComment 那一条 Core 语句管（两家一起发就是同一句话发两遍）。
    }

    /// <summary>跳过索引声明的骨架本体："名" 表达式 TYPE 档 GRANULARITY n。前面的 INDEX/ADD INDEX
    /// 两个动词由调用位给：建表内联与 ALTER 后补共用这一份文本。档位参数的校验在这里
    ///（ClickHouse 的语义不进中性契约）：set 要 max_rows，tokenbf/ngrambf 要桶（后者还要 n 元宽），
    /// 缺一颗当场红而不是发一句库不认的型。表达式：属性挂的是 plain 列名，加方言引号；
    /// 类挂的原样抄，标识符由声明者自己负责。GRANULARITY ≤ 0 按"声明里没写"处理，发本家缺省 1。</summary>
    string SkipIndexBody(SkipIndexContract skip)
    {
        var kind = skip.Kind switch
        {
            SkipIndexKind.MinMax => "minmax",
            SkipIndexKind.Set => $"set({Require(skip.SetMaxRows, skip, "SetMaxRows（set 档的 max_rows）")})",
            SkipIndexKind.BloomFilter => "bloom_filter",
            SkipIndexKind.TokenBloomFilter => $"tokenbf_v1({Require(skip.BucketSize, skip, "BucketSize（tokenbf 档的桶大小）")}, "
                + $"{HashFunctionsOf(skip)}, {skip.Seed})",
            SkipIndexKind.NgramBloomFilter => $"ngrambf_v1({Require(skip.NgramSize, skip, "NgramSize（ngrambf 档的 n 元宽）")}, "
                + $"{Require(skip.BucketSize, skip, "BucketSize（ngrambf 档的桶大小）")}, {HashFunctionsOf(skip)}, {skip.Seed})",
            _ => throw new QuickSqlException($"跳过索引 {skip.Name} 的档位 {skip.Kind} 在本家没有形状（ClickHouse 只认"
                + " minmax ∶ set ∶ bloom_filter ∶ tokenbf_v1 ∶ ngrambf_v1 五档）"),
        };

        var expression = skip.MemberName is null ? skip.Expression : Identifiers.Quote(skip.Expression);

        return $"{Identifiers.Quote(skip.Name)} {expression} TYPE {kind} GRANULARITY {(skip.Granularity > 0 ? skip.Granularity : 1)}";
    }

    static int HashFunctionsOf(SkipIndexContract skip) => skip.HashFunctions > 0 ? skip.HashFunctions : 1;

    static int Require(int value, SkipIndexContract skip, string argument)
        => value > 0 ? value
            : throw new QuickSqlException($"跳过索引 {skip.Name}（{skip.Kind} 档）缺 {argument}：这一档的参数是"
                + "型名的一部分，缺一颗发出去就是一句库不认的 TYPE —— 请在声明里补上");


    /// <summary>ENGINE 那一段：本家所有表都要有存储子句，主键归 ORDER BY/PRIMARY KEY 两颗引擎参数。
    /// 有主键 ORDER BY (cols) 在前、PRIMARY KEY (cols) 在后；无主键发 ORDER BY tuple()。</summary>
    void EmitStorage(SqlWriter writer, EntityContract contract)
    {
        writer.Append(" ENGINE = MergeTree ");

        if (contract.PrimaryKey.Length > 0)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            writer.Append("ORDER BY (").Append(key).Append(") PRIMARY KEY (").Append(key).Append(')');
        }
        else
        {
            writer.Append("ORDER BY tuple()");
        }
    }

    /// <summary>加列：ALTER TABLE … ADD COLUMN 列定义。本家没有"括号里塞清单"那一支，加列一次一条。
    /// 可空性读 Definition 的同一份判据（ColumnContract.NullableInStore），否则"加一次列"会把形状分裂出去。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>改列：ALTER TABLE … MODIFY COLUMN 完整列定义（不带括号，与 GBase 8s 同名而形不同）。
    /// "完整"的代价是把列定义整份重写——可空性两侧都出自契约里的同一格（ColumnContract.NullableInStore），
    /// 所以"改一次精度"不会把可空性的形状改岔。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column));
    }

    /// <summary>列注释：ALTER TABLE … COMMENT COLUMN "列" '注'（活库被收）。
    /// 必须幂等：这一句是置值形，重发多少遍都把备注顶成同一个值，第二趟重发不红。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" COMMENT COLUMN ").Append(Identifiers.Quote(column.ColumnName)).Append(' ');
        writer.AppendStringLiteral(column.Comment!, unicode: false);

        return true;
    }

    /// <summary>表注释：ALTER TABLE … MODIFY COMMENT '注'（活库被收：裸 COMMENT 形被服务端拒回
    /// Expected COLUMN，MODIFY 形过了）。comment 为 null 按清文字处理（发空串），
    /// 与读回那一侧的空串口径闭合。</summary>
    public bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COMMENT ");
        writer.AppendStringLiteral(comment ?? "", unicode: false);

        return true;
    }

    /// <summary>建成之后后补一颗跳过索引：ALTER TABLE … ADD INDEX …（活库被收）。
    /// Core 的结构同步不会走到这一句：同步引擎只按 IndexContract 算索引差而本家那一格整格拒，
    /// 跳过索引在 schema 读侧没有形状，差算不出来、计划里就没有它的行——这是给宿主的手工口。</summary>
    public void EmitAddSkipIndex(SqlWriter writer, TableInfo table, SkipIndexContract skip)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD INDEX ").Append(SkipIndexBody(skip));
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
        => throw new QuickSqlException(
            "ClickHouse 没有 CREATE INDEX 这种独立语句：这一族的索引要么在建表骨架里（"
            + "[ClickHouseSkipIndex]，EmitCreateTable 带出），要么在建成之后按 ALTER … ADD INDEX 补一颗（"
            + "本家 CodeFirst 的 EmitAddSkipIndex，按 SkipIndexContract），而契约里的 IndexContract 是"
            + "唯一约束/行定位器那一格的 OLTP 形状，两路都进不去 —— 这里红说明有人把 [Index] 那一家的"
            + "语义搬到了本家。要跳过索引请换声明那一档。");

    /// <summary>列的 DDL 字形："名" Nullable(T) 或 "名" T NOT NULL，此外没有第三形。
    /// 主键列不发列内 PK 子句：列外那一法已经写在 ENGINE 那一段里，两法不可并用。
    /// rowversion 在发射点拒收。</summary>
    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    /// <summary>列定义本体：与 Definition 同一段文本（一个用中间 buffer、一个直发 writer），改这一块看两处。
    /// 可空性读 ColumnContract.NullableInStore 而不是 IsNullable：判据与另几族同一条。</summary>
    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);
        var storeType = types.GetStoreType(stored);

        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("ClickHouse 不建 rowversion 列：这一族的类型清单里没有那一型（按缺席读"
                + "【文档·ClickHouse：其数据类型清单页没列出任何与 SQL Server rowversion 同实务的一型】）"
                + "，而且这一族连\"由库自动递增的列\"那一档都没有 —— 发出去是一句库不认识的型名。"
                + "请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另几族逐字同形（ClickHouseChangeGuard）。");

        var shape = stored.NullableInStore ? $"Nullable({storeType})" : storeType;

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(shape);

        if (!stored.NullableInStore)
            writer.Append(" NOT NULL");

        EmitDefault(writer, stored);
    }

    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }

    /// <summary>列默认值（2026-11-01 那一格的 ClickHouse 半边）。原生 Bool 落 true/false（与谓词侧 = true 同边）；字符串走 AppendStringLiteral。</summary>
    void EmitDefault(SqlWriter writer, ColumnContract column)
    {
        if (column.DefaultValue is not {} value)
            return;

        writer.Append(" DEFAULT ");

        switch (value)
        {
            case string text:
                writer.AppendStringLiteral(text, unicode: false);
                break;
            case bool flag:
                writer.Append(flag ? "true" : "false");
                break;
            default:
                writer.Append(ColumnDefaultText.Number(value));
                break;
        }
    }
}
