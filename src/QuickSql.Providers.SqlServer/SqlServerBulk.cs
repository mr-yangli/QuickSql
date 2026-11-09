using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace QuickSql.Providers.SqlServer;

/// <summary>批量高速层在这一家的三档：L1 SqlBulkCopy + L3 多值语句 + L5 逐条，
/// 而 L2 数组绑定与 L4 临时表在这一家没有对应实现，所以 BulkMethod 里也没有那两个成员。
/// 分块按参数个数算而不是按行数：500 行 × 30 列在 2100 预算下是 8 块、每块 65 行。</summary>
public sealed class SqlServerBulk : IBulkStrategy
{
    readonly Assembly _driver;

    IBulkCopyChannel? _channel;

    public SqlServerBulk(DbProviderFactory driverFactory)
    {
        // 构造期一次反射都没有：只记下"工厂住在哪个程序集"，第一次真取通道时才去找装载类。
        // 惰性是有对象的——只产 SQL 的离线测试也构造 provider，那条路上不该有任何一次类型查找，
        // 而诊断跟着"调用出现在哪个方法里"走，写在构造函数里就得给所有构造者交税。
        _driver = driverFactory.GetType().Assembly;
    }

    public int ParameterBudget => 2100;

    public string Describe =>
        "L1 SqlBulkCopy（按名字映射，CheckConstraints|FireTriggers 两颗开关从驱动的枚举上读，"
        + "白名单之外的列整批退 L3）+ L3 多值 VALUES + L5 逐条；参数预算 2100（§12.3）";

    /// <summary>这一家的读数最硬：MultiStatement 是 SqlClient 分号批的公开契约，从第一天就声明。
    /// 注意 SupportsMergedStatements 答的是"这一家能不能并着发"，不是"并着发更快"——
    /// 本地实例上整行批并档反而更慢（往返太便宜，省下的换不回添上的解析）。</summary>
    public bool SupportsMergedStatements => true;

    /// <summary>没有锁，命中与落空都只查一次：装载类存在时结果进 _channel，之后全是字段读，
    /// 两个线程同时第一次取最多各查一次幂等纯查找。落空的那一种（驱动太老或已被裁剪）每次取都重查——
    /// 这条分支的下一步是退到文本层为同一批数据付 N 次往返，为它省一次类型查找没有意义。</summary>
    [RequiresUnreferencedCode(
        "取通道是一次按类型名的反射查找（SqlServerBulkCopy.TryResolve），裁剪后的驱动交回 null，阶梯因此从 L3 起。"
    )]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public IBulkCopyChannel? ResolveCopyChannel() =>
        _channel ??= SqlServerBulkCopy.TryResolve(_driver);
}
