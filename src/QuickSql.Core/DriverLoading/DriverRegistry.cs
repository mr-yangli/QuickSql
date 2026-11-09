using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace QuickSql.DriverLoading;

/// <summary>一条驱动声明。AssemblyName 既是 Assembly.Load 的名字，也是 drivers/&lt;assembly&gt;.dll 的探测名；
/// FactoryType 为 null 时走「扫导出类型里第一个非抽象 DbProviderFactory 子类」那条兜底。</summary>
public sealed record DriverRef(string Key, string AssemblyName, string? FactoryType);

/// <summary>磁盘上指定的驱动装在独立上下文里：国产库驱动常自带一份旧版 System.Buffers /
/// Microsoft.Extensions.*，混进默认上下文会变成"装不上且报不出原因"。这里刻意不覆写 Load——
/// 只有 LoadFromAssemblyPath 装进来的那一个程序集落在本上下文，它的依赖仍回退默认上下文，免得把 BCL 复制一份。</summary>
sealed class DriverLoadContext(string key) : AssemblyLoadContext(key);

/// <summary>驱动的装载与工厂解析。反射只发生在这里、且只在第一次取用某条驱动时：拿到 DbProviderFactory
/// 之后，连接/命令/参数一律是虚调用（「加载可以反射，调用不行」）。名字来源只有一处：BuiltinDrivers
/// 那张内建表——现场要换一颗程序集名，把厂商 DLL 按内建表的名字丢进 drivers/；
/// 要彻底绕开按名装载，走 Register 把工厂直接交进来。</summary>
public sealed class DriverRegistry(string? root = null)
{
    readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public string Root { get; } = root ?? AppContext.BaseDirectory;

    /// <summary>注入一个已经在进程里的工厂：测试与「宿主自己就引用了驱动包」的场景用，
    /// 不构成绕过 —— 它同样是运行期取用，编译期没有任何驱动引用。</summary>
    public void Register(string key, DbProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
            _cache[key] = new CacheEntry(factory, null);
    }

    /// <summary>取驱动工厂。key 用 DbKind 的小写名（sqlserver、dameng），与 BuiltinDrivers 里的 key 一一对应；
    /// 表里没有这一行才抛 DriverNotAvailableException，所以宿主什么都不配也能只报库种与串就装配起来（零配置入口）。
    /// 需要反射的原因有两个，都写在这一层而不下沉：装载按程序集名，以及工厂的 Instance 是反射取的字段。
    /// AOT 现场的干净路径是 Register：宿主自己引用驱动包，把工厂交进来，编译期仍然没有驱动引用。</summary>
    [RequiresUnreferencedCode("按内建表里的程序集名与类型名解析驱动工厂，被裁剪的类型会在这里报驱动不存在。")]
    public DbProviderFactory Factory(string key)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Failure is null)
                    return cached.Factory!;

                throw new DriverNotAvailableException(key, cached.Failure);
            }
        }

        var reference = BuiltinDrivers.Find(key);

        if (reference is null)
            throw Fail(key, inner: null, Hint(key));

        DbProviderFactory factory;

        try
        {
            factory = Resolve(reference, Root);
        }
        catch (Exception ex)
        {
            // 失败也缓存：否则每次执行都要重走一遍抛异常的加载路径，故障现场的日志会被同一条堆栈刷满。
            throw Fail(key, ex, $"程序集 {reference.AssemblyName}，内建默认，探测 {Path.Combine(Root, "drivers")}");
        }

        lock (_gate)
            _cache[key] = new CacheEntry(factory, null);

        return factory;
    }

    [RequiresUnreferencedCode("转调 Factory，反射解析驱动工厂。")]
    public bool TryFactory(string key, [NotNullWhen(true)] out DbProviderFactory? factory, out string? failure)
    {
        try
        {
            factory = Factory(key);
            failure = null;
            return true;
        }
        catch (DriverNotAvailableException ex)
        {
            factory = null;
            failure = ex.Message;
            return false;
        }
    }

    [RequiresUnreferencedCode("按类型名在运行时程序集里找 DbProviderFactory，裁剪后可能找不到。")]
    static DbProviderFactory Resolve(DriverRef reference, string root)
    {
        var assembly = LoadAssembly(reference, root);

        if (reference.FactoryType is { Length: > 0 } declared)
            return FromType(assembly.GetType(declared, throwOnError: true)!);

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsAbstract && typeof(DbProviderFactory).IsAssignableFrom(type))
                return FromType(type);
        }

        // 走到这里说明扫遍导出类型一颗工厂都没有 —— 而内建表之外没有任何声明入口，
        // 现场能做的只有换 DLL 或走 Register 注入，两句话都写在消息里。
        throw new QuickSqlException($"{assembly.GetName().Name} 里没有可实例化的 DbProviderFactory 子类；确认 drivers/ 里那颗 DLL 是这家驱动的托管程序集，或改用 DriverRegistry.Register 注入工厂");
    }

    [RequiresUnreferencedCode("LoadFromAssemblyPath 拿到的程序集不在裁剪分析的引用图里。")]
    static Assembly LoadAssembly(DriverRef reference, string root)
    {
        // 只探 drivers/ 这一层，不探输出根目录：那里能找到的公开包默认上下文本来就能加载，
        // 复制进独立上下文只会让同一类型出现两份。
        var file = Path.Combine(root, "drivers", reference.AssemblyName + ".dll");

        if (File.Exists(file))
            return new DriverLoadContext(reference.Key).LoadFromAssemblyPath(file);

        return Assembly.Load(new AssemblyName(reference.AssemblyName));
    }

    /// <summary>驱动工厂都是单例：优先读它自己声明的 <c>Instance</c>，其次无参构造。两条都只在加载期走一次。</summary>
    [RequiresUnreferencedCode("读驱动工厂自己的 Instance 静态字段，或反射实例化它。")]
    static DbProviderFactory FromType(Type factoryType)
        => (factoryType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DbProviderFactory)
           ?? (Activator.CreateInstance(factoryType) as DbProviderFactory)
           ?? throw new QuickSqlException($"驱动工厂 {factoryType.FullName} 既无 Instance 也无法实例化");

    string Hint(string key)
        => $"内建表里没有 {key}。两条路：① 驱动 DLL 丢进 <输出目录>/drivers/（文件名按内建表的程序集名）；" +
           "② 宿主自己引用驱动包，把 DbProviderFactory 交给 DriverRegistry.Register(key, factory)（AOT 干净路径）。";

    DriverNotAvailableException Fail(string key, Exception? inner, string detail)
    {
        var message = inner is null ? detail : inner.Message + "；" + detail;

        lock (_gate)
            _cache[key] = new CacheEntry(null, message);

        return new DriverNotAvailableException(key, message, inner);
    }

    sealed record CacheEntry(DbProviderFactory? Factory, string? Failure);
}
