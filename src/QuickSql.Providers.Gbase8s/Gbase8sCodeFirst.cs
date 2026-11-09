using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的建表与改表。三处分岔：列定义里没有自增子句——自增在型名里
/// （SERIAL/BIGSERIAL），换名落在 SerialFor 上（写在 GetStoreType 里是死分支：共用层先定型、后判自增）；
/// 约束名写在 PRIMARY KEY (…) 之后（CONSTRAINT 跟在括号后面——只差一个词序，在库里是语法错）；
/// TryEmitColumnComment 返回 false——语句清单里没有 COMMENT ON 那一条，列注释是目录表的事，
/// 共用层那条发射路径第一次走到一家就断。
/// 另两处："模式"在库里是所有者（CREATE SCHEMA AUTHORIZATION user），发不出别家那种"建模式放表"的
/// 语句；尾部用分号串索引语句，而多语句批能不能收没有读数——与语句后回读是同一问。
/// rowversion 拒收按缺席读：类型清单里没有那一型，发出去是一句库会拒的建表
/// （没有"建得成"那一半读数，未问）。</summary>
public sealed class Gbase8sCodeFirst(Gbase8sTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.Gbase8s;

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

            // 词序与另五家相反：CONSTRAINT 跟在括号后面，不是走在 PRIMARY KEY 前面。
            parts.Add($"PRIMARY KEY ({key}) CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)}");
        }

        writer.Append(string.Join(", ", parts)).Append(')');

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    /// <summary>带括号的 ADD (…)：本家的 ADD 子句是列定义清单，括号是"能一次加多列"那个位置的容器——
    /// 共用层一次只给一列，所以发出去永远是 ADD (一个元素) 的形状。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD (").Append(Definition(column)).Append(')');
    }

    /// <summary>MODIFY (列 类型)：改列不叫 ALTER COLUMN，括号里是完整列定义——契约里没写的格子
    /// 会被库按这一句给的形状定下来（与虚谷记过的是同一类风险）。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY (").Append(Definition(column)).Append(')');
    }

    /// <summary>不发任何东西，返回 false（语句清单里没有 COMMENT ON，列注释是目录表的事）。
    /// 刻意不造列内注释子句：那会得到一句库拒的建表，比不写注释严重得多。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) => false;

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        if (index.Kind == IndexKind.FullText)
            throw new QuickSqlException("GBase 8s 没有 CREATE FULLTEXT INDEX 这种写法：这一族的索引种类按手册是普通、EXCLUDE（排除索引）与聚簇那一族【文档·IBM】，"
                + "而全文检索在那一边是 Blend 扩展件、不是一条语句级子句 —— 本次没有可引的那一页，所以今天不发。"
                + "另注意这一家多出来的那一支（EXCLUDE）在契约的 IndexKind 里没有对应那颗，所以它今天也发不出去（§14，与虚谷那句 BITMAP 同一格）。");

        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");
        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            // 只写 DESC、不写 ASC：缺省那一支本来就是升序，少写不改语义；
            // 万一这一家不认 ASC 那颗关键字，多写的那颗会让整句红——两案不对称，取少写的一边。
            if (index.Descending)
                writer.Append(" DESC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '?');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);
        var storeType = types.GetStoreType(stored);

        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("GBase 8s 不建 rowversion 列：这一族的类型清单里没有那一型（按缺席读【文档·IBM】，"
                + "本次没有读到它的清单，更没有一台库能问）。它不是虚谷那一格（那一家是实测到「建得成、会自增、读不回」三格之后裁的），"
                + "所以这里的判据只有一条：发一句库会拒的建表不如不发。"
                + "这一家请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另五家逐字同形（Gbase8sConcurrency），缺的只是「由库递增」那一格 —— 那一格等的是 SPI 与一台库，不是改名。");

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(SerialFor(column, storeType));

        // 本家没有列尾自增子句：IsIdentity 已经在型名里（SERIAL / BIGSERIAL）。
        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

        EmitDefault(writer, stored);
    }

    /// <summary>自增列把整数型名换成 SERIAL/BIGSERIAL，其余原样穿过。为什么不写在 GetStoreType 里：
    /// 共用层先定型、后判自增，那里看到的 IsIdentity 恒为 false，是一颗永远走不到的分支。
    /// 换名判据取"算出来的型名"而不是"人有没有写过"——定型那步填了 StoreTypeName 之后 echo 与
    /// 人写的已分不开，所以人手写 [Column(TypeName = "INTEGER")] 在自增列上会被换成 SERIAL
    /// （有意：IsIdentity 是这一列更近的事实）；换别的名字原样穿过。</summary>
    static string SerialFor(ColumnContract column, string storeType)
    {
        if (!column.IsIdentity)
            return storeType;

        return storeType.Trim() switch
        {
            "INTEGER" => "SERIAL",
            "BIGINT" => "BIGSERIAL",
            _ => storeType,
        };
    }

    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }

    /// <summary>列默认值。布尔落 1/0（INTEGER 存储）；字符串走字面量转义；数值走不变文化格式。</summary>
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
