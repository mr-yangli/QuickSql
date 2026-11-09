using QuickSql.Metadata;
using QuickSql.Sql;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的建表与改表，每一条要发的 DDL 形状都在活库上发过一次。三条主干约束：
/// 自增只能内联——INTEGER PRIMARY KEY AUTOINCREMENT 与表级主键语法级互斥，且型名必须换成
/// INTEGER（BIGINT 被正面拒；这不是把键弄窄，这一档的存储就是 64 位 rowid）；模式限定在
/// CREATE INDEX 上要挪到索引名那一侧（挂表侧是语法错，而库里存的 DDL 文本会把限定词抹掉——
/// 结构同步比的是存在性不是文本）；本文件的"抛"大多数带的是库自己说过的原话（ADD COLUMN
/// 不收自增列与非空无默认列，改精度没有语句也没有对象，COMMENT 一词本身就是语法错——
/// 这一族没有任何列注释的落处）。取 AUTOINCREMENT 而不取裸 INTEGER PRIMARY KEY 是为了
/// 不复用刚删掉的号；多语句串（建表 + 索引用 ';' 串）有活证，尾句不带分号也会被执行，
/// 而 DDL 的 ExecuteNonQuery 交回 affrows = 0——共用层若在 DDL 之后判 Affrows 会一律看到 0。</summary>
public sealed class SqliteCodeFirst(SqliteTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.Sqlite;

    /// <summary>取 AUTOINCREMENT 的代价是它依赖 sqlite_sequence，取它的理由是裸形会复用刚删掉的号。
    /// 写成 const 而不是 virtual（与 XuguCodeFirst.IdentityClause 同一条纪律）：它只给子句，
    /// 必需的 INTEGER 由 IdentityStoreType 给。</summary>
    const string IdentityClause = " PRIMARY KEY AUTOINCREMENT";

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        // 内联自增键已经自带 PRIMARY KEY，而本家那一颗与表级主键互斥（305/2806/3009 三颗同一句红话）。
        var inlineKey =
            contract.PrimaryKey.Length == 1 && contract.PrimaryKey[0].IsIdentity
                ? contract.PrimaryKey[0]
                : null;

        if (
            inlineKey is null
            && contract.PrimaryKey.Length > 0
            && contract.Columns.Any(c => c.IsIdentity)
        )
            throw new QuickSqlException(
                "SQLite 不能把自增列放进表级主键：内联的 "
                    + "INTEGER PRIMARY KEY AUTOINCREMENT 与表级 PRIMARY KEY 在这一族是互斥的两颗（305 ∶ 2806 ∶ 3009，"
                    + "三颗都报 table has more than one primary key）。今天请让自增列单独作主键，"
                    + "或把并发/业务那把复合键改成不含自增列——库没有「第二把键」这一格，缺的不是本家的发射而是这一族的语法。"
            );

        var parts = new List<string>(contract.Columns.Length + 1);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        if (contract.PrimaryKey.Length > 0 && inlineKey is null)
        {
            var key = string.Join(
                ", ",
                contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName))
            );

            // 具名表级主键这一形在本家成立（目录里 pk 序号逐颗对得上）。
            parts.Add(
                $"CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)} PRIMARY KEY ({key})"
            );
        }

        writer.Append(string.Join(", ", parts)).Append(')');

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    /// <summary>本家 ADD COLUMN 只有"可空、无键、无唯一"那一档是活的（带主键、带 UNIQUE、
    /// 带 NOT NULL 而无 DEFAULT 的三颗各报一句），所以这里先挡自增列与非空无默认列两颗、
    /// 把第三颗交给库。挡在发射点而不是建完之后。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        // 可空性读 stored 而不是裸契约值，与另六族同一句纪律。本家的 ApplyStorageDefaults
        // 今天不改这一颗位，所以这一读法现在是等价的；写成 stored 是因为"等价"这件事归那颗方法管，不归这里管。
        var stored = types.ApplyStorageDefaults(column);

        if (column.IsIdentity)
            throw new QuickSqlException(
                $"SQLite 不能往已有表加自增列（{column.ColumnName}）：这一族的自增必须是"
                    + "内联主键，而 ALTER TABLE ADD COLUMN 明确不收主键那一支（312b ∶ 2813 同一句 "
                    + "Cannot add a PRIMARY KEY column）。今天请建新表并搬数据，或把这一列做成应用给值的普通列。"
            );

        // 契约有了「列默认值」那一格：配了默认值的 NOT NULL 新列在结构同步那一格放行，
        // 走到这里的句子带 DEFAULT …（同一条语句带 DEFAULT 0 就成立是这一格的正面读数）。
        // 没配默认值的照旧拒。
        if (!stored.NullableInStore && column.DefaultValue is null)
            throw new QuickSqlException(
                $"SQLite 不能往已有表加非空列（{column.ColumnName}）：库里那一格需要一个"
                    + "非 NULL 的默认值来填已有行，而这一列没有配默认值（Fluent HasDefaultValue / [DefaultValue]；313 ∶ 2811 报 "
                    + "Cannot add a NOT NULL column with default value NULL ∶ 同一条语句带 DEFAULT 0 就成立，实测 3010）。"
                    + "今天请先以可空列加上、再回填、约束那一格在这一族改不回去。"
            );

        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>不可达：本家不声明 Alter_Column_Precision。走到这里说明那颗位被绕过，
    /// 抛的话要说清"不是没实现，而是这一问在本家没有对象"。</summary>
    public void EmitAlterColumnPrecision(
        SqlWriter writer,
        TableInfo table,
        ColumnContract column
    ) =>
        throw new QuickSqlException(
            "SQLite 没有改列型状的语句（F4 ∶ 2815 都是 near \"ALTER\": syntax error），"
                + "而且这一问在这一族没有对象：声明宽度从不参与存储（实测 4001 ∶ 4002），改宽度改不动库里的任何东西。"
                + "真正要换型的那一族操作在这一族是建新表、拷数据、改名，其形状本次没有逐颗读数（§14）。"
        );

    /// <summary>不发任何东西，返回 false：这一族的 COMMENT 是语法错，而目录回显的是引擎规范化的
    /// DDL 文本，所以也没有"塞进原文再读回来"的退路。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) =>
        false;

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        if (index.Kind == IndexKind.FullText)
            throw new QuickSqlException(
                "SQLite 没有 CREATE FULLTEXT INDEX 这一句：28 到 30 轮把建索引的三种形都问过了"
                    + "（普通、UNIQUE、带 WHERE 的部分索引都能建，实测 2816/2817/2818），里面没有全文那一档。"
                    + "这一族确实有 fts5 那一族虚表，但本仓库对它一条读数都没有，所以这里不发——"
                    + "那一格与 §14 的虚表/生成列并排（3012 量到 ADD COLUMN 收生成列而契约没有那颗，同一个形状）。"
                    + "今天请建普通索引承担这个职责。"
            );

        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");

        // 类注释第二条：模式限定只能挂在这颗名字上，表那一侧挂它是语法错。
        if (table.Schema is { Length: > 0 } schema)
            writer.Append(Identifiers.Quote(schema)).Append('.');

        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");

        // 这里刻意不调 EmitTable：那一句会写出 模式.表，而本家的建索引不收限定表名。
        writer.Append(Identifiers.Quote(table.Name));
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            // 两颗都发：这一族认 ASC 也认 DESC，所以不必像 Gbase8sCodeFirst 那样只写一半。
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);

        // 自增那一支在这里整颗换掉型名与键（BIGINT 被正面拒绝）。
        if (column.IsIdentity)
        {
            writer
                .Append(Identifiers.Quote(column.ColumnName))
                .Append(IdentityStoreType(column))
                .Append(IdentityClause);

            writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

            return;
        }

        writer
            .Append(Identifiers.Quote(column.ColumnName))
            .Append(' ')
            .Append(types.GetStoreType(stored));

        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

        EmitDefault(writer, stored);
    }

    /// <summary>自增列的型名：本家它是那颗 INTEGER 而不是 CLR 宽度对应的那一颗（long → BIGINT 会被库拒）。
    /// 判据取"算出来的型名里有没有 INT"：有则说明这一列在契约里就是整数族，换成 INTEGER 只改声明字形
    /// 而不改存储；没有则抛——文本列上的自增在本家没有形状，而库里那一格已经证明不收。
    /// 人手工写的 [Column(TypeName = …)] 在这里同样被换掉：IsIdentity 是这一列更近的事实，
    /// 而写下的那个名字在自增列上本来就不成立。</summary>
    string IdentityStoreType(ColumnContract column)
    {
        var declared = types.GetStoreType(types.ApplyStorageDefaults(column));

        if (!declared.Contains("INT", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException(
                $"SQLite 的自增列必须是整数那一族（{column.ColumnName} 的存储型是 {declared}）："
                    + "AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY（实测 306 ∶ 2807 两颗同一句，后者是 BIGINT）。"
                    + "整数族（byte/int/long/uint 与它们的枚举底）会被本家统一换成那颗 INTEGER，"
                    + "因为 AUTOINCREMENT 只认那一个字形而存储仍是 64 位（实测 310/310b）。"
            );

        return " INTEGER";
    }

    /// <summary>模式为空就不写限定，与 XuguCodeFirst/MySqlCodeFirst 那两份同形。
    /// 限定在 CREATE TABLE 与 ALTER TABLE 上都能发，只有建索引不收；本方法的默认形状不是
    /// "这一族不认限定"，而是"限定在这里是一颗会话级事实"——main 永远在，
    /// 别的名字要连接串或 ATTACH 先把它带进来。</summary>
    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }

    /// <summary>列默认值的 SQLite 半边。本家 ADD COLUMN 带 NOT NULL 时必须有默认值——这正是这一格的主要用武之地。布尔落 1/0（INTEGER 存储）。</summary>
    void EmitDefault(SqlWriter writer, ColumnContract column)
    {
        if (column.DefaultValue is not { } value)
            return;

        writer.Append(" DEFAULT ");

        switch (value)
        {
            case string text:
                writer.AppendStringLiteral(text, unicode: false);
                break;
            case bool flag:
                writer.Append(flag ? "1" : "0");
                break;
            default:
                writer.Append(ColumnDefaultText.Number(value));
                break;
        }
    }
}
