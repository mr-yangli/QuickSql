using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;

namespace QuickSql.TestInfra;

/// <summary>时刻那一族的靶实体：三格各占一种"列的形状"。
/// <list type="bullet">
/// <item><see cref="Bare"/> 走本家的默认映射 ⇒ <c>TIMESTAMP(3)</c>（不带时区）。</item>
/// <item><see cref="DeclaredOffset"/> 是同一颗 CLR <c>DateTime</c> 而列<em>声明</em>成带时区那一型 ——
/// 这一格存在的意义是"绑定侧不许按 CLR 型猜列的形状"。</item>
/// <item><see cref="Instant"/> 是 CLR 侧就带偏移的那一形 ⇒ <c>TIMESTAMPTZ(3)</c>。</item>
/// </list>
/// 三格合起来才量得动写侧那一句：同一个 <c>DateTimeKind</c> 落在不同形状的列上，答案不一样。</summary>
[Table("QuickSqlMomentRow")]
public class MomentRow
{
    public long Id { get; set; }

    public DateTime Bare { get; set; }

    [Column(TypeName = "timestamptz")]
    public DateTime DeclaredOffset { get; set; }

    public DateTimeOffset Instant { get; set; }
}
