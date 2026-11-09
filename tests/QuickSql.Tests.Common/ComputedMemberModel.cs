using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;

namespace QuickSql.TestInfra;

/// <summary>靶子：<b>只读计算属性不许成列</b>。这颗实体是消费侧那颗真红抄来的形状 ——
/// ABP/DDD 的聚合上普遍挂着 <c>public bool NeedsGrading =&gt; Type == … &amp;&amp; Score == null;</c>
/// 一类"从已存的列算出来的"成员：它有公共 getter、类型又可映射（<c>bool</c>），
/// 旧那一版因此把它当成一列收进契约，于是<em>读取器</em>建到它就在
/// <c>Expression.Assign</c> 上抛 <c>ArgumentException: Expression must be writeable</c> ——
/// 症状不是"这一列读不出来"，而是整个列表接口 500（现场是题库与考试记录两颗列表）。
///
/// <para><see cref="Guarded"/> 是这一颗唯一能钉住"摘的是没有写口的那一格"的对照：
/// <c>{ get; protected set; }</c> 与 <c>{ get; private set; }</c> 在表达式树里都落得回值，
/// 若判据写成"设置器公不公开"，这两列会一起消失，而那一刀砍的是整族 ABP 实体的键与明细。</para></summary>
[Table("QuickSqlComputedMemberRow")]
public class ComputedMemberRow
{
    public long Id { get; set; }

    public int Type { get; set; }

    public string Name { get; set; } = "";

    /// <summary>没有写口的计算属性 ⇒ 不是列。</summary>
    public bool RequiresManualGrading => Type == 7;

    /// <summary>另一形计算属性：返回可空值类型也不许成列。</summary>
    public int? Doubled => Type * 2;

    /// <summary>受保护写口：落得回值，必须仍然是一列。</summary>
    public string Guarded { get; protected set; } = "";

    /// <summary>私有写口：同上。</summary>
    public string Secretive { get; private set; } = "";

    /// <summary><c>init</c> 那一形也落得回值（编译器管着不许事后赋值，运行时管不着），仍是一列。</summary>
    public int Seeded { get; init; }
}
