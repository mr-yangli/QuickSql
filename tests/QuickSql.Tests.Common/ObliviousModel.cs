using System.ComponentModel.DataAnnotations.Schema;
#nullable disable
using QuickSql.Metadata;

namespace QuickSql.TestInfra;

/// <summary>住在<b>可空上下文之外</b>的实体（本文件首行 <c>#nullable disable</c>）——
/// 这正是 ABP / DDD 那一族框架代码的形状：整份实体的引用类型属性都没带可空注记，
/// 而运行期真会交出 <c>null</c>（租户号、备注、外部单号那一类）。
///
/// <para><b>这一颗靶子要判的是一句区分</b>：<c>NullabilityInfoContext</c> 对这样的成员交回的是
/// <c>Unknown</c>（"我没表过态"），不是 <c>NotNull</c>（"我说了非空"）。把它读成后者，
/// 就是拿一句谁都没说过的话去拒绝一次合法写入 —— 消费侧的现场症状是"插入被必填尺子挡下"。
/// 而列的形状不能跟着翻：现库那些列真的是 <c>NOT NULL</c>，翻方向等于批量 <c>DROP NOT NULL</c>。</para>
///
/// <para>三个成员各占一格：<see cref="Unspoken"/> 是没表过态的那一形，
/// <see cref="SpokenNullable"/> 证明"在 disable 上下文里显式写 <c>string?</c>"仍算表过态，
/// <see cref="SpokenNotNull"/> 是同一颗实体上的对照（可空上下文之外的 <c>string</c> 就是 Unknown，
/// 所以这一格要靠注解来说话）。</para></summary>
[Table("QuickSqlObliviousRow")]
public class ObliviousRow
{
    public int Id { get; set; }

    /// <summary>可空上下文之外的 <c>string</c> ⇒ <c>Unknown</c>：建表按非空列，必填尺子不量它。</summary>
    public string Unspoken { get; set; }

    /// <summary>显式 <c>string?</c>：即使在 disable 的文件里，这一段单独开回可空上下文 ——
    /// 它证明"表过态"是按<em>那一句</em>算的，而不是按整份文件算的。</summary>
#nullable enable
    public string? SpokenNullable { get; set; }
#nullable disable

    /// <summary><c>[Required]</c> 说过话 ⇒ 必填尺子照量，与上下无关。</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public string SpokenRequired { get; set; }

    /// <summary>值类型那一档不受这一颗改动影响：非空值类型永远非空。</summary>
    public int Quantity { get; set; }
}
