using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Linq.Expressions;
using QuickSql.Metadata;
using QuickSql.Query.Model;
using QuickSql.Schema;

namespace QuickSql.Materialization;

/// <summary>数据库返回值 → CLR 值的单点转换。各家驱动对同一 DbType 给的对象并不一致
/// （SQL Server 的 uniqueidentifier 回 Guid，DM 回 byte[]，金仓的 boolean 可能回 t/f），
/// 所以这一层必须存在，且必须只有一处。</summary>
public static class ValueConverter
{
    /// <summary>按列序号读一颗值。这是物化路径唯一的取数口：实体、DTO 槽位、标量与聚合出口都从这里走
    /// （实体档前面多了一条直读臂，而那条臂的非直读分支就是这一句，见 DirectRead），
    /// 所以"某一列的读法"只有一处可改。它服务的是落不了直读臂的那一半：空值列、枚举、byte[]、
    /// DateOnly/TimeOnly/DateTimeOffset、驱动交回型与声明型不同的列、DTO 槽位与 NULL 那一格。</summary>
    public static T Read<T>(DbDataReader reader, int ordinal)
        => To<T>(reader.GetValue(ordinal));

    public static T To<T>(object? value)
    {
        if (value is null or DBNull)
            return default!;

        if (value is T exact)
            return exact;

        var target = typeof(T);
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (underlying.IsEnum)
        {
            // 整数那一半是七族约定（枚举存整数），字符串那一半接的是两支真实来路：
            // [Column(TypeName=…)] 强制字符串列与遗留库。此前字符串直接 FormatException，
            // 说不出是哪一列哪个枚举；现在认得出就收，认不出给可读的错。
            if (value is string text)
                return Enum.TryParse(underlying, text, ignoreCase: true, out var parsed)
                    ? (T)parsed!
                    : throw new QuickSqlException($"无法把字符串 \"{text}\" 读成枚举 {underlying.Name}：列值与枚举成员名对不上（枚举按约定存整数，字符串列要么是列型强制、要么是遗留库）。");

            return (T)Enum.ToObject(underlying, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        if (underlying == typeof(Guid))
            return (T)(object)ToGuid(value);

        if (underlying == typeof(bool))
            return (T)(object)ToBoolean(value);

        if (underlying == typeof(decimal))
            return (T)(object)Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        if (underlying == typeof(DateOnly) && value is DateTime dateOnly)
            return (T)(object)DateOnly.FromDateTime(dateOnly);

        if (underlying == typeof(TimeOnly) && value is DateTime timeOnly)
            return (T)(object)TimeOnly.FromDateTime(timeOnly);

        if (underlying == typeof(TimeSpan) && value is not TimeSpan)
            return (T)(object)TimeSpan.FromTicks(Convert.ToInt64(value, CultureInfo.InvariantCulture));

        // 带偏移的时刻与裸时刻之间那两折，只折型不折时刻：真 PG 的 timestamptz 交回 Kind=Utc 的 DateTime
        // 而不是 DateTimeOffset，声明 DateTimeOffset? 的成员读它会以 InvalidCastException 收尾（宿主启动即挂）。
        // DateTime→DateTimeOffset 走 BCL 规则（Utc 配零偏移、Local 配它自己的偏移、Unspecified 按本机偏移）；
        // DateTimeOffset→DateTime 一律落 UTC 裸时刻，与写侧同一读法，两边凑回来是同一个瞬时。
        if (underlying == typeof(DateTimeOffset) && value is DateTime moment)
            return (T)(object)new DateTimeOffset(moment);

        if (underlying == typeof(DateTime) && value is DateTimeOffset withOffset)
            return (T)(object)withOffset.UtcDateTime;

        if (underlying == typeof(string))
            return (T)(object)ToText(value);

        return (T)Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    static string ToText(object value) => value switch
    {
        string text => text,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        Guid guid => guid.ToString(),
        _ => value.ToString() ?? string.Empty,
    };

    static bool ToBoolean(object value) => value switch
    {
        bool flag => flag,
        byte bit => bit != 0,

        // 金仓一族的 boolean 可能回 t/f：string 走 Convert 抛 FormatException，char 走 ToInt64
        // 会把 'f'（102）读成 true。当前 PG 血统驱动交回真 bool，但 Oracle 兼容模式那颗口子一开就现形。
        "t" or "T" or "true" or "True" or "TRUE" or "1" or "y" or "Y" or "yes" => true,
        "f" or "F" or "false" or "False" or "FALSE" or "0" or "n" or "N" or "no" => false,
        char c => c is 't' or 'T' or '1' or 'y' or 'Y',
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };

    public static Guid ToGuid(object value) => value switch
    {
        Guid guid => guid,
        byte[] bytes when bytes.Length == 16 => FromMixedEndian16(bytes),
        byte[] bytes when bytes.Length == 36 => Guid.Parse(System.Text.Encoding.ASCII.GetString(bytes)),
        string text => Guid.Parse(text),
        _ => throw new QuickSqlException($"无法把 {value.GetType().Name} 读成 Guid"),
    };

    /// <summary>MySQL 系与部分国产库的 BINARY(16) 是混合字节序：前 4 字节小端、接着 2+2 小端、尾部 8 字节大端。
    /// 能力位 Guid_Raw16 为真时读回来的形态就是这个。</summary>
    public static Guid FromMixedEndian16(ReadOnlySpan<byte> bytes)
    {
        Span<byte> normalized = stackalloc byte[16];
        bytes[..16].CopyTo(normalized);

        Reverse(normalized, 0, 4);
        Reverse(normalized, 4, 2);
        Reverse(normalized, 6, 2);

        return new Guid(normalized);

        static void Reverse(Span<byte> span, int start, int length) => span.Slice(start, length).Reverse();
    }
}

/// <summary>结果行 → 对象。形状由 ProjectionPlan 决定，所以它属于物化层而不是方言层。</summary>
public static class Materializer
{
    /// <summary>按投影形态取行读取器。只有 DTO 分支需要反射与表达式树编译：实体走注册表里已编译的读取器、
    /// 标量/聚合直读第 0 列，两条都是零反射；DTO 那条按形状进 DtoReaders，每个形状只编译一次。
    /// 标注打在分派口而不是整个类上，为的是「AOT 只禁掉 DTO 投影」在警告里看得见；
    /// 缓存不会让这两条标注变窄——静态分析看的是"这里可能编译"，不是"这次命中了"。</summary>
    [RequiresUnreferencedCode("ObjectProjection 的组装按运行期 Type 反射构造器与成员。")]
    [RequiresDynamicCode("ObjectProjection 的组装用表达式树编译委托，AOT 下不可用。")]
    public static Func<DbDataReader, T> Create<T>(QueryModel model, IEntitySchemaRegistry registry)
    {
        switch (model.Plan)
        {
            case EntityProjection entity:
            {
                var table = model.Tables[entity.TableIndex];
                var contract = table.Contract ?? throw new QuickSqlException($"表位 {table.Alias} 没有实体契约，无法物化");

                // LINQ 的 join/let 只换「元素形状」，投影计划可以还是实体那一行，而调用方要的类型是
                // 编译器造的匿名类型。这里不挡就会走到 (T) 强转，吐一句 InvalidCastException 说不清是谁的错。
                if (!typeof(T).IsAssignableFrom(contract.ClrType))
                    throw new QuickSqlException(
                        $"查询返回的是 {contract.ClrType.Name} 的整行，而结果类型是 {typeof(T).Name}：把整行放进投影（select new {{ o, c }} 这类）" +
                        "要 §5.7 的嵌套对象加载，本版本未接入。请投影成列（select new { o.Id, c.City }），或只投影一个实体（select c）。");

                var read = registry.ReadDelegate(contract.ClrType);

                // 实体投影的选列就是契约列序（QueryCompiler.EntityColumns），所以 reader 序号 0..n-1 一一对应。
                return EntityRows<T>(read, contract);
            }

            case ValueProjection:
            case AggregateProjection:
                return ScalarRows<T>();

            case ObjectProjection projection:
            {
                // 键里只有委托体真正依赖的那四项：谓词/表/别名进不了委托体，把它们算进键就等于
                // 同一处投影按参数各编一次——那正是要修的东西。
                var key = new ReaderKey(typeof(T), projection);

                if (DtoReaders.TryGetValue(key, out var cached))
                    return (Func<DbDataReader, T>)cached;

                var assembled = Assemble<T>(projection);

                DtoReaders[key] = assembled;

                return assembled;
            }

            default:
                throw new QuickSqlException("未知投影形态：" + model.Plan.GetType().Name);
        }
    }

    /// <summary>把注册表那条按票读委托包成"每场次探一次"的行读取器。探测的时机是第一次拿到 reader 而不是
    /// 构建时——Create 返回时连接还没打开，无从问驱动"这一列交回什么类型"；票据挂在闭包上、按 reader 引用
    /// 判要不要重探，同一趟读里每行只多一次引用比较，换一只 reader 就重探一张。并发口径：一张票是可变状态，
    /// 前提是"一个返回的 Func 只在一趟顺序读的循环里被调"（IAdo 的实际形状，逐行 await 不并发）；
    /// 要把同一个 Func 并发喂两只 reader，得先换成每场次一只对象，而不是在这里加锁。
    /// 标量/聚合档不在这里，它有自己的 ScalarRows。</summary>
    public static Func<DbDataReader, T> EntityRows<T>(Func<DbDataReader, ReadPlan, object> read, EntityContract contract, int startOffset = 0)
    {
        DbDataReader? probed = null;
        ReadPlan plan = default;    // 第一次调用必然重探（probed 还是 null，而 reader 不是），所以这一格永远是占位

        return reader =>
        {
            if (!ReferenceEquals(probed, reader))
            {
                probed = reader;
                plan = ReadOps.Build(reader, contract, startOffset);
            }

            return (T) read(reader, plan);
        };
    }

    /// <summary>标量与聚合那一档的行读取器：第 0 列也领一张票。与实体档唯一的分别是空值守归谁问——
    /// 实体侧成员声明自带可空性（int 与 int? 是两支臂），而这里的 T 只是调用方要的形状：Sum 打在空集上、
    /// Max 打在全空那一列上，库交回的就是 NULL，那是合法答案而不是模型在说谎，所以非空值类型的臂在这一档
    /// 先在门外问一次空值、交回 default；string 与 Nullable 那两族的臂自己问，这里不再问第二遍
    /// （那会用两趟驱动调用换一趟的答案）。探测仍是每场次一颗 GetFieldType：聚合恒定一行、与省下的盒
    /// 同场抵消，真正值它的是多行的标量流。</summary>
    public static Func<DbDataReader, T> ScalarRows<T>()
    {
        DbDataReader? probed = null;
        byte op = ReadOps.Boxed;

        return reader =>
        {
            if (!ReferenceEquals(probed, reader))
            {
                probed = reader;
                op = ReadOps.Probe(reader, 0, typeof(T));
            }

            if (op == ReadOps.Boxed)
                return ValueConverter.Read<T>(reader, 0);

            return ScalarArm<T>.GuardsNull && reader.IsDBNull(0)
                ? default!
                : ScalarArm<T>.Read(reader, 0, op);
        };
    }

    /// <summary>标量出口按 T 每一支实例化只落一次的臂（静态泛型字段就是它的缓存）。臂的句柄来自
    /// DirectRead.ArmFor：typeof(T) 是 int 的那支实例化里，表上那只 Func 与目标型是同一个运行期类型，
    /// 这一次转换不是装箱再拆而是同名委托的原样通过——每值仍零装箱，只多一次间接调用。没有臂的那些型
    /// （枚举、byte[]、DateOnly/TimeOnly/DateTimeOffset/TimeSpan、object）交回门本身：它们的票恒为 Boxed，
    /// 那一句实际读不到第三颗参数——留着它是为了守住「挑错臂的方向是没有变快，不是读错值」那条约定。</summary>
    static class ScalarArm<T>
    {
        public static readonly Func<DbDataReader, int, byte, T> Read =
            DirectRead.ArmFor(typeof(T)) as Func<DbDataReader, int, byte, T> ?? Door;

        static T Door(DbDataReader reader, int ordinal, byte op) => ValueConverter.Read<T>(reader, ordinal);

        /// <summary>这一档需不需要替臂问那一次空值：只有非空值类型要。string 与 Nullable&lt;T&gt; 的臂自己问，
        /// 这里再问就是重复付一趟驱动调用。</summary>
        public static readonly bool GuardsNull =
            typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;
    }

    // ── Include 物化 ─────────────────────────────────────────────────────

    /// <summary>带急切加载的物化器。结果集被编译器按深度优先遍历展宽：根实体列在 0..n-1，
    /// 每个 Include 的列紧随其后（IncludeNode.ColumnOffset）。启动期一次性构建物化计划
    /// （读委托、列偏移、导航属性设置器），之后每行只走已编译路径。</summary>
    [RequiresUnreferencedCode("Include 物化按运行期 Type 解析导航属性并反射设置。")]
    [RequiresDynamicCode("Include 物化路径走 G2 兜底注册表。")]
    public static Func<DbDataReader, T> CreateWithIncludes<T>(QueryModel model, IEntitySchemaRegistry registry)
    {
        var entity = model.Plan as EntityProjection
            ?? throw new QuickSqlException("Include 物化只支持实体投影，当前投影形态：" + model.Plan.GetType().Name);

        var rootTable = model.Tables[entity.TableIndex];
        var rootContract = rootTable.Contract
            ?? throw new QuickSqlException($"表位 {rootTable.Alias} 没有实体契约，无法物化 Include");

        var rootRead = registry.ReadDelegate(rootContract.ClrType);
        var rootRows = EntityRows<T>(rootRead, rootContract);
        var childPlans = BuildIncludePlans(model.Includes, registry, rootContract.ClrType);

        return reader =>
        {
            var root = rootRows(reader);

            // 读取委托交回的永远是一只新建的实体，不会是 null；这里的 ! 只补可空性注解看不出来的那一件事
            // （T 上没有 class 约束，编译器不知道那条不变式）。
            for (var i = 0; i < childPlans.Length; i++)
                childPlans[i].Materialize(reader, root!);

            return root;
        };
    }

    [RequiresUnreferencedCode("Include 物化按运行期 Type 解析导航属性并反射设置。")]
    [RequiresDynamicCode("Include 物化路径走 G2 兜底注册表。")]
    static IncludePlan[] BuildIncludePlans(
        IReadOnlyList<IncludeNode> includes, IEntitySchemaRegistry registry, Type parentClrType)
    {
        var plans = new IncludePlan[includes.Count];

        for (var i = 0; i < includes.Count; i++)
        {
            var node = includes[i];
            var contract = node.TargetTable.Contract
                ?? throw new QuickSqlException($"Include 目标表 {node.TargetTable.Alias} 没有实体契约");

            var read = registry.ReadDelegate(contract.ClrType);

            // 与跟踪器侧的 RequireKey 同一口的可读拒绝：空数组直接取 [0] 是一句裸下标异常，
            // 说不出是"这个实体没有主键所以 Include 不了"。
            if (contract.PrimaryKey.Length == 0)
                throw new QuickSqlException($"Include 目标实体 {contract.ClrType.Name} 没有主键：Include 靠主键判\"这一行是不是 NULL 行\"，无主键的实体接不上（§8.2 的无 PK 自动不跟踪同一裁决）。");

            var firstPk = contract.PrimaryKey[0];
            var firstPkIndex = Array.IndexOf(contract.Columns, firstPk);
            var nullCheckOrdinal = node.ColumnOffset + firstPkIndex;

            var setter = ResolveNavigationSetter(parentClrType, node.NavigationMemberName);
            var children = BuildIncludePlans(node.Children, registry, contract.ClrType);

            plans[i] = new IncludePlan(read, contract, node.ColumnOffset, nullCheckOrdinal, setter, children);
        }

        return plans;
    }

    [RequiresUnreferencedCode("Include 物化按运行期 Type 解析导航属性。")]
    [RequiresDynamicCode("Include 物化的导航设置器是编译出来的委托。")]
    static Action<object, object?> ResolveNavigationSetter(Type parentType, string memberName)
        => MemberAccessors.Setter(
            parentType, memberName,
            $"实体 {parentType.Name} 上没有名为 {memberName} 的导航属性。" +
            "Include 要求导航属性是公共可写属性。",
            $"实体 {parentType.Name}.{memberName} 没有公共写口。Include 要求导航属性是公共可写属性。");

    /// <summary>一个 Include 节点的预编译物化计划。持有读委托、契约、列偏移、空值检测序号、导航设置器
    /// 与子计划。Materialize 在每行上调用，全程零反射；直读票按 reader 引用探一次、这一趟复用。</summary>
    sealed class IncludePlan(
        Func<DbDataReader, ReadPlan, object> read,
        EntityContract contract,
        int startOffset,
        int nullCheckOrdinal,
        Action<object, object?> setter,
        IncludePlan[] children)
    {
        DbDataReader? probed;
        ReadPlan plan;

        public void Materialize(DbDataReader reader, object parent)
        {
            if (reader.IsDBNull(nullCheckOrdinal))
                return;

            if (!ReferenceEquals(probed, reader))
            {
                probed = reader;
                plan = ReadOps.Build(reader, contract, startOffset);
            }

            var child = read(reader, plan);

            setter(parent, child);

            for (var i = 0; i < children.Length; i++)
                children[i].Materialize(reader, child);
        }
    }

    /// <summary>DTO 读取委托的指纹缓存。进程级而不是挂在 QuickSqlOrm 上：委托与注册表、方言、连接串都无关，
    /// 挂到实例上只会让每个 orm 各编译一份同一个形状。用 ConcurrentDictionary 是这条路径的性质决定的——
    /// 形状随首次执行惰性出现，与"一次构建完再冻结"的容器规则不兼容。</summary>
    static readonly ConcurrentDictionary<ReaderKey, Delegate> DtoReaders = new();

    /// <summary>缓存键。相等判定与哈希都是手写的：record 自动生成那一版对数组字段比的是引用，
    /// 形状相同、数组不同的两次查询永远判成不等——缓存静默不命中，而它在基准上只表现为"没优化"，
    /// 不会有任何人来查这一格。手写版比的是逐项形状，代价是命中路径上多一个小数组键。</summary>
    readonly record struct ReaderKey(Type TargetType, Type ResultType, bool ViaConstructor, (string Name, Type MemberType)[] Slots)
    {
        public ReaderKey(Type targetType, ObjectProjection projection)
            : this(targetType, projection.ResultType, projection.ViaConstructor,
                   [.. projection.Slots.Select(s => (s.Name, s.MemberType))])
        {
        }

        public bool Equals(ReaderKey other)
            => TargetType == other.TargetType && ResultType == other.ResultType
               && ViaConstructor == other.ViaConstructor && Slots.SequenceEqual(other.Slots);

        public override int GetHashCode()
        {
            var hash = new HashCode();

            hash.Add(TargetType);
            hash.Add(ResultType);
            hash.Add(ViaConstructor);

            foreach (var (name, memberType) in Slots)
            {
                hash.Add(name, StringComparer.Ordinal);
                hash.Add(memberType);
            }

            return hash.ToHashCode();
        }
    }

    [RequiresUnreferencedCode("按运行期 Type 找构造器与可写成员。")]
    [RequiresDynamicCode("编译装配 DTO 的表达式树。")]
    static Func<DbDataReader, T> Assemble<T>(ObjectProjection projection)
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "reader");
        var read = typeof(ValueConverter).GetMethod(nameof(ValueConverter.Read), BindingFlags.Public | BindingFlags.Static, null, [typeof(DbDataReader), typeof(int)], null)!;

        var locals = new List<ParameterExpression>();
        var slotValues = new List<Expression>();
        var body = new List<Expression>();

        for (var i = 0; i < projection.Slots.Count; i++)
        {
            var slot = projection.Slots[i];
            var local = Expression.Variable(slot.MemberType, "slot" + i);

            locals.Add(local);
            slotValues.Add(local);
            body.Add(Expression.Assign(local, Expression.Call(read.MakeGenericMethod(slot.MemberType),
                reader, Expression.Constant(i))));
        }

        var result = Expression.Variable(projection.ResultType, "result");
        locals.Add(result);

        Expression creation;

        if (projection.ViaConstructor)
        {
            var ctor = projection.ResultType.GetConstructors()
                           .SingleOrDefault(c => c.GetParameters().Length == projection.Slots.Count)
                       ?? throw new QuickSqlException($"类型 {projection.ResultType.Name} 没有 {projection.Slots.Count} 参数的构造器");

            creation = Expression.New(ctor, slotValues);
        }
        else
        {
            // 无参那一颗不问可见性（同一颗判据，见 MemberAccessors.ParameterlessConstructor）；
            // 上面 ViaConstructor 那一臂照旧只认公共构造器 —— 参数表是协议，那一族不改
            var defaultCtor = MemberAccessors.ParameterlessConstructor(projection.ResultType);

            if (defaultCtor is null && !projection.ResultType.IsValueType)
                throw new QuickSqlException(
                    $"类型 {projection.ResultType.Name} 没有无参构造器，不能用成员初始化投影（公共与非公共都查过）");

            // struct 那一侧反射通常拿不到"无参构造器"这颗（record struct 与不写构造器的 struct 都没有可查的
            // 那一个），而 C# 的 `new S { X = … }` 合法 —— 表达式树用无 ctor 的 New 就是 initobj，
            // 与那条语义同一件事。
            var seed = projection.ResultType.IsValueType
                ? Expression.New(projection.ResultType)
                : Expression.New(defaultCtor!);

            var bindings = new List<MemberBinding>();

            for (var i = 0; i < projection.Slots.Count; i++)
            {
                var slot = projection.Slots[i];
                MemberInfo? member = (MemberInfo?)projection.ResultType.GetProperty(slot.Name) ?? projection.ResultType.GetField(slot.Name);

                if (member is null)
                    throw new QuickSqlException($"{projection.ResultType.Name} 上没有可写的成员 {slot.Name}");

                var target = member switch
                {
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    _ => slot.MemberType,
                };

                bindings.Add(Expression.Bind(member, Expression.Convert(slotValues[i], target)));
            }

            creation = Expression.MemberInit(seed, bindings);
        }

        body.Add(Expression.Assign(result, creation));
        body.Add(projection.ResultType == typeof(T) ? result : (Expression)Expression.Convert(result, typeof(T)));

        return Expression.Lambda<Func<DbDataReader, T>>(Expression.Block(locals, body), reader).Compile();
    }
}
