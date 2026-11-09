namespace QuickSql.TestInfra;

/// <summary>与 <see cref="ObliviousRow"/> 成对照的那一颗实体：整份文件在<b>可空上下文之内</b>，
/// 所以这里的 <c>string</c> 是一句真的"非空"声明，而 <c>string?</c> 是一句真的"可空"声明。
/// 必填尺子在这一族上照旧落 —— 把可空上下文之外那一格摘下来，不许顺手把这一格也摘了。</summary>
[System.ComponentModel.DataAnnotations.Schema.Table("QuickSqlSpokenRow")]
public class SpokenRow
{
    public int Id { get; set; }

    /// <summary>可空上下文里的 <c>string</c> ⇒ <c>NotNull</c>：一句话说过了，必填尺子量它。</summary>
    public string DeclaredNotNull { get; set; } = "";

    /// <summary>可空上下文里的 <c>string?</c> ⇒ 列可空，尺子不量。</summary>
    public string? DeclaredNullable { get; set; }
}
