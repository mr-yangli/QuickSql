using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的变更守卫，形状落在三段这一边：本家有 IS DISTINCT FROM，但那是引擎 3.39
/// 才有的算子，而 DbCapabilities 里没有"库的版本"那一格（引擎内嵌、版本由应用侧的包图决定，
/// SPI 连问的入口都没有），所以不用它。Compares 恒真——类型映射只产 INTEGER/REAL/TEXT/BLOB
/// 四颗基名且全部比得动（大对象在我们真正会用的量上量过），关掉任何一颗都是凭空造一处
/// "这一列不参与变更检测"，而那是丢写。两个本家特有的读法：decimal 落 TEXT，守卫比的是
/// 不变形文本，"变了没有"逐字符判（判"变了"而多发一次写是空写方向、没有丢写面——文本相同
/// 则数值必相同）；文本比较默认逐字节（NOCASE 要显式写进列声明且只到 ASCII），而 LIKE 默认
/// 不分大小写。宁可退回逐条也不做哈希近似：本家没有内置哈希函数，也不需要。</summary>
public sealed class SqliteChangeGuard : IChangeGuard
{
    /// <summary>恒真：本家的答案是"没有要关的型"。这里不留 Incomparable 那颗集合，
    /// 因为一颗空集合会把"为什么没有"这件事读成"还没填"。</summary>
    public bool Compares(ColumnContract column) => true;

    public SqlExpression BuildDifference(
        ColumnContract column,
        SqlExpression stored,
        SqlExpression incoming
    )
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引（七家同判）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        // 不用 IS DISTINCT FROM：那句在本家量得到而它有 3.39 那道版本门槛，而 SPI 没有版本格。
        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }
}
