using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.DriverLoading;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace QuickSql;

/// <summary>注册入口的配置块。默认 Scoped：QuickSqlOrm 不持有连接（连接按语句从池取），
/// 但 FilterRegistry 是每实例一份，Scoped 下每个请求可以独立注册租户过滤而不影响其他请求；
/// 重对象（IEntitySchemaRegistry、IDbProvider）走 Singleton，编译产物缓存一次、全进程共用。</summary>
public sealed class QuickSqlOrmBuilder
{
    internal DbKind Kind { get; set; }
    internal string ConnectionString { get; set; } = "";
    internal Func<IServiceProvider, string>? ConnectionStringFactory { get; set; }
    internal DbProviderFactory? DriverFactory { get; set; }

    /// <summary>缺省集群 main 的从库清单（读写分离那一档），按给出的顺序轮询。空清单 = 这座库不分读写，
    /// 读与写落在 ConnectionString 那一条串上，也就是这一格落地之前的行为。</summary>
    public IReadOnlyList<string> Replicas { get; set; } = [];

    /// <summary>main 之外的具名集群（分库那一档）。实体用 [DataSource("名字")] 点名自己住哪一座。
    /// 这里的串都是启动期常量：按请求解析的那颗工厂只管 main 的主库串（多租户每库一串），
    /// 要让一座集群按请求换串，请为那一座另建一颗 orm，而不是在这一格里塞工厂。</summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; set; } = [];

    /// <summary>Fluent 模型配置（优先级最高的那一层）。null 表示只用 DataAnnotations 与约定。</summary>
    public ModelConfiguration? Model { get; set; }

    /// <summary>字符串列没声明宽度时的缺省宽度（与 <see cref="QuickSqlOrmOptions.DefaultStringLength"/> 同一颗）。
    /// 注册表是 Singleton，所以这一格必须在注册时定好、之后不随请求变。</summary>
    public int? DefaultStringLength { get; set; }

    /// <summary>语句超时秒数。默认 30。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>无 ORDER BY 的分页查询怎么办。默认 Throw。</summary>
    public PagingOrderingPolicy PagingOrdering { get; set; } = PagingOrderingPolicy.Throw;

    /// <summary>结构同步策略。默认 Disabled。</summary>
    public SchemaSyncPolicy SchemaSync { get; set; } = SchemaSyncPolicy.Disabled;

    /// <summary>非 Development 环境下强制执行结构同步的开关。</summary>
    public bool ForceSchemaSync { get; set; }

    /// <summary>已经在的表要不要逐列比形状（补列、改长度与标度、改可空、重发备注、补索引）。
    /// 默认 true；关掉即退到"只有缺表才建"那一档。</summary>
    public bool SchemaSyncColumns { get; set; } = true;

    /// <summary>库里多出来的那条索引要不要 DROP。默认 false（opt-in）——它与列级同步不是一个风险档：
    /// 补一列不丢数据，删一条正在被用的索引会当场拖慢查询。唯一约束即便开了也不碰。</summary>
    public bool SchemaSyncDropIndexes { get; set; }

    /// <summary>每只 Scoped 实例建好之后要跑的装配（按请求装的过滤器、审计与日志钩子、行为注册）。
    /// 为什么要有这一颗：Filters、Aop 与 Behaviors 三处都是每实例一份，而"当前登录者是哪一家企业"
    /// "这一条语句的审计值从哪个用户来"恰恰是按请求的事实，只能在实例建好之后装——在那之前没有实例可装，
    /// QuickSqlOrmOptions 那一格只收得到启动期常量。每个请求跑一次，参数就是那一只刚建好的实例
    /// （第二个参数是本请求的 scope，可以从里面取当前用户那一类宿主服务）。注册序即执行序；
    /// 它是幂等的场地由调用方负责：这一格不做去重，同一段装配挂两次就跑两次，而 Behaviors 上同一把键
    /// 注册两次是当场抛——那是刻意的，装配写重了要在启动后第一跳就响。</summary>
    public List<Action<IServiceProvider, QuickSqlOrm>> AfterBuild { get; set; } = [];

    internal QuickSqlOrmOptions BuildOptions(IDbProvider provider, string connectionString) => new()
    {
        Provider = provider,
        ConnectionString = connectionString,
        Replicas = Replicas,
        Clusters = Clusters,
        Model = Model,
        DefaultStringLength = DefaultStringLength,
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        PagingOrdering = PagingOrdering,
        SchemaSync = SchemaSync,
        ForceSchemaSync = ForceSchemaSync,
        SchemaSyncColumns = SchemaSyncColumns,
        SchemaSyncDropIndexes = SchemaSyncDropIndexes,
    };
}

/// <summary>DI 注册入口。</summary>
public static class QuickSqlServiceCollectionExtensions
{
    /// <summary>静态连接串的注册口。默认 Scoped；重对象（IEntitySchemaRegistry、IDbProvider）走 Singleton。
    /// 驱动工厂由 DriverRegistry 按 BuiltinDrivers 内建表现场解析。</summary>
    [RequiresUnreferencedCode("QuickSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("QuickSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddQuickSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        string connectionString,
        Action<QuickSqlOrmBuilder>? configure = null)
    {
        var builder = new QuickSqlOrmBuilder { Kind = kind, ConnectionString = connectionString };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>连接串按请求解析的注册口（多租户每库一串）。connectionStringFactory 在每个 Scoped 解析时
    /// 执行一次，可以从 IServiceProvider 取当前租户上下文决定走哪条串。</summary>
    [RequiresUnreferencedCode("QuickSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("QuickSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddQuickSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        Func<IServiceProvider, string> connectionStringFactory,
        Action<QuickSqlOrmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        var builder = new QuickSqlOrmBuilder { Kind = kind, ConnectionStringFactory = connectionStringFactory };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>已知工厂的注册口。跳过内建表解析，适合宿主自己引用了驱动包的场景。</summary>
    [RequiresUnreferencedCode("QuickSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("QuickSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddQuickSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        string connectionString,
        DbProviderFactory driverFactory,
        Action<QuickSqlOrmBuilder>? configure = null)
    {
        var builder = new QuickSqlOrmBuilder { Kind = kind, ConnectionString = connectionString, DriverFactory = driverFactory };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>已知工厂 + 连接串按请求解析。多租户每库一串 + 宿主自持驱动的组合。</summary>
    [RequiresUnreferencedCode("QuickSqlOrm 的 G2 兜底构造器按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("QuickSqlOrm 的 G2 兜底构造器编译读写委托。")]
    public static IServiceCollection AddQuickSqlOrm(
        this IServiceCollection services,
        DbKind kind,
        Func<IServiceProvider, string> connectionStringFactory,
        DbProviderFactory driverFactory,
        Action<QuickSqlOrmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        var builder = new QuickSqlOrmBuilder { Kind = kind, ConnectionStringFactory = connectionStringFactory, DriverFactory = driverFactory };
        configure?.Invoke(builder);

        return RegisterCore(services, builder);
    }

    /// <summary>AOT-safe 注册口（静态连接串）：元数据走 G1-only 注册表——只认源生成器编译期产出的实体事实，
    /// 未生成或被 Fluent 配置过的实体在第一次解析时抛 NotSupportedException，没有反射兜底，所以整条注册路径
    /// 不带 RUC/RDC。驱动工厂必须显式给（宿主自己引用驱动包，如 SqliteFactory.Instance）：内建表解析
    /// （DriverRegistry）按程序集名反射装载驱动，那一步是 AOT 档禁掉的形态。
    /// lookup 递生成器产出的 QuickSql.Generated.QuickSqlGeneratedMetadata.TryGet。
    /// 前提与射程见 G1OnlyEntitySchemaRegistry：实体全部走 G1、校验只开 ContractOnly/Off；
    /// DTO 投影与 Include 的反射档不在这一档的射程内（标注在各自的算子口）。</summary>
    public static IServiceCollection AddQuickSqlOrmAotSafe(
        this IServiceCollection services,
        DbKind kind,
        string connectionString,
        DbProviderFactory driverFactory,
        GeneratedEntityLookup lookup,
        Action<QuickSqlOrmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(driverFactory);
        ArgumentNullException.ThrowIfNull(lookup);

        var builder = new QuickSqlOrmBuilder { Kind = kind, ConnectionString = connectionString, DriverFactory = driverFactory };
        configure?.Invoke(builder);

        return RegisterAotSafeCore(services, builder, lookup);
    }

    /// <summary>AOT-safe 注册口（连接串按请求解析，多租户每库一串）。其余口径与静态串那一颗相同：
    /// 驱动工厂显式给、元数据走 G1-only、未生成实体当场抛。</summary>
    public static IServiceCollection AddQuickSqlOrmAotSafe(
        this IServiceCollection services,
        DbKind kind,
        Func<IServiceProvider, string> connectionStringFactory,
        DbProviderFactory driverFactory,
        GeneratedEntityLookup lookup,
        Action<QuickSqlOrmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStringFactory);
        ArgumentNullException.ThrowIfNull(driverFactory);
        ArgumentNullException.ThrowIfNull(lookup);

        var builder = new QuickSqlOrmBuilder { Kind = kind, ConnectionStringFactory = connectionStringFactory, DriverFactory = driverFactory };
        configure?.Invoke(builder);

        return RegisterAotSafeCore(services, builder, lookup);
    }

    [RequiresUnreferencedCode("注册路径含 G2 兜底构造器与 DriverRegistry 反射。")]
    [RequiresDynamicCode("注册路径含 G2 兜底构造器。")]
    static IServiceCollection RegisterCore(IServiceCollection services, QuickSqlOrmBuilder builder)
        => RegisterCommon(services, builder,
            sp =>
            {
                var factory = builder.DriverFactory ?? ResolveFactory(builder.Kind);
                if (!ProviderRegistry.TryCreate(builder.Kind, factory, out var provider))
                    throw new InvalidOperationException(
                        $"DbKind.{builder.Kind} 未在 ProviderRegistry 中登记。可能原因：① 生成器 QuickSql.Generators 未作为 Analyzer 挂进编译；② 该 provider 程序集未被引用。");
                return provider;
            },
            sp => new EntitySchemaRegistry(new ContractResolver(sp.GetRequiredService<IDbProvider>(), builder.Model,
                defaultStringLength: builder.DefaultStringLength)));

    /// <summary>AOT-safe 的登记段：provider 由显式工厂经 ProviderRegistry 的编译期登记表构造（零反射），
    /// 注册表是 G1-only。Model 仍传下去——不是用来解析，是用来拒绝：Fluent 配置过的实体在 G1-only 档当场抛。</summary>
    static IServiceCollection RegisterAotSafeCore(IServiceCollection services, QuickSqlOrmBuilder builder, GeneratedEntityLookup lookup)
        => RegisterCommon(services, builder,
            sp =>
            {
                if (!ProviderRegistry.TryCreate(builder.Kind, builder.DriverFactory!, out var provider))
                    throw new InvalidOperationException(
                        $"DbKind.{builder.Kind} 未在 ProviderRegistry 中登记。可能原因：① 生成器 QuickSql.Generators 未作为 Analyzer 挂进编译；② 该 provider 程序集未被引用。");
                return provider;
            },
            sp => new G1OnlyEntitySchemaRegistry(sp.GetRequiredService<IDbProvider>(), lookup,
                model: builder.Model, defaultStringLength: builder.DefaultStringLength));

    /// <summary>两条注册路共用的登记段：校验、provider/注册表 Singleton、options 与 orm Scoped。
    /// 本身不带标注——反射税由调用方注入的两颗工厂各自携带（G2 路在 RegisterCore 的标注里，
    /// AOT-safe 路的两颗工厂零反射）。</summary>
    static IServiceCollection RegisterCommon(
        IServiceCollection services,
        QuickSqlOrmBuilder builder,
        Func<IServiceProvider, IDbProvider> providerFactory,
        Func<IServiceProvider, IEntitySchemaRegistry> registryFactory)
    {
        // 启动期只校验静态串；工厂模式延到 Scoped 解析时再判。
        if (builder.ConnectionStringFactory is null && string.IsNullOrEmpty(builder.ConnectionString))
            throw new InvalidOperationException($"AddQuickSqlOrm：DbKind.{builder.Kind} 的连接串为空。");

        // 从库与具名集群是启动期常量，而工厂解析的主库串每个请求一条：两者配在一起时
        // main 的主库属于当前租户、它的从库属于写配置时那一条串，症状是"读到别的租户的数据"。
        // 这一格不给静默的机会（要按请求换整座集群就为那一座另建一颗 orm，判据在 QuickSqlOrmBuilder.Clusters）。
        if (builder.ConnectionStringFactory is not null && (builder.Replicas.Count > 0 || builder.Clusters.Count > 0))
            throw new InvalidOperationException(
                "AddQuickSqlOrm：连接串工厂是按请求解析的，而从库与具名集群是启动期常量——两者混用会让 main 的主库与从库属于不同的租户。" +
                "要么整座集群都用静态串（去掉工厂），要么按请求换库时另建一颗 orm。");

        // 静态配置在这一格就把 ConnectionSources 建出来判一次：具名集群里出现 main、
        // 从库清单里有空串，都在注册期红，而不是等到第一条语句。
        if (builder.ConnectionStringFactory is null)
            new ConnectionSources(builder.ConnectionString, builder.Replicas, builder.Clusters);

        // 装配清单在登记这一刻抄一次快照：注册之后再往 AfterBuild 里添东西不影响已经登记的这一颗
        // （MS.DI 的描述表本就是登记时的产物，留活引用只会让"什么时候装的"变成猜）。
        var assembly = builder.AfterBuild.ToArray();

        services.AddSingleton(providerFactory);

        // 注册表只依赖 Provider + Model，都是启动期常量 → Singleton。
        // 不依赖 QuickSqlOrmOptions（那是 Scoped 的），直接从 Provider 拿 Model。
        services.AddSingleton(registryFactory);

        // QuickSqlOrmOptions 走 Scoped：连接串每个请求可以不同（多租户每库一串）。
        // 与 EF Core 的 DbContextOptions<T> 同一条判据。
        services.AddScoped(sp =>
        {
            var provider = sp.GetRequiredService<IDbProvider>();
            var connectionString = builder.ConnectionStringFactory?.Invoke(sp) ?? builder.ConnectionString;

            if (string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException(
                    $"AddQuickSqlOrm：DbKind.{builder.Kind} 的连接串工厂返回了空值。" +
                    "检查租户解析逻辑：当前 IServiceProvider 里是否拿得到租户对应的连接串。");

            return builder.BuildOptions(provider, connectionString);
        });

        services.AddScoped(sp =>
        {
            var options = sp.GetRequiredService<QuickSqlOrmOptions>();
            var schemas = sp.GetRequiredService<IEntitySchemaRegistry>();
            var orm = new QuickSqlOrm(options, schemas);

            // 装配跑在建好之后、这条流上的第一条语句之前：按请求的过滤器、审计与日志钩子没有别的落点。
            foreach (var install in assembly)
                install(sp, orm);

            return orm;
        });

        return services;
    }

    [RequiresUnreferencedCode("DriverRegistry 按内建表反射解析 DbProviderFactory。")]
    static DbProviderFactory ResolveFactory(DbKind kind)
    {
        var driverKey = ProviderRegistry.DriverKeyOf(kind)
            ?? throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记（生成器未挂或 provider 未引用）。");
        return new DriverRegistry().Factory(driverKey);
    }
}
