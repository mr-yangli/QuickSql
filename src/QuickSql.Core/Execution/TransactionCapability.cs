namespace QuickSql.Execution;

using QuickSql.Providers;

/// <summary>「事务可选」那颗位的唯一取法。DbCapabilities.Transactions 是真分派位，判词必须只有一种写法：
/// Declared。公开的 BeginTransaction 管拒绝的那一半——没有位就抛 NotSupportedException 带明话，严禁静默假事务；
/// 内核三条写路径（SaveChanges ∶ InsertOrUpdate ∶ 批量链的执行段）管放行的那一半——没有位就不包事务，
/// 逐句按库的 autocommit 直发。行为诚实条款：没有这一位时，多条语句不原子，中途失败已发的不回滚；
/// 单条语句自身的事务性按各家库的文档记账。同一段落也住在位注释与用法册，改判必须三处一起动。</summary>
internal static class TransactionCapability
{
    /// <summary>声明了 DbCapabilities.Transactions 才允许内核开一条真的 DbTransaction；
    /// 一次按位与，没有 IO、不碰驱动（TryEnsureLoaded 那一道在它之后才轮得到跑）。</summary>
    internal static bool Declared(IDbProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return (provider.Capabilities & DbCapabilities.Transactions) != 0;
    }
}
