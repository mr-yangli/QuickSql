using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Generated;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§13.11：G1（源生成）与 G2（启动期反射）必须给出<b>逐字段相同</b>的答案。
/// 这不是「两条实现碰巧一样」的核对，而是「两条实现是否共用同一份判定」的核对——
/// 收尾（provider 的 storage defaults、自增约定、索引与外键的列名换算）两边都走 <see cref="EntityContractFactory"/>，
/// 校验消息两边都走 <see cref="ContractRules"/>，所以这里的红意味着某一层的规则被复制成了两份并且已经分叉。
/// <b>全程不连库</b>：<see cref="SqlServer.NoDriverFactory"/> 一被碰到就抛。</summary>
public sealed class MetadataEquivalenceTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static IEntitySchemaRegistry Generated(ModelConfiguration? model = null)
        => new GeneratedEntitySchemaRegistry(Provider, QuickSqlGeneratedMetadata.TryGet, model: model);

    static IEntitySchemaRegistry Reflected(ModelConfiguration? model = null)
        => new EntitySchemaRegistry(new ContractResolver(Provider, model));

    static QuickSqlOrm Orm(IEntitySchemaRegistry schemas)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString }, schemas);

    /// <summary>只比「生成的 == 反射的」不够：两条路径同时退化成同一个错误值也会通过。
    /// 所以每个实体还钉了一条绝对值断言（<see cref="Contract_carries_the_layer_it_claims"/>）。</summary>
    [Theory]
    [InlineData(typeof(Order))]
    [InlineData(typeof(OrderItem))]
    [InlineData(typeof(Customer))]
    [InlineData(typeof(SessionToken))]
    [InlineData(typeof(InventoryItem))]
    [InlineData(typeof(DefaultsRow))]
    [InlineData(typeof(LegacyShipment))]
    // 可空上下文之外那一格（<c>NullabilityDeclared</c>）是这一册里唯一"两侧各读一份元数据"的字段：
    // G1 问的是 Roslyn 的 <c>NullableAnnotation.None</c>，G2 问的是 <c>NullabilityInfoContext</c> 的
    // <c>Unknown</c>。两个名字不同的东西必须在契约上落成同一个值，否则同一份代码在两条启动路径上
    // 一条挡写入、一条放行 —— 而那种分叉不改变任何一条 SQL 的文本。
    [InlineData(typeof(ObliviousRow))]
    [InlineData(typeof(SpokenRow))]
    // 只读计算属性那一档（<c>Expression must be writeable</c> 那颗真 500 的来路）：两侧都要把它摘掉，
    // 而摘的位置在链上第几格也算形状 —— 反射侧先判显式排除再判写口，生成器同一次序才有对拍意义。
    [InlineData(typeof(ComputedMemberRow))]
    [InlineData(typeof(BareReport))]
    [InlineData(typeof(AuditRow))]
    [InlineData(typeof(Project))]
    [InlineData(typeof(Milestone))]
    [InlineData(typeof(SelfRef))]
    [InlineData(typeof(TwoKeys))]
    // 分表这两张（§16.4）在这里的对拍有它自己的一处不可替代：G1 那颗分表实参是从 TypedConstant 里
    // 抄成表达式文本的，而 G2 读的是注解实例。两侧算出的 SplitFacts 只要差一格（档、成员、桶数、
    // 换算后的列名），落进库里的就是两张不同的表 —— 那种分叉不会让任何一条 SQL 变红。
    [InlineData(typeof(ShardLog))]
    [InlineData(typeof(TenantRow))]
    // 超级表那张（v0.39 第六笔）同一取向：Subtable 模板与 TAG 成员清单在 G1 是从 TypedConstant 抄成的
    // 字面量、在 G2 读的是注解实例；两侧漏抄一颗 TAG 或抄错一个占位符，TAGS 子句与子表名从第一天起就不同，
    // 而任何一条 SQL 都不会因此变红 —— 那一格的分叉只有这里量得到。
    [InlineData(typeof(StableMeter))]
    // 集群归属这两张（§16.4①）同一取向：G1 那颗集群名是从注解实参抄成<em>字符串字面量</em>的，
    // G2 读的是注解实例。那一格分叉不会让任何一条 SQL 变红 —— 它让一句读发到另一座库里去。
    // ArchiveNote 是第三、第四颗参数同时非空的那一张：漏抄任一颗都是"片名对、库错"。
    [InlineData(typeof(ReportRow))]
    [InlineData(typeof(ArchiveNote))]
    // 级联那五对（§8.2 的集合边）在这里的对拍同样是不可替代的：Cascade 那一格 G1 读的是符号上的
    // [NoCascade]（属性档与类档两档），G2 读的是注解实例；[ForeignKey] 点名的成员名同理。
    // 两侧对同一根边给出一真一假的开关，落到库里就是"同一份代码在两条启动路径上带不同数量的明细"。
    // SlotHook 是那条链的第三层（架 → 位 → 钩）：摘行那一档要说"摘掉的一只连带它自己手里的子串"，
    // 而这句话在两层模型上根本判不出来 —— 两侧的第三层边只要差一格，级联的深度就不一样了。
    [InlineData(typeof(Board))]
    [InlineData(typeof(Card))]
    [InlineData(typeof(Rack))]
    [InlineData(typeof(ShelfSlot))]
    [InlineData(typeof(SlotHook))]
    [InlineData(typeof(Folder))]
    [InlineData(typeof(Paper))]
    [InlineData(typeof(Ledger))]
    [InlineData(typeof(LedgerNote))]
    [InlineData(typeof(Crate))]
    [InlineData(typeof(Parcel))]
    public void Both_paths_produce_the_same_contract(Type clrType)
        => Assert.Equal(Shape(Reflected().Contract(clrType)), Shape(Generated().Contract(clrType)));

    [Theory]
    [InlineData(typeof(Order), "table = -.QuickSqlOrder", "primary = Id", "index IX_QuickSqlOrder_CreatedAt NonClustered (CreatedAt) → (CreatedAt)",
        "nav Customer → QuickSql.TestInfra.Customer fk=(CustomerId) cols=(CustomerId)",
        // 集合边钉一条绝对值：两侧同时把 Items 记成"不是边"也是一样的形状，那条相等断言照样绿，
        // 而级联在两条路径上都没有边可走 —— 这一格正是"逐字段相同"覆盖不到的那种共同退化。
        "coll Items → QuickSql.TestInfra.OrderItem fk=() cascade=True")]
    [InlineData(typeof(LegacyShipment), "table = -.QuickSqlLegacyShipment", "primary = ShipmentId", "[1] CarrierCode clr=String col=carrier_code len=Char:32/64B")]
    [InlineData(typeof(Project), "nav = (0)")]
    [InlineData(typeof(Milestone), "nav Project → QuickSql.TestInfra.Project fk=(ProjectId) cols=(ProjectId) origin=Convention: 约定：Project ⇒ ProjectId")]
    [InlineData(typeof(SelfRef), "nav Manager → QuickSql.TestInfra.SelfRef fk=(ManagerId) cols=(ManagerId)")]
    [InlineData(typeof(TwoKeys), "nav = (0)")]
    [InlineData(typeof(ReportRow), "source = report")]
    // 超级表那一格钉绝对值（v0.39 第六笔）：两侧同时把 StableMeter 记成"普通表"也是相等的，
    // 而那种共同退化会让 CREATE STABLE 发成 CREATE TABLE —— 相等断言看不见，这一条看得见。
    [InlineData(typeof(StableMeter), "table = -.QuickSqlStableMeter",
        "stable = template=QuickSqlStableMeter_{device} tags=(Device→Device,GroupId→GroupId)")]
    // 开关那一格钉绝对值：两条路径同时把 [NoCascade] 读成"照旧级联"也是相等的，
    // 而那种共同退化会让看板在两条启动路径上各自带出一串卡片 —— 相等断言看不见，这一条看得见。
    [InlineData(typeof(Board), "coll Cards → QuickSql.TestInfra.Card fk=() cascade=False")]
    [InlineData(typeof(Crate), "coll Parcels → QuickSql.TestInfra.Parcel fk=() cascade=False")]
    [InlineData(typeof(Rack), "coll Slots → QuickSql.TestInfra.ShelfSlot fk=(RackKey) cascade=True")]
    [InlineData(typeof(ShelfSlot), "coll Hooks → QuickSql.TestInfra.SlotHook fk=(SlotId) cascade=True")]
    public void Contract_carries_the_layer_it_claims(Type clrType, params string[] expected)
    {
        var shape = Shape(Generated().Contract(clrType));

        foreach (var line in expected)
            Assert.Contains(shape, l => l == line || l.StartsWith(line, StringComparison.Ordinal));

        // 自增不是哪一层声明的，是「整型主键 ⇒ 自增」这条约定在收尾时补的（两侧共用一份实现）。
        // Project 的主键是 Guid、StableMeter 的主键是 DateTime（v0.39 第六笔的稳定表夹具），
        // 那一格必须是 False —— 无条件断 True 等于把这条约定念成"每个主键都自增"。
        Assert.Contains(shape, l => l.Contains("identity=" + (clrType == typeof(Project) || clrType == typeof(StableMeter) ? "False" : "True"), StringComparison.Ordinal));
    }

    /// <summary>把契约摊成一行一个字段：集合相等的失败消息只会说「两个集合不同」，
    /// 而这里要的是<b>哪一格</b>分叉。origin 默认随行——两侧同源时它就是证据；
    /// 比「注解 vs Fluent」那一对时关掉它，因为 origin 按定义要记录是哪一层赢的（§10.2）。
    /// <c>conc=</c> 那一格（v0.18）不加进来的话，「逐字段相同」是一句假话：并发标记只落在一侧时，
    /// 列清单、<c>WHERE</c>、回填集三处一起分岔，而这里照样全绿。
    /// <c>nav</c> 那一格（v0.20）同一取向：引用导航只落在一侧时，另一侧的 <c>SaveChanges</c> 就排不出这一根依赖，
    /// 而两条路径"看起来都建出了这张表"。</summary>
    static string[] Shape(EntityContract contract, bool withOrigin = true)
    {
        var lines = new List<string>
        {
            // 表备注搭在同一行：G1 抄的是注解实参的字面量、G2 读的是注解实例（Fluent 那一层只有 G2 看得见），
            // 两侧分叉时这一格是"改了备注而库里永远不改"的唯一读数（§9.5 的备注同步以这一格为来路）。
            $"table = {contract.Table.Schema ?? "-"}.{contract.Table.Name} comment={contract.Table.Comment ?? "-"}",

            // 分表那一格（§16.4）：表名之外还要比"这一张是模板、按哪一档、从哪一列换算"。
            // 只比表名的话，G1 把桶数抄成 0 而 G2 抄成 4 也照样绿，而两侧算出的物理片名从第一天起就不同。
            $"split = {(contract.Table.Split is { } split ? $"{split.Kind} member={split.MemberName} column={split.ColumnName} buckets={split.Buckets} time={split.IsTime}" : "-")}",

            // 集群归属那一格（§16.4①）：不加进来的话「逐字段相同」又是一句假话 —— G1 把 report 抄成
            // archive 时两侧各自都能建出表、都能出 SQL，只是发往两座不同的库。
            $"source = {contract.Table.Source?.Name ?? "-"}",

            // 超级表那一格（v0.39 第六笔）：模板与 TAG 清单（成员名→列名、声明序）都要比。
            // 只比"是不是稳定表"的话，G1 漏抄一颗 TAG 而 G2 抄全也照样绿，
            // 而两侧发出的 TAGS 子句从第一天起就少一列。
            $"stable = {(contract.Stable is { } stable ? $"template={stable.SubtableTemplate ?? "-"} tags=({string.Join(",", stable.Tags.Select(t => t.MemberName + "→" + t.ColumnName))})" : "-")}",

            $"primary = {string.Join(",", contract.PrimaryKey.Select(c => c.MemberName))}",
            $"nav = ({contract.Relationships.Count})",

            // 集合边那一格（级联的来路）：两侧都要比「有几根、哪一根指向哪个子型、声明了什么外键成员、
            // 关没关级联」，但<b>不比委托的身份</b> —— 那两把委托按构造就不可能是同一个对象
            // （G1 是生成的 lambda，G2 是编译的表达式树），能比的只有它<b>在不在</b>，而不在的话
            // EntityContractFactory 在建契约时就抛了，这一格根本到不了这里。
            $"coll = ({contract.Collections.Count})",
        };

        for (var i = 0; i < contract.Columns.Length; i++)
        {
            var c = contract.Columns[i];

            lines.Add($"[{i}] {c.MemberName} clr={c.ClrType.Name} col={c.ColumnName} len={c.LengthSemantics}:{c.MaxChars?.ToString() ?? "-"}/{c.MaxBytes?.ToString() ?? "-"}B unicode={c.Unicode} null={c.IsNullable} spoken={c.NullabilityDeclared} key={c.IsPrimary} identity={c.IsIdentity} conc={c.IsConcurrencyToken} prec={c.Precision?.ToString() ?? "-"}/{c.Scale?.ToString() ?? "-"} store={c.StoreTypeName ?? "-"} comment={c.Comment ?? "-"}"
                + (withOrigin ? $" origin={c.Origin}" : ""));
        }

        foreach (var index in contract.Indexes)
            lines.Add($"index {index.Name} {index.Kind} ({string.Join(",", index.MemberNames)}) → ({string.Join(",", index.ColumnNames)}) desc={index.Descending}");

        foreach (var nav in contract.Relationships)
            lines.Add($"nav {nav.MemberName} → {nav.TargetType.FullName} fk=({string.Join(",", nav.MemberNames)}) cols=({string.Join(",", nav.ColumnNames)})"
                + (withOrigin ? $" origin={nav.Origin}" : ""));

        // 集合边：外键成员名属于子端那张表，这里只比声明的原样（没声明就是空），换算在级联那一处运行期做。
        foreach (var edge in contract.Collections)
            lines.Add($"coll {edge.MemberName} → {edge.ChildType.FullName} fk=({string.Join(",", edge.DeclaredForeignKeyMembers)}) cascade={edge.Cascade}"
                + (withOrigin ? $" origin={edge.Origin}" : ""));

        return [.. lines];
    }

    [Fact]
    public void The_generated_entity_is_materialized_by_generated_code_not_by_reflection()
    {
        // 这句是「G1 真的在跑」的证据。没有它，下面所有相等断言都可能只是两条反射路径在互相印证。
        Assert.Equal("QuickSqlGeneratedOrderSchema", Generated().Schema<Order>().GetType().Name);
        Assert.Equal("QuickSqlGeneratedOrderValidator", Generated().Validator<Order>().GetType().Name);

        // 没标注的实体必须留在 G2，否则「命中 G1」这条断言只是自称通过。
        Assert.StartsWith("ReflectionEntitySchema", Generated().Schema<BareReport>().GetType().Name);
        Assert.StartsWith("ReflectionEntityValidator", Generated().Validator<BareReport>().GetType().Name);

        // 让路判据看的是"有没有可映射的公共属性"，不是"有没有边"（§12.7 那六条），所以零条边的 Project
        // 与一条边的 Milestone 都该在 G1 那一侧 —— 没有这两句，上面那 8 条新对拍可能只是两条反射路径在互相印证。
        Assert.Equal("QuickSqlGeneratedProjectSchema", Generated().Schema<Project>().GetType().Name);
        Assert.Equal("QuickSqlGeneratedMilestoneSchema", Generated().Schema<Milestone>().GetType().Name);

        // 分表那两张也必须在 G1 这一侧命中，否则上面两条对拍只是两条反射路径在互相印证：
        // `[SplitTable]` 的枚举实参在 G1 是从 TypedConstant 抄成<em>表达式文本</em>的（含 (SplitKind)99 那种退路），
        // 在 G2 读的是注解实例 —— 只有两边都真的走自己的那条路，相等才有内容。
        Assert.Equal("QuickSqlGeneratedShardLogSchema", Generated().Schema<ShardLog>().GetType().Name);
        Assert.Equal("QuickSqlGeneratedTenantRowSchema", Generated().Schema<TenantRow>().GetType().Name);

        // 集群那两张同理：`[DataSource]` 在 G1 是抄进第四颗参数字面量的，在 G2 是注解实例，
        // 只有两边都真走自己那条路，上面那两条对拍才有内容。
        Assert.Equal("QuickSqlGeneratedReportRowSchema", Generated().Schema<ReportRow>().GetType().Name);
        Assert.Equal("QuickSqlGeneratedArchiveNoteSchema", Generated().Schema<ArchiveNote>().GetType().Name);
    }

    [Fact]
    public void Both_paths_read_the_same_order_row()
    {
        var rows = Read(typeof(Order), OrderRow());
        var reflected = Assert.IsType<Order>(rows[0]);
        var generated = Assert.IsType<Order>(rows[1]);

        Assert.Equal(Vals(reflected), Vals(generated));

        // 上面那句只证明两侧一致；这句证明它们一致地<b>读对了</b>。
        Assert.Equal(OrderState.Shipped, generated.State);
        Assert.Equal(1234.5600m, generated.Amount);
        Assert.Null(generated.PaidAt);
        Assert.Equal("加急，别丢字", generated.Note);

        static string[] Vals(Order o)
            => [$"Id={o.Id}", $"CustomerName={o.CustomerName}", $"CustomerId={o.CustomerId}", $"Amount={o.Amount:0.0000}",
                $"State={o.State}", $"CreatedAt={o.CreatedAt:O}", $"PaidAt={o.PaidAt:O}", $"Note={o.Note}"];
    }

    [Fact]
    public void Both_paths_read_the_same_dataannotations_row()
    {
        var rows = Read(typeof(LegacyShipment), ShipmentRow());
        var reflected = Assert.IsType<LegacyShipment>(rows[0]);
        var generated = Assert.IsType<LegacyShipment>(rows[1]);

        Assert.Equal(reflected.ShipmentId, generated.ShipmentId);
        Assert.Equal(reflected.CarrierCode, generated.CarrierCode);
        Assert.Equal(reflected.Weight, generated.Weight);
        Assert.Equal(reflected.DispatchedAt, generated.DispatchedAt);

        // [NotMapped] 不在契约里，所以它连读取序号都没有：这一列必须保持默认值。
        Assert.Null(generated.TrackingUrl);
        Assert.Equal("SFxxx", generated.CarrierCode);
        Assert.Equal(12.3400m, generated.Weight);
    }

    /// <summary>两条路径的读取委托读同一份 <see cref="DataTableReader"/>、<b>同一张票</b>，返回 <c>[0]=G2、[1]=G1</c>。
    /// 序号取 <c>0..n-1</c> 的前提是「契约列序 == 选列序」，那句前提就在下面的断言里。
    /// 票必须共用：它决定"哪些列这次不进装箱口"（§16.21），两边各探一次的话，
    /// 这一条就退化成"两条实现各自读对了"而不再比"两条实现对同一件事的判断相同"。</summary>
    static object[] Read(Type clrType, DataTable table)
    {
        var reflected = Reflected();
        var contract = reflected.Contract(clrType);
        var ordinals = new int[contract.Columns.Length];

        for (var i = 0; i < ordinals.Length; i++)
            ordinals[i] = i;

        Assert.Equal(contract.Columns.Select(c => c.ColumnName), table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Take(ordinals.Length));

        using var reader = table.CreateDataReader();

        Assert.True(reader.Read());

        var plan = ReadOps.Build(reader, contract, 0);

        return [reflected.ReadDelegate(clrType)(reader, plan), Generated().ReadDelegate(clrType)(reader, plan)];
    }

    static DataTable OrderRow()
    {
        var table = new DataTable();

        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("CustomerName", typeof(string));
        table.Columns.Add("CustomerId", typeof(int));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("State", typeof(int));
        table.Columns.Add("CreatedAt", typeof(DateTime));
        table.Columns.Add("PaidAt", typeof(DateTime));
        table.Columns.Add("Note", typeof(string));

        table.Rows.Add(1, "张三", 42, 1234.5600m, (int) OrderState.Shipped, new DateTime(2026, 1, 2, 3, 4, 5), DBNull.Value, "加急，别丢字");

        return table;
    }

    static DataTable ShipmentRow()
    {
        var table = new DataTable();

        table.Columns.Add("ShipmentId", typeof(int));
        table.Columns.Add("carrier_code", typeof(string));
        table.Columns.Add("Weight", typeof(decimal));
        table.Columns.Add("DispatchedAt", typeof(DateTime));
        table.Columns.Add("TrackingUrl", typeof(string));

        table.Rows.Add(7, "SFxxx", 12.3400m, new DateTime(2026, 3, 5, 10, 0, 0), "不该被读");

        return table;
    }

    [Fact]
    public void Both_paths_report_the_same_validation_failures()
    {
        var oversize = new Order { CustomerName = new string('n', 65), Note = "ok" };
        var inside = new Order { CustomerName = "刚好", Note = null };

        Assert.Equal(Failures(Reflected(), oversize), Failures(Generated(), oversize));
        Assert.Equal(Failures(Reflected(), inside), Failures(Generated(), inside));

        // 至少要真的报出问题，否则上面那句相等只是两个人同时沉默。
        Assert.Equal("length", Rules(Generated(), oversize));
        Assert.Empty(Failures(Generated(), inside));
    }

    [Fact]
    public void The_required_and_length_rulers_apply_the_same_way_on_dataannotations_columns()
    {
        var missing = new LegacyShipment { Weight = 1m };
        var oversize = new LegacyShipment { CarrierCode = new string('c', 33), Weight = 1m };

        Assert.Equal(Failures(Reflected(), missing), Failures(Generated(), missing));
        Assert.Equal(Failures(Reflected(), oversize), Failures(Generated(), oversize));

        Assert.Equal("required", Rules(Generated(), missing));
        Assert.Equal("length", Rules(Generated(), oversize));
    }

    static string Rules<T>(IEntitySchemaRegistry schemas, T entity) where T : class
        => string.Join(",", Failures(schemas, entity).Select(f => f.Rule));

    static List<ValidationFailure> Failures<T>(IEntitySchemaRegistry schemas, T entity) where T : class
    {
        var failures = new List<ValidationFailure>();

        schemas.Validator<T>().TryValidate(entity, failures);

        return failures;
    }

    [Fact]
    public void A_higher_validation_mode_is_the_reflection_path_job_not_the_generated_one()
    {
        // 生成的校验器只有 ContractOnly 档：再往上一档要读注解实例，那是运行期的东西（§10.6）。
        var raised = new GeneratedEntitySchemaRegistry(Provider, QuickSqlGeneratedMetadata.TryGet,
            new EntitySchemaOptions(ValidationMode.DataAnnotations));

        Assert.StartsWith("ReflectionEntityValidator", raised.Validator<Order>().GetType().Name);
        Assert.Equal("QuickSqlGeneratedOrderValidator", Generated().Validator<Order>().GetType().Name);
    }

    [Fact]
    public void Fluent_configuration_makes_the_generator_step_aside()
    {
        var model = new ModelConfiguration();
        model.Entity<Order>().ToTable("QuickSqlOrderRenamed").Property(o => o.Note).HasMaxLength(16);

        // §10.2 里 Fluent 压在注解之上，而生成器看不到运行期的 ModelConfiguration：
        // 让它接管就会把高优先层的配置静默吃掉，所以这里必须交回反射那份答案。
        var contract = Generated(model).Contract<Order>();

        Assert.Equal("QuickSqlOrderRenamed", contract.Table.Name);
        Assert.Equal(16, contract.Columns.First(c => c.MemberName == "Note").MaxChars);
        Assert.Equal(Shape(Reflected(model).Contract<Order>()), Shape(contract));

        // 反过来也要成立：没有 Fluent 时 G1 用的是自己算出的表名。
        Assert.Equal("QuickSqlOrder", Generated().Contract<Order>().Table.Name);
    }

    /// <summary>§13.11 的「默认可用不是口号」那一半：同一个实体，一套只写注解、一套把同样的值写成 Fluent，
    /// 除 origin 之外<b>每一格都必须相等</b>（长度量纲、Unicode、可空、自增、存储类型全都要一样）。
    /// origin 反过来<b>必须不等</b>——它按定义记录的是哪一层赢了（§10.2），两侧一致就说明这格是装饰。</summary>
    [Fact]
    public void Annotations_only_and_the_equivalent_Fluent_agree_on_values_and_disagree_on_origin()
    {
        var model = new ModelConfiguration();
        var customer = model.Entity<Customer>();

        customer.ToTable("QuickSqlCustomer");
        customer.Property(c => c.Name).HasMaxLength(64);
        customer.Property(c => c.City).HasColumnName("City");
        customer.Property(c => c.City).HasMaxLength(32);
        customer.Property(c => c.Remark).HasMaxLength(32);

        var viaAnnotation = Reflected().Contract<Customer>();
        var viaFluent = Reflected(model).Contract<Customer>();

        Assert.Equal(Shape(viaAnnotation, withOrigin: false), Shape(viaFluent, withOrigin: false));

        var city = viaFluent.Columns.Single(c => c.MemberName == "City").Origin;

        Assert.Equal(MetadataLayer.Fluent, city.Layer);
        Assert.Contains("Fluent HasColumnName", city.Note);
        Assert.Contains("Fluent HasMaxLength(32)", city.Note);
        Assert.Contains("[QuickColumn(Length=32)]", viaAnnotation.Columns.Single(c => c.MemberName == "City").Origin.Note);

        // 只覆写长度的那一列不该顺手把「列名来自哪一层」也改成 Fluent——层的粒度是按字段而不是按实体。
        Assert.Equal(MetadataLayer.Convention, viaFluent.Columns.Single(c => c.MemberName == "Name").Origin.Layer);

        // 约定命中的主键与 Fluent 无关，两侧连 origin 都必须一模一样。
        Assert.Equal(viaAnnotation.Columns[0].Origin, viaFluent.Columns[0].Origin);
    }

    /// <summary>§13.11 的冲突护栏：两条互斥注解同时出现时必须**抛**，而不是"取更严的那条"。
    /// 取严格者会让建表按 20、校验按 10，事故三个月后才现形（§10.2）。</summary>
    [Fact]
    public void A_same_layer_conflict_is_refused_at_startup_rather_than_resolved_to_the_stricter_one()
    {
        var raised = Checks.Raised<MetadataConflictException>(() => Reflected().Contract<ConflictingLengths>());

        Assert.Equal(nameof(ConflictingLengths.Code), raised.MemberName);
        Assert.Equal(new[] { "[StringLength(10)]", "[MaxLength(20)]" }, raised.Sources);
        Assert.Contains("不自动取更严的那条", raised.Message);
    }

    [Fact]
    public void Sql_is_byte_identical_through_both_registries()
    {
        Assert.Equal(
            Orm(Reflected()).Select<Order>().Where(o => o.State == OrderState.Paid && o.Amount > 100m).OrderByDescending(o => o.CreatedAt).ToSql(),
            Orm(Generated()).Select<Order>().Where(o => o.State == OrderState.Paid && o.Amount > 100m).OrderByDescending(o => o.CreatedAt).ToSql());

        Assert.Equal(
            Orm(Reflected()).Select<LegacyShipment>().Where(s => s.CarrierCode != null).ToSql(),
            Orm(Generated()).Select<LegacyShipment>().Where(s => s.CarrierCode != null).ToSql());
    }

    [Fact]
    public void An_unannotated_entity_still_resolves_through_the_g1_registry()
    {
        // BareReport 没被生成：注册表既不能因此找不到契约，也不能把它当成 G1 命中。
        var columns = Generated().Contract<BareReport>().Columns;

        Assert.Equal("Id", columns[0].ColumnName);
        Assert.True(columns[0].IsPrimary);
        Assert.True(columns[0].IsIdentity);

        // 约定命中的主键会把整条 origin 换掉（不是追加），所以这里钉的是那句人话而不是层名。
        Assert.Equal(MetadataLayer.Convention, columns[0].Origin.Layer);
        Assert.Equal("Id 命中 Id/{Entity}Id 形态", columns[0].Origin.Note);
        Assert.Contains("约定：属性名", columns[1].Origin.Note);
    }
}

/// <summary>同层冲突的负样本（StringLength 与 MaxLength 这一对）。<b>它必须待在本项目而不是
/// QuickSql.Tests.Common</b>：那边挂着源生成器，同样的形状在那边会让编译直接失败（QuickSql001），
/// 而「编译期负样本没有断言口」是 §14 未完项里另一格。换到这里，G2 的启动期抛点才有可跑的用例
///（§13.11）。
/// <para>双 [Table] 那一档的旧负样本不在这里了：两颗注解合成系统的一颗后，「两个 [Table] 抢名」的
/// 形状不再存在于声明层（同名双注解是编译器眼里的 CS0579），冲突护栏只剩口径真正互斥
/// 的同层两格 [StringLength]/[MaxLength]。</para></summary>
[Table("ConflictingLengths")]
public class ConflictingLengths
{
    public int Id { get; set; }

    [Da.StringLength(10)]
    [Da.MaxLength(20)]
    public string? Code { get; set; }
}
