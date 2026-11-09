using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的变更守卫：三段式。这一家没有 IS DISTINCT FROM，也没有 NULL 安全等号——三段式不是选择而是唯一发得出的形状
/// （与"布尔不是一等值"的 T-SQL/Oracle 血统、"要按版本发"的 MySQL 血统是三条不同理由，共用同一份 ChangeGuardShapes.ThreeWay）。
/// 可空性这一档与三族同判：NOT NULL 列一句不等号就够。
/// Compares 关 clob/blob 两颗：TEXT 在这一家是 VARCHAR 的同义字、带宽度、可比——跟着另四族"TEXT 无上限"的直觉写会多关掉一颗本可以比的列。
/// 这两颗是"问不出答案"（文档没写过、产物没发过），而 byte[] → BLOB 是默认路径，这一位一旦为 false 就会在真实实体上被读到，
/// 后果是"这一列不参与变更检测"（守卫关了一列 ⇒ 整批退回一句一实体，今天由方言闸门代跑）。
/// CHAR/VARCHAR 存入时去掉尾部空格：参数比较发生在写入之前，"库里 abc、新值 abc␣"会被判"变了"并多发一行 UPDATE——
/// 多写一行而不是丢写一行。</summary>
public sealed class XuguChangeGuard : IChangeGuard
{
    /// <summary>问不出答案的型：两颗大对象（CLOB 2GB、BLOB 无上限）。它们带不出比较所需的宽度语义，
    /// 不等号在这种量上要么被库挡下、要么把整值读出来比——两种都不是这一句该干的事。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "clob", "blob" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句不等号就够：两侧都不可能是 NULL，多出来的两段 OR 只会让谓词更难走索引。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，而守卫在发射路径上调用。
        // 切在第一个 '(' 或空格之前：TIME(3) 与 NUMERIC(18,4) 都要落到基名，两颗大对象不带限定词，
        // 手写的 "CLOB …" 也要落得下来——切分不能只认括号。
        var name = XuguTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
