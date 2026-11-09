using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的 SQL 文本层。分页落在句尾且两子句独立可缺省（与 PG 血统、MySQL 血统、虚谷的
/// LIMIT…OFFSET 同一形状）；恒假/恒真落布尔字面量 false/true（Bool 是本家的原生型）；布尔谓词落
/// = true/= false（可空 Bool 列上 = false 匹配不到 NULL，三值逻辑不是本家的特性）；UPDATE/DELETE 整格拒发——
/// 本家的 UPDATE 官方形状是 ALTER TABLE … UPDATE … WHERE（异步后台 mutation），DELETE 的 lightweight DELETE
/// 同属变异档，宁可当场红也不发一条"跑完要等 mutation 队列清完"的语句；SupportsJoinedWrites 跟着写格落 false。
/// EmitReturning 转交 ClickHouseKeys.EmitReturning（那里抛）。发射形有活库出处（server 26.9.21.9 ∶ 驱动 7.14.0，
/// golden 十份里九份逐字执行过）。</summary>
public sealed class ClickHouseDialect(ClickHouseTypes types, ClickHouseKeys keys, ClickHouseValues values, ClickHouseExpressions expressions)
    : DialectBase(IdentifierPolicy.ClickHouse, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.ClickHouse;

    /// <summary>@：【实测·2026-10-20 活库】SQL 文本里的 @x 被 7.14.0 驱动按名替换收走（参数驱动侧正确名字
    /// 是"不带前缀"，与 AdoExecutor.Bind 那颗摘前缀纪律逐格对上；反证也在场：ParameterName="@x" 时
    /// 文本里的 @x 原样过网被服务端当语法错报）。</summary>
    public override char ParameterPrefix => '@';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>合并档开关跟着写格落 false：UPDATE/DELETE 两整格都抛，读侧 EntityTracker 走"一句一实体"那一支；
    /// 合并那一档在这颗开关 false 时走不到默认出口，所以 EmitJoinedSources 不覆写。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>恒假/恒真都写成布尔字面量（true/false 两颗字面量在本家原生）。
    /// 恒假那颗在共用层有消费（空集合 IN () 特化），恒真那颗 src/ 里零消费。</summary>
    public override string EmptySetPredicate => "false";

    public override string AlwaysTruePredicate => "true";

    /// <summary>= true/= false：布尔是本家的原生型，与列那一侧 Bool 同边——Boolean_Type 能力位走到谓词侧的落点。
    /// 可空 Bool 列上 = false 匹配不到 NULL（三值逻辑不是本家的特性）。</summary>
    protected override void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " = true" : " = false");

    /// <summary>本家没有句首限定词那一档：LIMIT/OFFSET 都在句尾【实测·2026-10-20 活库】
    ///（golden 三式体会句逐字执行过：LIMIT 单独、OFFSET 单独、两颗都带）。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is { } limit)
        {
            writer.Append(" LIMIT ");

            EmitExpression(limit, writer);
        }

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");

            EmitExpression(offset, writer);
        }
    }

    /// <summary>转交 ClickHouseKeys.EmitReturning（那里抛）。与 GBase 8s 同一立场：宁可当场红，
    /// 也不发一条"看着像在回填"的语句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>变异档：ClickHouse 的 UPDATE 官方形状是 ALTER TABLE … UPDATE … WHERE
    ///（文档·ClickHouse：alter 页，异步后台 mutation）。这一档不在本仓库的写路径形状内——
    /// 宁可当场红，也不发一条"跑完要等 mutation 队列清完"的语句。</summary>
    public override void Emit(in UpdateStatement update, SqlWriter writer)
        => throw new QuickSqlException(
            "ClickHouse 的 UPDATE 不在写路径上：官方形状是 ALTER TABLE … UPDATE … WHERE（后台异步 mutation），"
            + "本仓库今天不发变异语句，所以这里整格抛。");

    /// <summary>同上一格：DELETE 的官方 lightweight DELETE（DELETE FROM t WHERE …）也属变异档，整格抛。</summary>
    public override void Emit(in DeleteStatement delete, SqlWriter writer)
        => throw new QuickSqlException(
            "ClickHouse 的 DELETE 走官方 lightweight DELETE（DELETE FROM t WHERE …，"
            + "https://clickhouse.com/docs/sql-reference/statements/delete），同属变异那一档，"
            + "本仓库今天不发变异语句，所以这里整格抛。");
}
