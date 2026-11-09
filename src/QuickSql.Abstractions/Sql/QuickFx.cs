namespace QuickSql.Sql;

/// <summary>查询侧的方便口。
/// 一颗都没有 C# 实现：只在查询表达式树里有意义，由内核逐颗翻译成 SQL，直接在代码里调用得到
/// NotSupportedException（每一颗方法体的原话都是指路的：先 ToList()/ToListAsync() 拿回行，再用普通算术比）。
/// 两族分工：日期三口是谓词形状（返回 bool，接 Where），译文只含比较运算与 AND、不落任何方言日期函数，
/// 所以各家一张模板都不用登记；行号四颗是投影形状（返回 int，只接 Select），发 ROW_NUMBER() OVER。
/// 窗口函数落在 WHERE 或 GROUP BY 上在任何一家都是不认的句子，那一格由内核在组合时当场拒，不等库报错。</summary>
public static class QuickFx
{
    /// <summary>日期范围：把一列时间（或一列日期）按天圈进 [beginDate, endDate]——两端都含。
    /// 发出的谓词是半开式（date 大于等于 beginDate 且小于 endDate 加一天）：上界的"加一天"不用调用方记口径，
    /// 内核在翻译时把两个边界求值并在参数侧算出下一天，列上不挂任何表达式，索引照走。
    /// 两个约束：两颗边界必须能当场读出值（局部变量、闭包、内联常量都算数；拿查询行上的成员当边界
    /// 就得落方言日期算术，当场拒并指路，那档请用 DateIntersection 或手写比较）；endDate 传当天即可，
    /// 不用调用方自己加一。beginDate 晚于 endDate 是合法的空段（区间的空集），照发不拦；
    /// endDate 传 DateOnly.MaxValue 时上界那半边不发（它没有下一天）；date 列可空时 NULL 行不中，
    /// 与手写比较是同一句 SQL 比较语义。</summary>
    public static bool DateRange(object date, DateOnly beginDate, DateOnly endDate)
        => throw new NotSupportedException(
            "QuickFx.DateRange 只活在查询表达式树里（会被翻译成 SQL 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里过滤，先 ToList()/ToListAsync() 落到内存，再用日期算术比较。");

    /// <summary>日期交集：实体存的一段 [beginDate, endDate]（闭端）与用户选的一段
    /// [beginDate2, endDate2]（闭端）有没有同一天。发出的谓词是
    /// beginDate 小于等于 endDate2 且 endDate 大于等于 beginDate2——两段相触的那一天算相交
    /// （闭端区间的数学）。上界与下界都是表达树里的自由实参（列、闭包、行成员都行），不计边界算术。</summary>
    public static bool DateIntersection(DateOnly beginDate, DateOnly endDate, DateOnly beginDate2, DateOnly endDate2)
        => throw new NotSupportedException(
            "QuickFx.DateIntersection 只活在查询表达式树里（会被翻译成 SQL 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里过滤，先 ToList()/ToListAsync() 落到内存，再按闭端区间算。");

    /// <summary>时间段乘日期段的交集：实体两端是 DateTime（闭端），用户给的是 DateOnly 日起止（闭端）。
    /// 发出的谓词与 DateIntersection 同一形状。两颗 DateOnly 实参按本家的 date 绑参
    /// （每家 Values 的 DateOnly 臂），不经列的存储型转换。</summary>
    public static bool DateTimeIntersection(DateTime beginDate, DateTime endDate, DateOnly beginDate2, DateOnly endDate2)
        => throw new NotSupportedException(
            "QuickFx.DateTimeIntersection 只活在查询表达式树里（会被翻译成 SQL 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里过滤，先 ToList()/ToListAsync() 落到内存，再按闭端区间算。");

    // ── 行号四颗（投影形状） ──────────────────────────────────────────

    /// <summary>行号：整集按 orderKey 排，第一行发 1、往下连续（ROW_NUMBER() OVER 加 ORDER BY 升序）。</summary>
    public static int RowNumber(object orderKey)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");

    /// <summary>同上，descending 决定那一列的序（true 落 DESC）。
    /// 这颗必须当场读得出值：序是句子的形状，不是每行各问一次的东西，拿行上的成员当方向当场拒。</summary>
    public static int RowNumber(object orderKey, bool descending)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");

    /// <summary>分行号：按 partitionKey 分堆，每堆各自从 1 数起
    /// （ROW_NUMBER() OVER 加 PARTITION BY 与 ORDER BY）。
    /// 分堆与排序的判据都是句子里的列，所以两侧都不许挂方言函数之外的形状。</summary>
    public static int RowNumber(object partitionKey, object orderKey)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");

    /// <summary>分行号带方向，与前两颗同一档分工：descending 读的是 orderKey
    /// 那一列的方向，partitionKey 不参与排序。</summary>
    public static int RowNumber(object partitionKey, object orderKey, bool descending)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");
}
