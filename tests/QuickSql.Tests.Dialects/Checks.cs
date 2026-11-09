using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>断言辅助：失败消息里必须带<b>整条产出的 SQL</b>。
/// 方言文本的问题几乎都不是"少了一个字符"而是"版式整个不对"，只报 needle 会让人去猜上下文。</summary>
static class Checks
{
    public static void Has(string needle, string sql)
        => Assert.True(sql.Contains(needle, StringComparison.Ordinal), $"产出的 SQL 里没有 {needle}：{Environment.NewLine}{sql}");

    public static void Absent(string needle, string sql)
        => Assert.False(sql.Contains(needle, StringComparison.Ordinal), $"产出的 SQL 里不该有 {needle}：{Environment.NewLine}{sql}");

    /// <summary>取回异常本体而不是"抛了就通过"：这些护栏的价值一半在消息有没有说清楚该改哪里（§5.8）。
    /// 参数用 <see cref="Func{TResult}"/> 而非 <c>Action</c>，是为了让 <c>() => q.ToSql()</c> 这种
    /// 有返回值的表达式只有一个候选重载 —— 否则 <c>Assert.Throws</c> 在这里会报二义。</summary>
    public static T Raised<T>(Func<object?> evaluate) where T : Exception
    {
        var failure = Record.Exception(() => _ = evaluate());

        Assert.IsType<T>(failure);

        return (T) failure!;
    }

    /// <summary>SELECT 与 FROM 之间的投影列。写个真解析器是过度设计，但断言方必须拿到"逐列的列表"
    /// 而不是数量 —— 列序本身就是被钉住的东西（§10.3）。</summary>
    public static IReadOnlyList<string> Columns(string sql)
    {
        var start = sql.StartsWith("SELECT DISTINCT ", StringComparison.Ordinal) ? "SELECT DISTINCT ".Length : "SELECT ".Length;
        var end = sql.IndexOf(" FROM ", StringComparison.Ordinal);

        Assert.True(end > start, $"这条 SQL 里找不到 FROM：{sql}");

        return sql[start..end].Split(", ", StringSplitOptions.RemoveEmptyEntries);
    }

    public static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
