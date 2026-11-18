using QuickSql.Generated;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>G1-only 注册表（trim/AOT 档的静态保证口）与 AddQuickSqlOrmAotSafe 的判据。
/// 与 GeneratedEntitySchemaRegistry 的差别只有一颗：未命中不退回 G2，当场抛 NotSupportedException。
/// 所以这一册问四件事：① 命中路径与混合档逐字同形（同一颗 EntityContractFactory 收尾）；
/// ② 未生成/被 Fluent 覆写的实体拒得响亮（消息指得出路）；③ 校验只有 ContractOnly/Off 两档；
/// ④ DI 的 AOT-safe 口装出来的就是这一颗注册表，且共用登记段的闸门与装配一颗不少。
/// 全程离线（SqlServer 桩工厂），一句 SQL 都不上线。</summary>
public sealed class G1OnlyRegistryTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static G1OnlyEntitySchemaRegistry G1Only(EntitySchemaOptions? options = null, ModelConfiguration? model = null)
        => new(Provider, QuickSqlGeneratedMetadata.TryGet, options, model);

    /// <summary>本项目没把生成器挂成 Analyzer（负样本纪律，见 csproj 注释），
    /// 所以这一颗本地实体永远不在事实册里——它是"未生成"那一格的天然靶子。</summary>
    sealed class NotGenerated
    {
        public int Id { get; set; }
    }

    // ── ① 命中路径与混合档同形 ──────────────────────────────────────────

    [Fact]
    public void A_generated_entity_gets_the_same_contract_as_the_mixed_registry()
    {
        var mixed = new GeneratedEntitySchemaRegistry(Provider, QuickSqlGeneratedMetadata.TryGet);

        var expected = mixed.Contract<Order>();
        var actual = G1Only().Contract<Order>();

        Assert.Equal(expected.Table.Name, actual.Table.Name);
        Assert.Equal(expected.Columns.Length, actual.Columns.Length);

        for (var i = 0; i < expected.Columns.Length; i++)
        {
            Assert.Equal(expected.Columns[i].ColumnName, actual.Columns[i].ColumnName);
            Assert.Equal(expected.Columns[i].MemberName, actual.Columns[i].MemberName);
            Assert.Equal(expected.Columns[i].ClrType, actual.Columns[i].ClrType);
        }
    }

    [Fact]
    public void The_generated_schema_reads_and_writes_by_ordinal_without_reflection()
    {
        var schema = G1Only().Schema<Order>();
        var contract = schema.Contract;
        var entity = schema.New();

        var nameOrdinal = Array.IndexOf(contract.Columns, contract.ByMember[nameof(Order.CustomerName)]);
        schema.SetValue(entity, nameOrdinal, "alice");

        Assert.Equal("alice", schema.GetValue(entity, nameOrdinal));
        Assert.Equal("alice", entity.CustomerName);
    }

    [Fact]
    public void Resolved_types_are_registered_for_structure_sync()
    {
        var registry = G1Only();

        Assert.Empty(registry.RegisteredTypes);

        registry.Contract<Order>();

        Assert.Contains(typeof(Order), registry.RegisteredTypes);
    }

    [Fact]
    public void A_writable_to_one_navigation_carries_a_generated_writer()
    {
        // G1 证据：写口是生成器写好的 lambda（直接强转赋值），不是运行期编译的委托。
        var relationship = G1Only().Contract<Order>().Relationships.Single(r => r.MemberName == nameof(Order.Customer));

        Assert.NotNull(relationship.NavigationWriter);

        var order = new Order();
        var customer = new Customer { Id = 7 };
        relationship.NavigationWriter!(order, customer);

        Assert.Same(customer, order.Customer);

        // null 也缝得回去（引用型强转的运行期语义与 MemberAccessors 的 Convert 一致）。
        relationship.NavigationWriter!(order, null);
        Assert.Null(order.Customer);
    }

    [Fact]
    public void The_reflected_registry_leaves_the_writer_null_for_the_fallback()
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
        var reflected = new EntitySchemaRegistry(new ContractResolver(provider));

        var relationship = reflected.Contract<Order>().Relationships.Single(r => r.MemberName == nameof(Order.Customer));

        Assert.Null(relationship.NavigationWriter);
    }

    [Fact]
    public void A_writable_appendable_collection_carries_generated_bindings()
    {
        // G1 证据：集合缝合的后三把委托（写/追加/造表）是生成器写好的强类型 lambda，不是运行期
        // MakeGeneric + 编译表达式树 + Expression.New(List<T> ctor)——AOT 全裁剪下 List<T> 无参构造被裁
        // 的那条边界（IncludeMany 探针曾报 WARN）由此消失。Order.Items 是 List<OrderItem>? 公共 set，正样本。
        var edge = G1Only().Contract<Order>().Collections.Single(c => c.MemberName == nameof(Order.Items));

        Assert.NotNull(edge.CollectionSetter);
        Assert.NotNull(edge.CollectionAdd);
        Assert.NotNull(edge.NewCollection);

        // 造表口交回的是一只真的 List<OrderItem>（字面 new，裁剪看得见）。
        var collection = edge.NewCollection!();
        Assert.IsType<List<OrderItem>>(collection);

        // 追加口往集合里加一颗（强转声明类型再 Add，与反射侧 appendable 时 target=声明类型同口径）。
        var item = new OrderItem { Id = 3 };
        edge.CollectionAdd!(collection, item);
        Assert.Same(item, ((List<OrderItem>) collection)[0]);

        // 写口把整只集合缝回导航属性；读口（NavigationReader）读得回来。
        var order = new Order();
        edge.CollectionSetter!(order, collection);
        Assert.Same(collection, order.Items);
        Assert.Same(collection, edge.NavigationReader(order));

        // null 也写得回（引用型强转的运行期语义与 MemberAccessors 的 Convert 一致）。
        edge.CollectionSetter!(order, null);
        Assert.Null(order.Items);
    }

    [Fact]
    public void The_contract_aware_collection_binding_stitches_via_generated_delegates()
    {
        // 契约感知的 BindCollection 优先用集合边上的生成委托（零反射构造），IncludeMany/补载每行只调 lambda。
        var contract = G1Only().Contract<Order>();
        var nav = MemberAccessors.BindCollection(contract, nameof(Order.Items), typeof(OrderItem), "集合导航缺失");

        var order = new Order(); // Items 未初始化（null）
        var item = new OrderItem { Id = 11 };
        nav.Attach(order, item); // 空位 → 造表 → 写回 → 追加

        Assert.NotNull(order.Items);
        Assert.Same(item, order.Items![0]);
    }

    [Fact]
    public void The_reflected_registry_leaves_collection_bindings_null_for_the_fallback()
    {
        var reflected = new EntitySchemaRegistry(new ContractResolver(Provider));
        var edge = reflected.Contract<Order>().Collections.Single(c => c.MemberName == nameof(Order.Items));

        // G2 不带后三把（null）——消费侧退回 MemberAccessors.BindCollection 的启动期编译委托。
        Assert.Null(edge.CollectionSetter);
        Assert.Null(edge.CollectionAdd);
        Assert.Null(edge.NewCollection);

        // 读口与级联入口 G2 仍带（启动期编译一次），缺了它们 EntityContractFactory 建契约时就抛。
        Assert.NotNull(edge.NavigationReader);
        Assert.NotNull(edge.CascadeEnter);
    }

    [Fact]
    public void Generated_GetValue_boxes_each_column_as_its_declared_type()
    {
        // int + decimal 混合实体是陷阱形状：switch 表达式有自然公共类型 decimal（int→decimal 隐式），
        // 不逐臂 (object?) 装箱的话所有列都按 decimal 交回——跨实体键缝合 boxed decimal ≠ boxed int，
        // 字典永远命不中；写侧参数也会把 int 列绑成 Decimal。2026-11-17 AOT 冒烟闸审出，此钉防回归。
        var schema = G1Only().Schema<Order>();
        var contract = schema.Contract;
        var order = new Order { Id = 5, CustomerId = 9, Amount = 3m };

        var id = schema.GetValue(order, Array.IndexOf(contract.Columns, contract.ByMember[nameof(Order.Id)]));
        var customerId = schema.GetValue(order, Array.IndexOf(contract.Columns, contract.ByMember[nameof(Order.CustomerId)]));
        var amount = schema.GetValue(order, Array.IndexOf(contract.Columns, contract.ByMember[nameof(Order.Amount)]));

        Assert.IsType<int>(id);
        Assert.IsType<int>(customerId);
        Assert.IsType<decimal>(amount);
    }

    // ── ② 未命中即抛，不退回反射 ────────────────────────────────────────

    [Fact]
    public void An_entity_outside_the_generated_facts_is_refused_with_an_actionable_message()
    {
        var error = Assert.Throws<NotSupportedException>(() => G1Only().Contract<NotGenerated>());

        Assert.Contains(nameof(NotGenerated), error.Message);
        Assert.Contains("QuickSql003", error.Message);
        Assert.Contains("GeneratedEntitySchemaRegistry", error.Message);
    }

    [Fact]
    public void A_fluent_configured_entity_is_refused_even_though_it_was_generated()
    {
        var model = new ModelConfiguration();
        model.Entity<Order>();

        var error = Assert.Throws<NotSupportedException>(() => G1Only(model: model).Contract<Order>());

        Assert.Contains(nameof(Order), error.Message);
        Assert.Contains("Fluent", error.Message);
    }

    // ── ③ 校验只有 ContractOnly / Off 两档 ──────────────────────────────

    [Fact]
    public void ContractOnly_validation_uses_the_generated_rulers()
    {
        var validator = G1Only().Validator<Order>();
        var entity = new Order { CustomerName = new string('x', 65) };   // 契约长度 64
        var failures = new List<ValidationFailure>();

        Assert.Equal(ValidationMode.ContractOnly, validator.Mode);
        Assert.False(validator.TryValidate(entity, failures));
        Assert.NotEmpty(failures);
    }

    [Fact]
    public void Off_validation_passes_everything()
    {
        var validator = G1Only(new EntitySchemaOptions(Mode: ValidationMode.Off)).Validator<Order>();

        Assert.Equal(ValidationMode.Off, validator.Mode);
        Assert.True(validator.TryValidate(new Order(), []));
    }

    [Fact]
    public void DataAnnotations_validation_is_refused_because_it_needs_runtime_attribute_instances()
    {
        var registry = G1Only(new EntitySchemaOptions(Mode: ValidationMode.DataAnnotations));

        var error = Assert.Throws<NotSupportedException>(() => registry.Validator<Order>());

        Assert.Contains("DataAnnotations", error.Message);
        Assert.Contains("ContractOnly", error.Message);
    }

    // ── ④ DI 的 AOT-safe 口 ─────────────────────────────────────────────

    static IServiceCollection RegisterAot(Action<QuickSqlOrmBuilder>? configure = null)
        => new ServiceCollection().AddQuickSqlOrmAotSafe(
            DbKind.SqlServer, SqlServer.OfflineConnectionString, SqlServer.NoDriverFactory.Instance,
            QuickSqlGeneratedMetadata.TryGet, configure);

    static QuickSqlOrm Resolve(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<QuickSqlOrm>();
    }

    [Fact]
    public void The_aot_safe_entry_wires_the_g1_only_registry()
    {
        var orm = Resolve(RegisterAot());

        Assert.IsType<G1OnlyEntitySchemaRegistry>(orm.Schemas);

        // 命中实体：读链与按实体写链都出得来语句（离线，只到文本）。
        Checks.Has("FROM [QuickSqlOrder] AS [t0]", orm.Select<Order>().ToSql());
        Checks.Has("UPDATE [t0] SET [CustomerName]", orm.Update(new Order { Id = 1, CustomerName = "bob" }).ToSql());
    }

    [Fact]
    public void The_aot_safe_entry_refuses_an_ungenerated_entity_at_first_contract_ask()
    {
        var orm = Resolve(RegisterAot());

        Assert.Throws<NotSupportedException>(() => orm.Select<NotGenerated>());
    }

    [Fact]
    public void The_aot_safe_entry_keeps_the_shared_registration_gates_and_assembly()
    {
        // 空串在注册期就红（共用登记段的闸门）。
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddQuickSqlOrmAotSafe(
                DbKind.SqlServer, "", SqlServer.NoDriverFactory.Instance, QuickSqlGeneratedMetadata.TryGet));

        // AfterBuild 装配照跑（共用登记段的那一颗快照清单）。
        var seen = new List<QuickSqlOrm>();
        var orm = Resolve(RegisterAot(b => b.AfterBuild.Add((_, target) => seen.Add(target))));

        Assert.Single(seen);
        Assert.Same(orm, seen[0]);
    }
}