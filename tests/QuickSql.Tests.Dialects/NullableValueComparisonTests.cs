using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>可空值<em>直接</em>比较（不取 <c>.Value</c>）不该落进 SQL 一层转换（v0.39 缺陷⑫的收口，2026-10-09）。
///
/// <para>形状来源是 C# 的提升比较：<c>枚举列 == 枚举? 量</c> 在树上长的是
/// <c>Convert(EStatus? → Int32?)</c>，两侧各裹一层 <c>Nullable</c> 就同时躲开 <c>IsEnum</c> 与
/// <c>IsSameColumnar</c>；<c>int 列 == short? 量</c> 长的是 <c>Convert(Int16? → Int32?)</c>，
/// <c>int 列 == long? 量</c> 更把 CAST 贴到<em>列</em>那一侧。原先这一格落到
/// <c>StoreTypeOf</c>，而它交回的是 <b>DDL 建表型名</b>（MySQL 给 <c>INT</c>），
/// 于是发出去的是 <c>CAST(@p0 AS INT)</c> —— MySQL 的 CAST 不认 <c>INT</c>，整条句子被库拒
/// （真库见证：1064 near 'INT)'）。豁免判据因此是"看穿一层 <c>Nullable</c> 的枚举" ∪ "整型安全加宽"。</para>
///
/// <para><b>两条边界是裁定，不是实现顺手取的</b>：① 判据<em>只放宽、不收紧</em> —— 曾写成"枚举只允许
/// 转到自己的 <c>Enum.GetUnderlyingType</c>"，短血统枚举（<see cref="ShortState"/>）那一形反而给列也套上
/// CAST，因为提升比较转到的是 <c>Int32?</c> 而不是 <c>Int16?</c>，比原文更坏；② 整型只豁免<em>加宽</em>，
/// 收窄（<c>Int32 → Int16</c>）会真丢值、<c>char</c> 的列是 CHAR(1) 存文本（拿数值比字符是库级静默
/// 字符串↔数字降级），这两族都<b>留着 CAST 让它响</b>，见 <see cref="Real_conversions_still_emit_a_cast"/>。
/// 至于 CAST 的<em>目标型名</em>取自 DDL 那一张表（<c>(decimal)</c> → <c>DECIMAL(18,4)</c> 恰好能用，
/// <c>SMALLINT</c> 不能）是另一格欠，规格里今天还没有它的名字（<c>StoreTypeOf</c> 在台账里命中 0），不在本文件射程内。</para></summary>
public sealed class NullableValueComparisonTests
{
    [Fact]
    public void Nullable_enum_compared_directly_binds_the_parameter_bare()
    {
        OrderState? state = OrderState.Paid;
        var sql = MySql.Orm().Select<Order>().Where(o => o.State == state).ToSql();

        Assert.Contains("`t0`.`State` = @p0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_backed_enum_and_wider_integer_values_all_bind_bare()
    {
        ShortState? level = ShortState.High;
        OrderState? state = OrderState.Paid;
        short? small = 7;
        long? wide = 7L;
        var orm = MySql.Orm();

        // 短血统枚举：判据看穿的是 Nullable，不是"枚举必须转到自己的底层型"。
        Assert.DoesNotContain("CAST(", orm.Select<RepresentationProbe>().Where(p => p.Level == level).ToSql(), StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(", orm.Select<RepresentationProbe>().Where(p => p.State == state).ToSql(), StringComparison.Ordinal);

        // 整型加宽：量的那一侧与列的那一侧都不该出现 CAST。
        Assert.Contains("`t0`.`Id` = @p0", orm.Select<RepresentationProbe>().Where(p => p.Id == small).ToSql(), StringComparison.Ordinal);
        Assert.Contains("`t0`.`Big` = @p0", orm.Select<RepresentationProbe>().Where(p => p.Big == small).ToSql(), StringComparison.Ordinal);
        Assert.Contains("`t0`.`Id` = @p0", orm.Select<RepresentationProbe>().Where(p => p.Id == wide).ToSql(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_integer_widening_on_the_column_side_loses_its_cast_and_keeps_the_index()
    {
        // 提升比较会把 int 列升到 Int64：那一颗 CAST 语义上从不需要，而留着它就把列包进函数、索引作废。
        var sql = MySql.Orm().Select<RepresentationProbe>().Where(p => (long)p.Id > 5L).ToSql();

        Assert.DoesNotContain("CAST(", sql, StringComparison.Ordinal);
        Assert.Contains("`t0`.`Id` >", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_valued_nullable_enum_folds_the_equality_into_is_null()
    {
        // 豁免之后 :349 那颗"右侧为 null 就折 IS NULL"的折法够得着了 —— 这一格是语义问题不是观感问题。
        OrderState? none = null;
        var sql = MySql.Orm().Select<Order>().Where(o => o.State == none).ToSql();

        Assert.Contains("`t0`.`State` IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_conversions_still_emit_a_cast()
    {
        // 反向探针：收窄与 char 都<em>不</em>豁免。丢掉这两颗判据会把"值真的变了"伪装成"只是表示不同"，
        // 那是静默错行，比一句被库拒的 CAST 更坏。
        var orm = MySql.Orm();

        Assert.Contains("CAST(`t0`.`Id` AS DECIMAL(18,4))", orm.Select<Order>().Where(o => (decimal)o.Id == 2m).ToSql(), StringComparison.Ordinal);
        Assert.Contains("CAST(`t0`.`Id` AS SMALLINT)", orm.Select<RepresentationProbe>().Where(p => (short)p.Id == 3).ToSql(), StringComparison.Ordinal);

        char? sign = '甲';
        Assert.Contains("CAST(`t0`.`Sign` AS INT)", orm.Select<RepresentationProbe>().Where(p => p.Sign == sign).ToSql(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sql_server_family_shares_the_exemption_word_for_word_modulo_quoting()
    {
        OrderState? state = OrderState.Paid;
        var sql = SqlServer.Orm().Select<Order>().Where(o => o.State == state).ToSql();

        Assert.Contains("[t0].[State] = @p0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(", sql, StringComparison.Ordinal);
    }
}

/// <summary>只有列、没有关系的探针表：整数、长整数、两种血统的枚举、一颗 char。
/// 放在本文件而不是 Common，为的是不动 <c>Order</c> 的列清单（黄金册里每一张 SELECT 列表都挂着它）。</summary>
[Table("QuickSqlRepresentationProbe")]
public sealed class RepresentationProbe
{
    public int Id { get; set; }

    public long Big { get; set; }

    public OrderState State { get; set; }

    public ShortState Level { get; set; }

    public char Sign { get; set; }
}

/// <summary>短血统枚举：豁免判据踩错一步（按 <c>Enum.GetUnderlyingType</c> 死配）就会让它当场回退。</summary>
public enum ShortState : short
{
    Low = 1,
    High = 2,
}
