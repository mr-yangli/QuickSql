using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的主键策略。EmitReturning 抛：写入那一路的官方语句清单里没有 RETURNING/OUTPUT 任何一支，
/// 宁可当场红也不发"看着像在回填"的语句。SupportsPostStatementKeyRead false：回读句的前提是
/// "有一颗由库生成的自增值"，本家连"自增"那一格都没有——主键就是那颗时间戳（首列 TIMESTAMP
/// 由系统自动设为主键），值由调用方自己给，前提不成立（走到 EmitPostStatementKeyRead 即抛，虚口保真）。
/// SupportsExplicitIdentityInsert false：那颗开关管的是"往自增列里写显式值要不要解锁"，本家没有自增列，
/// 这一问没有对象。SupportsBulkKeyRetrieval false：批量回靠 RETURNING/OUTPUT 读键，本家没有回填语句
/// 也没有批量通道。IdentityInsertToggle 返回 null，与另几家同一条路。</summary>
public sealed class TDengineKeys : IKeyStrategy
{
    /// <summary>抛，而不是发一条"看着像在回填"的语句。表侧列名在这里点出来，因为现场读到这条错的人
    /// 下一步一定是"我这颗键为什么没回来"，那一句要能自己答。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
        => throw new QuickSqlException($"TDengine 没有可作为结果集发出的 RETURNING/OUTPUT，而本次请求要回填（{Columns(returning)}）。"
            + "写入那一路的官方语句清单里没有 RETURNING/OUTPUT 任何一支【文档·TDengine】。"
            + "本家也没有由库递增的列：主键就是那颗时间戳（首列 TIMESTAMP 由系统自动设为主键），"
            + "值请调用方自己生成、提前落值，插入就按普通列走。");

    /// <summary>false：回读句的前提"由库生成的自增值"在本家没有对象。</summary>
    public bool SupportsPostStatementKeyRead => false;

    /// <summary>false：没有自增列，"往自增列写显式值"那一问没有对象。</summary>
    public bool SupportsExplicitIdentityInsert => false;

    public bool SupportsBulkKeyRetrieval => false;

    /// <summary>没有开关，这一口刻意留在接口默认（Core 今天没有调用点）。
    /// 返回 null 让 Core 走"这条语句不需要前置/后置语句"那一支。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => null;

    /// <summary>开关 false 的另一半：走到这一口即抛（虚口保真——只翻开关、不带文本，
    /// 会得到一颗更靠里的抛）。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key)
        => throw new QuickSqlException(
            "TDengine 没有由库生成的自增值，回读句没有形态可发"
            + "（SupportsPostStatementKeyRead 为 false，走到这里说明那颗开关被绕过了）。");

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
