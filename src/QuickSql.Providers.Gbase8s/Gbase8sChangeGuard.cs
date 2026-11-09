using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s 的变更守卫：三段式。理由与前面四家又不同条——读到的语句清单里没有 IS DISTINCT FROM，
/// 也没有 NULL 安全等号，没有"一句而不用赌版本"的候选（负向理由；布尔是不是一等值没问过，
/// 所以这是少数"换一台库就可能改判"的档位）。可空性这一档与五族同判：NOT NULL 列一句不等号就够；
/// 合并语句本家根本不发（没有 UPDATE … FROM），守卫的 false 由方言闸门代跑。
/// Compares 关 CLOB/BLOB/BYTE 三颗："问不出答案"（文档没写、产物没有、连活库都没有）；
/// byte[] → BLOB 与无宽度 string → CLOB 都是默认路径，这一位为 false 会在真实实体上被读到，
/// 后果是"这一列不参与变更检测"；BYTE 是纯护栏（本仓库不发它，只接住别人建的表）。
/// 本家 TEXT 与 LVARCHAR 同族带上限（可比），方向与虚谷那颗一致而出处薄一层（只有手册没有产物）。
/// 与五家同一立场：宁可退回逐条也不哈希近似——撞了哈希的行会被判"没变"而静默丢写。</summary>
public sealed class Gbase8sChangeGuard : IChangeGuard
{
    /// <summary>问不出答案的型：两颗大对象加同族的 BYTE（GB 量级）。它们带不出比较所需的宽度语义，
    /// 不等号在这种量上要么被库挡下、要么把整值读出来比——两种都不是这一句该干的事。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "clob", "blob", "byte" }
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
        // 切在第一个 '(' 或空格之前：本家有带空格的型名（DATETIME YEAR TO FRACTION(3)），
        // 三颗大对象既不带括号也不带空格，手写的 "CLOB …" 也要落得下来。
        var name = Gbase8sTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
