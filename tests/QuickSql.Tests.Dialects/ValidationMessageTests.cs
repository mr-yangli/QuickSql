using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>校验闸门那一句摘要（§10.6）在多项违例时归并成什么形状。
///
/// <para><b>为什么这一格值得单钉</b>：被拒的时候语句还没生成，库里什么都没发生，能定位的只有契约那几条尺 ——
/// 于是"报得清不清楚"全在消息文本上，而文本的读法是裁决出来的（按「成员 + 规则」而不是按行，
/// 见 <c>ValidationFailedException.BuildMessage</c>）。真实的形状是一次 134 行的种子报出 656 项，
/// 按行取前几项会把槽位全花在同一列的连续几行上。</para>
///
/// <para>写路径怎么把明细喂进来由 <c>SqlServerInsertTests</c> 那两颗钉，这一册只管聚合本身。</para></summary>
public sealed class ValidationMessageTests
{
    const string Head = "134 行插入被拒绝：656 项违反库契约（ValidationMode.ContractOnly）。整批先校验后执行，一行都没写进库。";

    [Fact]
    public void Many_rows_broken_on_the_same_rule_collapse_into_one_line()
    {
        var failures = new List<ValidationFailure>();

        for (var i = 1; i <= 5; i++)
            failures.Add(new ValidationFailure("OutLink", "required", $"[第 {i} 行] OutLink 不可为空（列 OutLink，来源 Column）"));

        failures.Add(new ValidationFailure("Name", "length", "[第 6 行] Name 超出列 Name 的 64 字符"));

        var ex = Raise(failures);

        Assert.StartsWith(Head, ex.Message, StringComparison.Ordinal);
        Checks.Has("按「成员 + 规则」归并（共 2 组）：", ex.Message);
        Checks.Has("OutLink / required：5 项", ex.Message);
        Checks.Has("Name / length：1 项", ex.Message);

        // 项数多的排前面，而每一组只留一条原文当样本：行号在明细里，一处不丢。
        Assert.True(
            ex.Message.IndexOf("OutLink / required", StringComparison.Ordinal)
            < ex.Message.IndexOf("Name / length", StringComparison.Ordinal));
        Checks.Has("例：[第 1 行] OutLink 不可为空", ex.Message);
        Checks.Absent("[第 2 行]", ex.Message);
        Assert.Equal(6, ex.Failures.Count);
    }

    [Fact]
    public void A_Lone_failure_stays_at_the_head_line()
    {
        var ex = Raise([new ValidationFailure("Note", "required", "Note 不可为空（列 Note，来源 Column）")]);

        // 抬头本来就点到成员与列，再来一份只有一行的清单只是重说一遍。
        Assert.Equal(Head, ex.Message);
    }

    [Fact]
    public void Beyond_the_limit_the_count_stays_whole_while_the_list_stops()
    {
        var failures = new List<ValidationFailure>();

        for (var i = 1; i <= ValidationFailedException.GroupLimit + 1; i++)
            failures.Add(new ValidationFailure($"M{i}", "required", $"M{i} 不可为空（列 C{i}，来源 Column）"));

        var ex = Raise(failures);

        Checks.Has($"共 {failures.Count} 组，列出前 {ValidationFailedException.GroupLimit} 组，其余见 Failures", ex.Message);
        Assert.Equal(ValidationFailedException.GroupLimit, Checks.Occurrences(ex.Message, "项 —— 例："));
        Checks.Absent($"  {failures.Count}. ", ex.Message);
        Assert.Equal(failures.Count, ex.Failures.Count);
    }

    static ValidationFailedException Raise(IReadOnlyList<ValidationFailure> failures)
        => Assert.Throws<ValidationFailedException>(() => Throw(failures));

    // 这一格验的是消息文本，所以走一条同步的抛出；异步的写路径由 SqlServerInsertTests 那两颗钉。
    static void Throw(IReadOnlyList<ValidationFailure> failures) => throw new ValidationFailedException(Head, failures);
}
