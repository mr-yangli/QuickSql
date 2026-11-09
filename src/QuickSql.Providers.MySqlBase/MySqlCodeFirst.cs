using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的 DDL。与血统特性绑死的几格：Schema 为 null 什么都不写（模式就是 DATABASE()，由连接串决定建表落在哪个库）；
/// 改列与加列注释都走 MODIFY COLUMN 整列重写——型、可空性、AUTO_INCREMENT、注释必须一次写全，漏哪格就顺手改了哪格；
/// 建表带 ENGINE = InnoDB 与 DEFAULT CHARSET = utf8mb4（前者兑现事务承诺，后者让 VARCHAR(n) 的 n 装得下中文）。
/// DROP INDEX 必须带 ON 表（索引名住在表的命名空间里）；全文索引是真语法（发原生子句）；索引列的字节上限
/// （767/3072）今天不判——长字符串列建索引红在库而不是红在这里（前缀长度在契约里没有那一格）。</summary>
public class MySqlCodeFirst(MySqlTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.MySql;

    /// <summary>自增写法：列定义里的一个子句，位置在型与可空性之间（这一家两种顺序都认，取文档示例那一形）。</summary>
    protected virtual string IdentityClause => " AUTO_INCREMENT";

    /// <summary>建表尾部的表选项。不是版式而是语义：ENGINE 决定这张表有没有事务，CHARSET 决定 VARCHAR(n) 能不能装下中文。
    /// 刻意不写 COLLATE：排序规则是现场策略（它决定守卫的比较结果），猜一颗比留给现场更糟。</summary>
    const string TableOptions = " ENGINE = InnoDB DEFAULT CHARSET = utf8mb4";

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length + 1);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        if (contract.PrimaryKey.Length > 0)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            parts.Add($"CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)} PRIMARY KEY ({key})");
        }

        writer.Append(string.Join(", ", parts)).Append(')').Append(TableOptions);

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>一句 MODIFY COLUMN 把整列定义重写（含注释）。COLUMN 关键字可省，但写出来与 ADD COLUMN 同形，
    /// 五家对读时少一处"这一家为什么省了"。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column, includeComment: true));
    }

    /// <summary>这一家唯一的注释写法：MODIFY COLUMN 带 COMMENT。返回 true 而不走 Core 那条"跳过并 WARN"的退路，
    /// 因为这件事在这家确实做得到；代价（重写整列）由两条共用一个 Definition 出口兜住，不会各自漏一格。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column, includeComment: true));

        return true;
    }

    /// <summary>表注释是一句表选项（ALTER TABLE … COMMENT = '…'），不碰任何一列，所以不共享列侧"重发整列"的纪律。
    /// 反斜杠档必须带：注释是契约文本直接排版进字面量，而这一家缺省把 \ 当转义符。</summary>
    public bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" COMMENT = ").AppendStringLiteral(comment ?? string.Empty, unicode: false, backslashIsEscape: true);

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        writer.Append(index.Kind switch
        {
            IndexKind.Unique => "CREATE UNIQUE INDEX ",

            // 五家里唯一"升格"的一格：PG 系与 Oracle 血统两族抛，T-SQL 落回普通 CREATE INDEX（看起来建成了）。
            // 今天没有任何公共入口能造出这颗 Kind（IndexAttribute/FluentIndex 都不带 FullText）。
            // 全文索引按相关度排序，ASC/DESC 对它无意义，所以不写方向。
            IndexKind.FullText => "CREATE FULLTEXT INDEX ",
            _ => "CREATE INDEX ",
        });

        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            if (index.Kind == IndexKind.FullText)
                continue;

            // 方向 8.0 起才真生效：5.7 解析但忽略，DESC 建出来是升序。今天不判版本也不因此不写——
            // 写了在 8.0 上是对的，没写的版本上它才是静默的。
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    /// <summary>删库里读回来的那一条：ON 那一半不是版式，是这一家索引名住在表的命名空间决定的——光有名字指不落。
    /// 表名的模式限定与建表走同一个 EmitTable（模式为空就不写）。</summary>
    public bool TryEmitDropIndex(SqlWriter writer, TableInfo table, SchemaIndex index)
    {
        writer.Append("DROP INDEX ").Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);

        return true;
    }

    string Definition(ColumnContract column, bool includeComment = false)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column, includeComment);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column, bool includeComment)
    {
        var stored = types.ApplyStorageDefaults(column);

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(types.GetStoreType(stored));

        if (column.IsIdentity)
            writer.Append(IdentityClause);

        // 可空性读 NullableInStore（五族同一条规则：DDL 里的可空性不读 IsNullable）。
        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

        // 注释只在契约真写了才发：契约没写而库里有一处注释时，改精度那句会把那一处注释抹掉（已知代价）。
        // 反斜杠档必须带：注释与表注释是这一族仅有的两处"契约文本直接排版进字面量"的执行路径。
        if (includeComment && column.Comment is { Length: > 0 } comment)
            writer.Append(" COMMENT ").AppendStringLiteral(comment, unicode: false, backslashIsEscape: true);

        EmitDefault(writer, stored);
    }

    /// <summary>列默认值。这一族字符串字面量反斜杠是转义（与备注同一条），bool 落 1/0（TINYINT(1) 的存储形）。</summary>
    void EmitDefault(SqlWriter writer, ColumnContract column)
    {
        if (column.DefaultValue is not { } value)
            return;

        writer.Append(" DEFAULT ");

        switch (value)
        {
            case string text:
                writer.AppendStringLiteral(text, unicode: false, backslashIsEscape: true);
                break;

            case bool flag:
                writer.Append(flag ? "1" : "0");
                break;

            default:
                writer.Append(ColumnDefaultText.Number(value));
                break;
        }
    }

    /// <summary>模式为空就不写限定：这一家裸名说的是"当前连接所选的数据库"，
    /// 与 Oracle 血统同一条判据（不补假名字），与 T-SQL/PG 两家的 dbo/public 分岔。</summary>
    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
