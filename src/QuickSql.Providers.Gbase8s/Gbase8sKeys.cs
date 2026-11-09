using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的主键策略。EmitReturning 抛：这一族的 RETURNING 只在 ESQL/C 与 SPL 里
/// 成立、必须配 INTO 宿主变量，而 INTO 要 OUT 绑定——AdoParameter 没有方向位，没有 INTO 的形在 CLI 侧
/// 收不到结果集。SupportsPostStatementKeyRead 取 true 但两句要连读：回读表达式 DBINFO('sqlca.sqlerrd1'
/// | 'serial8') 有文档出处，而"一条命令文本里放两句 SQL"本家收不收没读到——错的话红在每一条带回读的
/// 开销上，不会红在少回填键。覆盖面窄：单颗自增列、逐条形、同命令同会话；带并发列与多颗自增列都落回那一抛。
/// SupportsExplicitIdentityInsert 取 false 而含义与别家不同：往 SERIAL 列写显式值默认就允许，
/// 压根不需要开关；真正的缺口是"写显式值之后序列跟不跟着挪"没有读数。刻意不为 IdentityInsertToggle
/// 留覆写点——Core 今天没有调用路径，覆写只会得到一颗永远不被调用的虚函数。</summary>
public sealed class Gbase8sKeys : IKeyStrategy
{
    /// <summary>抛，而不是发一条"看着像在回填"的语句（缺的是 AdoParameter 的方向位，不是本家的语法）。
    /// 表侧列名在消息里点出来：现场读到这条错的人下一步一定是"我这颗键为什么没回来"。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
        => throw new QuickSqlException($"GBase 8s 没有可作为结果集发出的 RETURNING，而本次请求要回填（{Columns(returning)}）。"
            + "这一家的 RETURNING 只活在 ESQL/SPL 里、且必须配 INTO 宿主变量（CLI 侧没有 INTO 的那一形收不到结果集）；"
            + "INTO 要的是 OUT 绑定，而 AdoParameter 今天没有方向位（那一格自 §4 起一直缺，Oracle 血统与虚谷各记过一次）。"
            + "单颗自增键请走语句后回读那一条（SupportsPostStatementKeyRead 为真，而它自己的前提见 Gbase8sKeys 类注释）；"
            + "多颗或带并发列的回填今天没有路，请自己生成主键（Guid/雪花）并直接用 ExecuteAffrowsAsync。");

    /// <summary>true，但只覆盖单颗自增列、逐条形、同命令同会话；前提（同一条命令文本放两句）未被任何读数证过，
    /// 错的话红在每一条带回读的开销上。重开触发：第一台活库上第一件事问这一句。</summary>
    public bool SupportsPostStatementKeyRead => true;

    /// <summary>按 CLR 侧键宽分两支：sqlca.sqlerrd1 那一槽是 32 位的，拿它读 SERIAL8/BIGSERIAL 的 64 位
    /// 序列值会静默丢高位（不报错、交回一个对不上的数）——本文件里唯一"选错不会红而会错"的格，
    /// 64 位那档必须走 DBINFO('serial8')。判据取 CLR 型而不是 StoreTypeName（后者只有
    /// ApplyStorageDefaults 跑过才有值）。不引列名、不带参数；前面的分号与空格由 Core 补。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key)
    {
        var clrType = Nullable.GetUnderlyingType(key.ClrType) ?? key.ClrType;

        // systables WHERE tabid = 1 是事实上的 SELECT-1 表源（这一族没有 DUAL，而那一行恒在）。
        // 目录表名不带引号：它是库自己的对象，且"不带引号折小写"与 systables 在目录里的小写恰好一致。
        writer.Append(clrType == typeof(long) || clrType == typeof(ulong)
            ? "SELECT DBINFO('serial8') FROM systables WHERE tabid = 1"
            : "SELECT DBINFO('sqlca.sqlerrd1') FROM systables WHERE tabid = 1");
    }

    /// <summary>false，但含义与另五家不同：这一家"写显式值"不需要开关。真正欠的是"序列会不会跟着挪"，今天没有读数。</summary>
    public bool SupportsExplicitIdentityInsert => false;

    public bool SupportsBulkKeyRetrieval => false;

    /// <summary>没有开关，且刻意不是 virtual（Core 今天没有调用点）。返回 null 让 Core 走"不需要前置/后置语句"那一支。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => null;

    static string Columns(IReadOnlyList<ColumnContract> returning)
    {
        var names = new System.Text.StringBuilder(returning.Count * 12);

        for (var i = 0; i < returning.Count; i++)
        {
            if (i > 0)
                names.Append(", ");

            names.Append(returning[i].ColumnName);
        }

        return names.ToString();
    }
}
