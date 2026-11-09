namespace QuickSql.Transactions;

/// <summary>形态 (2) 的返回值：不抛异常、返回码。
/// 没有 ErrorCode 槽位：DbException 这一层没有与库无关的错误码可读（各家错误码都在派生类型上），
/// 要做就得先给驱动 SPI 加一个 DescribeError。与其塞一个恒为 null 的槽位，不如先记成待决项：
/// 真出现"业务要按错误码分支"的需求时连同 SPI 一起做。错误原文已经在 Exception 里，
/// 分支可以先按"Exception 是某家驱动异常"由调用方自己做。</summary>
public record TransactionResult
{
    public required bool Success { get; init; }

    /// <summary>失败时是异常原文（含内层消息），成功时为 null。给日志用，不参与分支。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>失败时的事务内首个异常。已经吞掉了，不再向上抛——形态 (2) 的承诺就是"不抛异常"。</summary>
    public Exception? Exception { get; init; }

    /// <summary>回滚那一步自己的失败原文；成功路径与回滚顺利时为 null。
    /// 业务异常与回滚失败不是同一场事故的两面，各有各的原文：业务异常永远走 Exception，
    /// 回滚失败走这一条，两条都在时两个都看得到。</summary>
    public Exception? RollbackFailure { get; init; }

    internal static TransactionResult Ok() => new() { Success = true };

    internal static TransactionResult Failed(Exception exception, Exception? rollbackFailure = null)
        => new() { Success = false, ErrorMessage = Text(exception), Exception = exception, RollbackFailure = rollbackFailure };

    internal static TransactionResult<T> Ok<T>(T value)
        => new() { Success = true, Value = value };

    internal static TransactionResult<T> Failed<T>(Exception exception, Exception? rollbackFailure = null)
        => new() { Success = false, Value = default, ErrorMessage = Text(exception), Exception = exception, RollbackFailure = rollbackFailure };

    /// <summary>取最内层消息：DbException 常被 TargetInvocationException 一类壳包着，只报外层就等于没报。</summary>
    static string Text(Exception exception)
    {
        var inner = exception;

        while (inner.InnerException is { } next)
            inner = next;

        return inner.Message;
    }
}

/// <summary>带业务返回值的形态 (2)。Value 只在 Success 为真时有意义：
/// 回滚之后的 Value 是 default，不是"业务算出来的那个数"。</summary>
public sealed record TransactionResult<T> : TransactionResult
{
    public T? Value { get; init; }
}
