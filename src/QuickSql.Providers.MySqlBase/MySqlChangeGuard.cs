using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的变更守卫：可空列走三段式。这一家其实有 IS DISTINCT FROM（8.0.29 起），
/// 也有 <=>（NULL 安全等号，三值逻辑下两侧同空算相等），取三段的唯一原因是共用层两样都发不出：
/// 加 <=> 是接口契约的事（不在 provider 单方权限内），IS DISTINCT FROM 又要按版本发而 SPI 没有"库的版本"一格
/// ——三段是唯一不需要知道版本就能发的形状。可空性判法与 T-SQL/Oracle 同判（NOT NULL 列一句 <> 就够），
/// 同样宁可退回逐条也不哈希近似（撞哈希的行被判"没变"而丢写）。
/// Compares 只关 json 一型：不是确定不能用，是 <> 在它上面的行为问不出来，且今天的类型映射根本产不出 json 列，是护栏。</summary>
public sealed class MySqlChangeGuard : IChangeGuard
{
    /// <summary>问不出答案的型只有 json 一颗，而这颗今天产不出来（类型映射无 json 列）。与第三家那四颗 LOB 对照：
    /// 一边是厂商明文的限制、一边是问不出答案；两家的生产后果今天都为零——都被方言闸门挡住了。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "json" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，多出来的两段 OR 只会让谓词更难走索引。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：守卫在发射路径上调用，那一趟不保证 ApplyStorageDefaults 已跑完。
        // 切在第一个 '(' 或空格之前：TIME(3) 与 DECIMAL(20,0) 都要落到基名上。
        var name = MySqlTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
