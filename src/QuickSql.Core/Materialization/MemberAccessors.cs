using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using QuickSql.Tracking;

namespace QuickSql.Materialization;

/// <summary>「加载可以反射、调用不行」在急切加载与跟踪路径上的兑现处。取一个成员的当前值、给一个成员写值、
/// 往一个集合里追加一颗——三件事都只在建计划那一次按名字定位成员，之后每行拿到的是已编译委托，
/// 调用是普通方法调用、不是反射调度。报错文本一律由调用方给：这一支不知道自己是在找主键属性还是在外键
/// 列对应的属性，「哪一列、哪个实体、为什么找不到」只有调用方说得准；唯一的例外是集合导航那一支里
/// 「写入口不是公共的」两格——调用方那句断言的是「成员不存在」而成员明明在场，写口可见性与集合位
/// 空不空只有这一支知道，那两句由它现拼。</summary>
[RequiresUnreferencedCode("按运行期类型与成员名定位属性并取它的读口与写口。")]
[RequiresDynamicCode("把成员读写与集合追加编译成委托。")]
public static class MemberAccessors
{
    // ── 编译产物的进程级缓存 ─────────────────────────────────────────────
    // 三张表的键都只有委托体真正依赖的那几项（声明类型、成员名、元素型），不含调用方递进来的报错文本：
    // 文本只在"成员找不到/写口不可绑"那一次抛里用到，而那一抛发生在进缓存之前。Include / IncludeMany /
    // 拆分加载 / DbSet.Load 四条路都在每次执行调这里，没有缓存时每次执行各付一轮反射定位 + Compile
    // （数十 µs 起），契约构建那一次从此与执行期共用同一份产物。

    static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName), Func<object, object>> Getters = new();

    static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName), Action<object, object?>> Setters = new();

    static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName, Type ElementType), CollectionBindings> Collections = new();

    /// <summary>读一颗公共实例属性的值，返回装箱后的 object（当字典键用）。属性挂在基类上时委托里转的是
    /// 那个基类而不是派生型——与「按名字找成员」的原写法同形。missingMessage 是属性不存在时的报错文本，
    /// noGetterMessage 是属性存在但没有公共读口时的报错文本。</summary>
    public static Func<object, object> Getter(Type declaringType, string memberName, string missingMessage, string? noGetterMessage = null)
    {
        var key = (DeclaringType: declaringType, MemberName: memberName);

        // 命中路径先 TryGetValue：值类型键不装箱、不分配，连 GetOrAdd 那颗工厂元组都不造。
        if (Getters.TryGetValue(key, out var hit))
            return hit;

        return Getters.GetOrAdd(key,
            static (k, messages) => GetterCore(k.DeclaringType, k.MemberName, messages.missingMessage, messages.noGetterMessage),
            (missingMessage, noGetterMessage));
    }

    static Func<object, object> GetterCore(Type declaringType, string memberName, string missingMessage, string? noGetterMessage)
    {
        var read = (declaringType.GetProperty(memberName)
                    ?? throw new QuickSqlException(missingMessage)).GetGetMethod()
                   ?? throw new QuickSqlException(noGetterMessage ?? missingMessage);

        var entity = Expression.Parameter(typeof(object), "o");

        return Expression
            .Lambda<Func<object, object>>(
                Expression.Convert(
                    Expression.Call(Expression.Convert(entity, read.DeclaringType!), read),
                    typeof(object)),
                entity)
            .Compile();
    }

    /// <summary>给一颗公共实例属性写值。写入前把 object 转成成员的声明类型，传错类型进来当场抛
    /// InvalidCastException——差别只是「问成员是什么型」挪到了建计划时，不在每行问。
    /// missingMessage 是属性不存在时的报错文本，noSetterMessage 是没有公共写口时的报错文本。</summary>
    public static Action<object, object?> Setter(Type declaringType, string memberName, string missingMessage, string? noSetterMessage = null)
    {
        var key = (DeclaringType: declaringType, MemberName: memberName);

        if (Setters.TryGetValue(key, out var hit))
            return hit;

        return Setters.GetOrAdd(key,
            static (k, messages) => SetterCore(k.DeclaringType, k.MemberName, messages.missingMessage, messages.noSetterMessage),
            (missingMessage, noSetterMessage));
    }

    static Action<object, object?> SetterCore(Type declaringType, string memberName, string missingMessage, string? noSetterMessage)
    {
        var property = declaringType.GetProperty(memberName) ?? throw new QuickSqlException(missingMessage);
        var write = property.GetSetMethod() ?? throw new QuickSqlException(noSetterMessage ?? missingMessage);
        var entity = Expression.Parameter(typeof(object), "o");
        var value = Expression.Parameter(typeof(object), "v");

        return Expression
            .Lambda<Action<object, object?>>(
                Expression.Call(
                    Expression.Convert(entity, write.DeclaringType!), write,
                    Expression.Convert(value, property.PropertyType)),
                entity, value)
            .Compile();
    }

    /// <summary>集合导航的级联入口：把「拿这只集合送进子端会话」那一次泛型调用编译成委托。
    /// MakeGenericMethod 只在这里出现一次（建契约那一次，启动期），级联每行拿到的是已编译委托；
    /// 交给 ICascadeSink 而不是返回 IEnumerable&lt;object&gt;，是因为跟踪器按类型分格（For&lt;TChild&gt;()），
    /// 那一格的类型只有在这一次泛型方法绑定里才拿得到。missingMessage 是属性不存在或没有公共读口时的报错文本。</summary>
    public static Action<ICascadeSink, object> CascadeEnter(Type parentType, Type childType, string memberName, string missingMessage)
    {
        var read = (parentType.GetProperty(memberName)
                    ?? throw new QuickSqlException(missingMessage)).GetGetMethod()
                   ?? throw new QuickSqlException(missingMessage);

        var enter = typeof(ICascadeSink).GetMethod(nameof(ICascadeSink.Enter))!
            .MakeGenericMethod(parentType, childType);

        var sink = Expression.Parameter(typeof(ICascadeSink), "sink");
        var owner = Expression.Parameter(typeof(object), "o");
        var parent = Expression.Convert(owner, parentType);

        return Expression
            .Lambda<Action<ICascadeSink, object>>(
                Expression.Call(
                    sink, enter,
                    parent,
                    Expression.Convert(Expression.Call(parent, read), typeof(IEnumerable<>).MakeGenericType(childType)),
                    Expression.Constant(memberName, typeof(string))),
                sink, owner)
            .Compile();
    }

    /// <summary>集合导航的一整套访问器：读集合位当前的值、写回一只新集合、往里追加一颗、按元素型建一只空集合。
    /// 四件事共用同一次成员定位，一个节点只反射一次；元素型在建计划时就知道，运行期不再按每颗子行的实际类型
    /// 找追加口——原先找不到是静默不加，这里改成建计划时就抛：静默丢一颗子行不是可接受的失败形状。
    /// 写入口不是公共的那一岔（protected set 是完全合法的集合导航形状）按形状分岔：属性类型自己收得到追加口
    /// 就往 getter 交回的那只集合里追加，缝合那条路根本不碰写口；两样都没有就当场抛，文案说清缺的到底是
    /// 哪一样形状。公共写口那一臂一字不易：已经能工作的实体不为这一改换任何东西。</summary>
    public static CollectionBindings BindCollection(Type declaringType, string memberName, Type elementType, string missingMessage)
    {
        var key = (DeclaringType: declaringType, MemberName: memberName, ElementType: elementType);

        if (Collections.TryGetValue(key, out var hit))
            return hit;

        return Collections.GetOrAdd(key, static (k, message) => BindCollectionCore(k.DeclaringType, k.MemberName, k.ElementType, message), missingMessage);
    }

    static CollectionBindings BindCollectionCore(Type declaringType, string memberName, Type elementType, string missingMessage)
    {
        var property = declaringType.GetProperty(memberName) ?? throw new QuickSqlException(missingMessage);
        var listType = typeof(List<>).MakeGenericType(elementType);

        // 声明类型自己收得到追加口就用声明类型（ICollection<T> 与 List<T> 都在这一臂），
        // 收不到（IEnumerable<T> 那一型）才退到当场新建的 List<T>：那条路上集合位为空的父可以正常缝，
        // 而集合位已经装着别的形状的父会当场抛，不是静默少一颗。
        var appendable = property.PropertyType.GetMethod("Add", [elementType]) is not null;
        var target = appendable ? property.PropertyType : listType;

        var add = target.GetMethod("Add", [elementType])
                  ?? throw new QuickSqlException($"{missingMessage}（{target.Name} 上找不到接受 {elementType.Name} 的追加口）");

        var getter = Getter(declaringType, memberName, missingMessage);

        if (property.GetSetMethod() is not null)
        {
            return new CollectionBindings(
                getter,
                Setter(declaringType, memberName, missingMessage),
                Adder(target, elementType),
                listType);
        }

        // 没有公共写口。可追加的那一臂：Attach 往现成集合里追加，永远走不到写口这一格；写口委托唯一的活
        // 是真被调用到的那一次把当场的原因说准——集合位是 null 就报 null 那一格（缝不上去的真实形状是
        // 「写口不可见 ∧ getter 交回空串」，两句都要点到），集合位有人就报「这一档要整只写回」
        // （补载那一条路的形状，就地追加够不着它）。两句话都不再谎称「成员不存在」。
        if (appendable)
        {
            return new CollectionBindings(
                getter,
                NullWriteMouth(getter,
                    $"{declaringType.Name}.{memberName} 缝不上子实体：写入口不是公共的（set 是 protected 或 private），" +
                    $"而此刻 getter 交回的集合位是 null，没有现成集合可以就地追加。出路二选一：把写口放开为公共的，" +
                    $"或建实体时把导航初始化成可追加集合，例如 {RequiredShape(elementType, memberName)}。",
                    $"{declaringType.Name}.{memberName} 写不回整只集合：这一档要按整串替换（补载），而写入口不是公共的" +
                    $"（set 是 protected 或 private）。出路二选一：给这根导航一个公共写口，或让它长成建实体时就初始化好的" +
                    $"可追加集合（{RequiredShape(elementType, memberName)} 那一形，IncludeMany 的缝合走就地追加那条路）。"),
                Adder(target, elementType),
                listType);
        }

        // 两样都没有：只读属性交回的又是一副收不了追加的形状（IReadOnlyList、数组、裸 IEnumerable 这些），
        // 写不回来也追加不进 —— 这一格在建绑定的当场就响，文案点名到底是哪副形状不对。
        throw new QuickSqlException(
            $"{declaringType.Name}.{memberName} 挂不上子实体：这根属性没有公共写口，而它的形状 {Readable(property.PropertyType)}" +
            $" 上也没有接受 {elementType.Name} 的追加口 —— 既写不回整只集合，也追加不进现成集合。需要的形状二选一：" +
            $"带公共写口的集合属性，或建实体时已初始化成可追加集合的导航（{RequiredShape(elementType, memberName)}）。");
    }

    /// <summary>没有公共写口那一臂的「写口」位：正常缝合（就地追加）永远碰不到它；被碰到即这一档确实需要
    /// 写回整只集合，此刻按 getter 交回的到底是什么来挑那句真话。</summary>
    static Action<object, object?> NullWriteMouth(Func<object, object> getter, string nullSlotMessage, string replacementMessage)
        => (owner, _) => throw new QuickSqlException(getter(owner) is null ? nullSlotMessage : replacementMessage);

    /// <summary>报错文本里替调用方把「该长成什么样」写出来。</summary>
    static string RequiredShape(Type elementType, string memberName)
        => $"public List<{elementType.Name}> {memberName} {{ get; protected set; }} = [];";

    /// <summary>IReadOnlyList&lt;ShopCert&gt; 这样的形状名：Type.Name 交回的是 IReadOnlyList`1，
    /// 报给人看之前把反引号那一截换成尖括号参数表。</summary>
    static string Readable(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var head = type.Name[..type.Name.IndexOf('`')];

        return $"{head}<{string.Join(", ", type.GetGenericArguments().Select(Readable))}>";
    }

    /// <summary>往集合里追加一颗，集合与元素都从 object 进来；转成目标类型这一步由委托里的
    /// 一次显式转换承担，所以运行期没有方法查找。</summary>
    static Action<object, object> Adder(Type collectionType, Type elementType)
    {
        var add = collectionType.GetMethod("Add", [elementType])!;
        var collection = Expression.Parameter(typeof(object), "c");
        var item = Expression.Parameter(typeof(object), "i");

        return Expression
            .Lambda<Action<object, object>>(
                Expression.Call(
                    Expression.Convert(collection, add.DeclaringType!), add,
                    Expression.Convert(item, elementType)),
                collection, item)
            .Compile();
    }

    /// <summary>按类型建一只实例（用它的无参构造器），编译期就把 Activator 那次查找换成一次 new。</summary>
    static Func<object> New(Type type)
    {
        var ctor = ParameterlessConstructor(type)
                   ?? throw new QuickSqlException(
                       $"{type.Name} 没有无参构造器，装配不出实例（公共与内部、受保护、私有都查过）。");

        var body = Expression.Convert(Expression.New(ctor), typeof(object));

        return Expression.Lambda<Func<object>>(body).Compile();
    }

    /// <summary>无参构造器：装配用的那一颗，不问可见性——公共查不到就退到内部/受保护/私有。
    /// 物化要的形状是「造得出一个空壳、字段初始化器已就位」，而 protected X() + 有参创建构造器是常见的
    /// 实体写法，把可见性当门槛得到的不是"更安全"，是整颗实体读写两侧一起拒。带参的那一颗不在这格：
    /// 构造器 DTO 与 record 走 Materializer 的 ViaConstructor 臂、仍只认公共构造器——参数表是协议，
    /// 把私有构造器当协议会在别人改内部实现时悄悄换掉装配形状。两条边界免得拿错工具：
    /// Activator.CreateInstance&lt;T&gt;() 对非公共无参构造抛 MissingMethodException（它要的是 new() 那一档）；
    /// RuntimeHelpers.GetUninitializedObject 出实例但不跑构造器也不跑字段初始化器，
    /// 「Xxx { get; set; } = []」那一格留 null——正是集合导航最怕的形状；
    /// Expression.New(非公共 ctor).Compile() 两样都对，所以走这一条。</summary>
    public static ConstructorInfo? ParameterlessConstructor(Type type)
        => type.GetConstructor(Type.EmptyTypes)
           ?? type.GetConstructor(
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
               binder: null,
               types: Type.EmptyTypes,
               modifiers: null
           );

    /// <summary>集合导航的四把委托，挂在节点计划上：每行只调用、不反射。</summary>
    [RequiresUnreferencedCode("按运行期类型与成员名定位集合属性。")]
    [RequiresDynamicCode("集合的四把委托是编译出来的表达式树。")]
    public sealed class CollectionBindings(
        Func<object, object> getter,
        Action<object, object?> setter,
        Action<object, object> add,
        Type listType)
    {
        /// <summary>集合位当前的值（未初始化时为 null）。</summary>
        public Func<object, object> Getter { get; } = getter;

        /// <summary>把一整只集合写回导航属性。</summary>
        public Action<object, object?> Setter { get; } = setter;

        /// <summary>往集合里追加一颗。</summary>
        public Action<object, object> Add { get; } = add;

        /// <summary>建一只空的元素型表，供「先攒后写回」那一档用。</summary>
        public Func<object> NewCollection { get; } = New(listType);

        /// <summary>追加一颗；集合位还是空的就先建一只、写回去、再追加。</summary>
        public void Attach(object parent, object child)
        {
            var collection = Getter(parent);

            if (collection is null)
            {
                collection = NewCollection();
                Setter(parent, collection);
            }

            Add(collection, child);
        }
    }
}
