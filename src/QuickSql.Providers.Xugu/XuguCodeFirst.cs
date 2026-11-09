using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的 DDL。标识符折叠与查询共用：双引号一漏，建出来的表名与查的表名就不是同一个东西。
/// 模式为 null 什么都不写（默认模式 = 当前会话用户，补一个假名字会造出"看着限定了其实不存在"的模式）；
/// 自增是列上的 IDENTITY(1,1) 子句（与自己的 Oracle 血统相反，Oracle 族是 GENERATED … AS IDENTITY）；
/// 改精度一条语句、同句重述可空性（本文件出处最薄的一格，取最保守形：不重述在 T-SQL 形上是静默改约束）；
/// 列注释是原生 COMMENT ON COLUMN；全文索引抛（厂商列的索引种类是普通/UNIQUE/BITMAP，没有全文那颗，
/// 而多出来的 BITMAP 契约里也没有）。
/// 两处实测背书的坑：建表尾部没有任何表选项——ENGINE/DEFAULT CHARSET/CHARACTER SET 三种写法全被解析器驳回
/// （不是"只有一个引擎"，是压根没有引擎那一格）；字符集只能在连接串上给，所以"默认映射到宽的一侧"不由 DDL 保证，
/// 同一份建表文本在不同连接上会建出字节宽度不同的列。
/// rowversion 是第一颗被 DDL 拒收的名字：库里建得成、会自增，但值取不回（读即异常），
/// 发出去是一列"看着能用"的废列。</summary>
public sealed class XuguCodeFirst(XuguTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.Xugu;

    /// <summary>自增写法，const 而不是 virtual（没有第二种库种，不留没人覆写的虚函数）。</summary>
    const string IdentityClause = " IDENTITY(1,1)";

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

        // 尾部没有表选项：建表语法在列清单之后就收尾了（三种表级选项实测全部驳回）。
        writer.Append(string.Join(", ", parts)).Append(')');

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

    /// <summary>一条语句把型与可空性一起重写。读的是 NullableInStore 而不是 IsNullable：漏掉它就会把值归库给的列改回可空。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ALTER COLUMN ").Append(Definition(column));
    }

    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("COMMENT ON COLUMN ");
        EmitTable(writer, table);
        writer.Append('.').Append(Identifiers.Quote(column.ColumnName)).Append(" IS ");
        writer.AppendStringLiteral(column.Comment ?? string.Empty, unicode: false);

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        // 全文索引抛而不是降格成普通索引（T-SQL 族降格）：那一页压根没有全文那颗，降格会"看起来建成了"。
        if (index.Kind == IndexKind.FullText)
            throw new QuickSqlException($"虚谷没有 CREATE FULLTEXT INDEX 这种写法：厂商文档列的索引种类是普通、UNIQUE、BITMAP 三种（§9.2）。"
                + "今天请自己建普通索引承担这个职责，或等全文检索那一格接上（缺的不是关键字而是整条 tsvector/解析器链路，与 PG 系那一句同一档）。"
                + "另注意这一家有的 BITMAP 在契约的 IndexKind 里没有那一颗，所以这一族多出来的那一支今天也发不出去（§14）。");

        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");
        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            // 方向照四家多数派写出来（两家产物的建索引文本都带方向）；方向写错是索引形状不对，不是建表失败。
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], ':');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);

        var storeType = types.GetStoreType(stored);

        // rowversion 拒收：库里收得下这句 CREATE、也真的会自增，可那一列的值取不回（带它的投影一取行就抛异常）。
        // 抛在 DDL 侧是错误唯一的便宜出口——落在读侧是整表不可用，落在写侧是每次保存撞库。
        // 抛点在 Definition 而不是 GetStoreType：后者还有非 DDL 的读者（守卫、目录对账），拒收只属于"要不要建它"这一问。
        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("虚谷不建 rowversion 列：库里建得成、会自增，但驱动读不回那一列的值"
                + "（带它的 SELECT 一取行就抛 System.DataOutOfBindException，RV-08/A-06 两批同一颗红，§14）。"
                + "这一家请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另四家逐字同形（XuguConcurrency），缺的只是「由库递增」这一格 —— 那一格等的是 SPI，不是改名。");

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(storeType);

        if (column.IsIdentity)
            writer.Append(IdentityClause);

        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

        EmitDefault(writer, stored);
    }

    /// <summary>模式为空就不写限定。与 T-SQL/PG 两家的 dbo/public 分岔：那两家的默认模式是人人都有的公共模式，
    /// 这一家补假名字的效果是落到一个可能不存在的模式。</summary>
    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }

    /// <summary>列默认值。布尔落 1/0（TINYINT 存储）；字符串走字面量转义；数值走不变文化格式。</summary>
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
                writer.Append(flag ? "1" : "0");
                break;
            default:
                writer.Append(ColumnDefaultText.Number(value));
                break;
        }
    }
}
