using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace QuickSql.Providers;

/// <summary>把一颗可寻址的 leaf provider 类登记在它的 DbKind 上，供编译期生成器
/// 在消费方汇编里发现并登记。只在带 (DbProviderFactory) 公开构造器、且实现 IDbProvider 的具体类上生效；
/// 抽象类与不满足形状的类型生成器会跳过。属性本身不改变运行期分派：真正的表由生成器在消费方
/// 产出，未挂生成器时这颗属性对运行时零影响。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class QuickSqlProviderAttribute(DbKind kind) : Attribute
{
    public DbKind Kind { get; } = kind;
}

/// <summary>编译期发现、运行期查表的 DbKind 到 provider 构造登记。
/// 由源生成器在每一份引用了 provider 程序集且挂了生成器作 Analyzer 的编译单元里
/// 产出一段 [ModuleInitializer] 引导（每颗 provider 一行 Register），
/// 每一行都是静态 new 的委托、无名字反射，因此 AOT/trim 安全。
/// QuickSqlOrm.Create 从这里读表。
/// 同一颗 DbKind 重复登记以后写为准（幂等）。</summary>
public static class ProviderRegistry
{
    sealed record Entry(string DriverKey, Func<DbProviderFactory, IDbProvider> Build);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<DbKind, Entry> Map = new();

    /// <summary>登记一颗 provider。driverKey 是它在内建表 BuiltinDrivers
    /// 与 DriverRegistry 里的那颗字符串键，用于
    /// QuickSqlOrm.Create(DbKind, string) 走内建表解析时找出工厂。</summary>
    public static void Register(DbKind kind, string driverKey, Func<DbProviderFactory, IDbProvider> build)
    {
        ArgumentNullException.ThrowIfNull(driverKey);
        ArgumentNullException.ThrowIfNull(build);
        Map[kind] = new Entry(driverKey, build);
    }

    public static bool Contains(DbKind kind) => Map.ContainsKey(kind);

    /// <summary>取这颗 DbKind 的字符串驱动键；未登记返回 null。
    /// 给 QuickSqlOrm.Create(DbKind, string) 用：先按 Kind 反查键、再让 DriverRegistry 拿工厂。</summary>
    public static string? DriverKeyOf(DbKind kind)
        => Map.TryGetValue(kind, out var entry) ? entry.DriverKey : null;

    public static bool TryCreate(DbKind kind, DbProviderFactory factory, [NotNullWhen(true)] out IDbProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Map.TryGetValue(kind, out var entry))
        {
            provider = entry.Build(factory);
            return true;
        }
        provider = null;
        return false;
    }

    /// <summary>当前已登记的 DbKind 集合，用于错误消息与测试盘点。顺序不保证。</summary>
    public static IEnumerable<DbKind> Registered => Map.Keys;
}
