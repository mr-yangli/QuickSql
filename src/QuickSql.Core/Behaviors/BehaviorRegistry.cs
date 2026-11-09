using System.Linq.Expressions;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Behaviors;

/// <summary>库里预置的写侧行为（「Delete 语句改写为 UPDATE 置位」的入口子，今天只有 SoftDelete 一件）。
/// 组装根上的 orm.Behaviors 就是这一只，一个 orm 实例一份，与 orm.Filters 同一个姿势（注册在启动期做完，
/// 之后每条语句读它）。它与 orm.Filters 是两只对象、各管一半、缺一不可：过滤器管「读到的集合里有没有
/// 已删的行」，行为管「Delete 落库里的是哪一句」——只注册过滤器不注册行为，orm.Delete 仍把行删掉、
/// 那一格永远没人置位；只注册行为不注册过滤器，置位了读侧照样把那几行读出来。改写落在 AST 层而不是
/// 文本层，判的是那句话的目的：「日志与审计看到的必须是真正发出去的那一句」——文本层改写要自己重做
/// 五族各一份的发射器（UPDATE 语法、参数登记顺序、目标自引用名），AST 层改的是语句形状、发射仍由方言的
/// Emit 完成，改出来的文本照样要过 ApplyCud 与 ApplyBinding，Executed 与 AuditDiff 拿到的仍是最终文本。
/// 三处删除出口都接这一只：链式 orm.Delete、批量 orm.DeleteRange 与 SaveChanges 里被摘掉的行——
/// 「这张表是软删表」是按实体成立的事实、不是按调用姿势成立的，只接一家会留下「同一张表，
/// 走 A 留住行、走 B 丢行」那个洞。</summary>
public sealed class BehaviorRegistry
{
    readonly QuickSqlOrm _orm;

    /// <summary>与 FilterRegistry 同一条读写比例：注册一次、每条删除语句读一次，所以写侧加锁换整表、
    /// 读侧不进锁（HasAny 落在热位上，进锁就等于让没注册行为的项目在每次删除上付一次 monitor）。</summary>
    readonly Lock _gate = new();

    volatile Rule[] _items = [];

    internal BehaviorRegistry(QuickSqlOrm orm) => _orm = orm;

    /// <summary>有没有任何一个行为在册。三处删除出口靠它在没人注册时整段短路：不短路的话
    /// 「物理删除」那条老路径多付一次数组遍历与两列解析，写侧基线会为一条没有行为的链多记账。</summary>
    internal bool HasAny => _items.Length > 0;

    /// <summary>把 TEntity（可以是接口或抽象基类）的删除改写成「置位那一格 + 记时间那一格」的 UPDATE。
    /// 注册之后这条链上不再有物理删除，DeletePlan.Reason 与 Executed 日志都按改写后的那一句自陈。
    /// deleted 是标记列的读法（t => t.Deleted）、不是谓词：库往那一格写的值恒为 true，那一列必须是 bool，
    /// 要写 1 / "Y" 那一类标记就不是这一档；注册这一趟会对具体类键当场向契约认一遍那一列（列在不在、
    /// 类型对不对、是不是自增或并发列），接口与抽象基类键认不了（没有列契约），失败点挪到第一次真贴上某张表时。
    /// at 是置位时间列（t => t.DeletedAt），DateTime 与 DateTime? 两种列都认，null 就是只置位、不记时间。
    /// clock 是时间值从哪儿来，缺省本地时钟——本库不替调用方决定本地还是 UTC，要 UTC 就递 () => DateTime.UtcNow；
    /// 读它的时机是「构建那一句」而不是「注册那一句」：注册期把值冻下来，等于后面所有删除记同一个时间。</summary>
    public BehaviorRegistry SoftDelete<TEntity>(
        Expression<Func<TEntity, bool>> deleted,
        Expression<Func<TEntity, DateTime?>>? at = null,
        Func<DateTime>? clock = null) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(deleted);

        var key = typeof(TEntity);

        var rule = new Rule(
            key,
            MemberOf(deleted.Body, "deleted"),
            at is null ? null : MemberOf(at.Body, "at"),
            clock ?? LocalNow);

        // 具体类键在注册期就把两列各认一遍：认不出列、类型不对、撞上自增或并发列，都在这里响，
        // 不等第一次删。接口与抽象基类键没有契约可查（注册表也没有"列出全部实现类"那个口子），
        // 所以那一档把失败点挪到 Rewrite 里，与 FilterRegistry.Probe 欠的是同一半。
        if (!key.IsInterface && !key.IsAbstract)
            rule.Columns(_orm.Schemas.Contract(key));

        lock (_gate)
        {
            // 同一把键注册两次当场拒。"两张册子对同一张表各说一种写法"时谁赢没有任何依据；
            // 而接口键与具体类键的重叠不在这一格里（那种重叠至少说得出"先注册的是哪一句"，见 RuleFor）。
            foreach (var existing in _items)
                if (existing.Key == key)
                    throw new QuickSqlException(
                        $"{key.Name} 的软删已经注册过一次（置位列 {existing.DeletedMember}，" +
                        (existing.AtMember is null ? "没有时间列" : $"时间列 {existing.AtMember}") +
                        "）：同一张表上注册两次不会后一次盖掉前一次，仍然生效的是第一次那一句。" +
                        "要换写法就换一只 orm 实例（册子挂在实例上），或者把两处合成一处。");

            _items = [.. _items, rule];
        }

        return this;
    }

    static DateTime LocalNow() => DateTime.Now;

    /// <summary>这一张表上生效的软删规则（按注册序取第一条匹配得上的，与过滤器的"注册序折叠"同一条纪律）。
    /// 匹配规则只有一句：键 IsAssignableFrom 实体类型 ⇒ 按接口注册一次，所有实现类都带上。</summary>
    internal Rule? RuleFor(Type entity)
    {
        var items = _items;

        if (items.Length == 0)
            return null;

        foreach (var item in items)
            if (item.Key.IsAssignableFrom(entity))
                return item;

        return null;
    }

    /// <summary>取一个成员读法里的成员名。只认「参数.成员」那一种形状：这一档要回答的是"哪一列"，
    /// 不是"什么条件"，所以 t => t.A.B、t => !t.Deleted、方法调用都在注册期拒，而不是翻成一个看不懂的列名。
    /// 剥掉 Convert 那一层是为了让 DateTime 列配得上 at 那颗 Func&lt;T,DateTime?&gt; 形参（与 UpdateEngine.ColumnOf
    /// 同一条剥法）。</summary>
    static string MemberOf(Expression body, string which)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } widen)
            body = widen.Operand;

        if (body is not MemberExpression { Expression: ParameterExpression } member)
            throw new QuickSqlException(
                $"SoftDelete 的 {which} 那一格要的是一个成员的读法（形如 t => t.列名），当前写的是 {body}。" +
                "这一档不判条件、不算表达式，它只回答「置位哪一列、时间记在哪一列」这两件事。");

        return member.Member.Name;
    }

    /// <summary>一条注册。内部类型：与 FilterRegistry.Registration 同一个理由——公开它等于公开
    /// 「行为可以按运行期 Type 塞进来」那条路，而那条口要的是另一处决定。</summary>
    internal sealed record Rule(Type Key, string DeletedMember, string? AtMember, Func<DateTime> Clock)
    {
        /// <summary>把两格成员名落成那一张契约上的两列，并且把尺子量一遍。注册期（具体类键）与每次构建
        /// 都走这一颗：前者是启动期响，后者是「接口键只有贴上某张表才知道」的那一半。交出去的是
        /// ColumnContract 而不是名字，因为发射段要的正是契约里那一份（列名、可空性、绑定提示）。</summary>
        internal (ColumnContract Deleted, ColumnContract? At) Columns(EntityContract contract)
        {
            var deleted = Column(contract, DeletedMember, "置位");

            if (deleted.ClrType != typeof(bool))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的 {deleted.MemberName} 是 {deleted.ClrType.Name}，而软删往那一格里写的是 true：那一格必须是 bool 列。" +
                    "0/1 整数标记与 \"Y\"/\"N\" 字符标记不是这一档（值恒为 true，没有第二档可给），" +
                    "要么把列改成 bool，要么自己走 orm.Update<T>().Set(o => o.标记, 值).Where(...)，或者订一颗 CudBuilding 做文本改写。");

            if (AtMember is null)
                return (deleted, null);

            var at = Column(contract, AtMember, "时间");

            if (ReferenceEquals(at, deleted))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的 deleted 与 at 指的是同一列（{DeletedMember}）：一句 UPDATE 的 SET 里同一列出现两次，库里本来就执行不过。");

            if (Nullable.GetUnderlyingType(at.ClrType) != typeof(DateTime) && at.ClrType != typeof(DateTime))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的 {at.MemberName} 是 {at.ClrType.Name}，而那一格要写的是一个时间：at 只认 DateTime 或 DateTime? 列。" +
                    "要记字符串或数字时间戳就别给 at 这颗参数（只置位），或者自己走 orm.Update<T>().Set(...) 那一档。");

            return (deleted, at);
        }

        ColumnContract Column(EntityContract contract, string member, string which)
        {
            if (!contract.ByMember.TryGetValue(member, out var column))
                throw new QuickSqlException(
                    $"{contract.Table.Name} 没有名为 {member} 的列（{Key.Name} 上注册的软删把「{which}」那一格点的是它）：" +
                    "软删要写的两列都必须是映射进库的列，导航属性与 [NotMapped] 成员不算。" +
                    (Key.IsInterface || Key.IsAbstract
                        ? " 键是接口/基类，注册期查不出来（那一档没有列契约），只有贴上某张表时才知道 —— 这张就是查出来的那一张。"
                        : string.Empty));

            if (column.IsIdentity)
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的自增列 {column.MemberName} 当不了「{which}」列：那一列不接受写（与 UPDATE 链的 Set 同一把尺子）。");

            if (column.IsConcurrencyToken)
                throw new QuickSqlException(
                    $"{contract.Table.Name} 的并发列 {column.MemberName} 只比较、不书写（§8.2）：把它当「{which}」列，产出的那句 UPDATE 在库里本来就执行不过。");

            return column;
        }

        /// <summary>那颗时钟。同一批只取一次由调用方管（三处删除出口各在批开始之前取这一颗）：
        /// 一次 DeleteRange 切成两句、两句各读一次时钟，会把同一批行记成两个删除时间。</summary>
        internal DateTime Stamp() => Clock();

        /// <summary>置了哪几列的名字。两份读者共用这一颗：DeletePlan.Reason 那句话要的是一个连起来的字符串
        /// （"改了哪几格"），SaveChangesOp.Columns 要的是清单本身（"这条语句自己点名了哪几列"）。
        /// 与 Rewrite 各调一次 Columns 而不是缓存结果：一次删除语句只构建一次，两格查表在这里不是热位，
        /// 而缓存就要按（规则, 契约）记一份状态——为省两次字典查找去养一张表不划算。</summary>
        internal IReadOnlyList<string> SetColumns(EntityContract contract)
        {
            var (deleted, at) = Columns(contract);

            return at is null ? [deleted.ColumnName] : [deleted.ColumnName, at.ColumnName];
        }

        internal string SetSummary(EntityContract contract)
            => string.Join("、", SetColumns(contract));

        /// <summary>那条 DELETE 的改写产物。输入是已经叠完全局过滤器的 WHERE：软删不改条件，
        /// 只把「删掉这些行」换成「把这些行的那一格置位」，所以过滤器的命中范围在两版语句里是同一个。
        /// deletedAt 是批级那颗时间，null 表示就地读时钟（单句出口用这一支，批量与 SaveChanges 由调用方
        /// 把同一颗传进来）。两格值都走 ParameterValue 而不是内联字面量：它们与调用方 Set 写进去的东西同级，
        /// 同样要过 ParameterBinding 那颗钩子，也只有参数化才让五族的字面量形状不需要在这里各写一份。</summary>
        internal UpdateStatement Rewrite(
            EntityContract contract,
            TableRef table,
            SqlExpression where,
            DateTime? deletedAt = null,
            object? entity = null,
            Action<SoftDeleteValuesEvent>? extraValues = null)
        {
            var (deleted, at) = Columns(contract);

            List<SetClause> sets = [new SetClause(deleted, new ParameterValue(true, deleted))];

            if (at is not null)
                sets.Add(new SetClause(at, new ParameterValue(deletedAt ?? Clock(), at)));

            // 订阅者往上加的 SET 项（"谁删的"那一格是调用方的领域事实，库里替不了）。
            // 逐行触发：链式 Delete<T>().Where(...) 那一档没有行可给，那一档不触发（entity 为 null）。
            if (entity is not null && extraValues is { } hook)
            {
                var @event = new SoftDeleteValuesEvent(entity, contract.Table.Name);

                hook(@event);

                foreach (var (column, value) in @event.ExtraValues)
                {
                    // 与那两格同一把尺子：自增列不接受写，并发列只比较不书写。
                    if (column.IsIdentity)
                        throw new QuickSqlException(
                            $"{contract.Table.Name} 的自增列 {column.MemberName} 当不了软删的额外列：那一列不接受写（与 UPDATE 链的 Set 同一把尺子）。");

                    if (column.IsConcurrencyToken)
                        throw new QuickSqlException(
                            $"{contract.Table.Name} 的并发列 {column.MemberName} 只比较、不书写（§8.2）：把它当软删的额外列，产出的那句 UPDATE 在库里本来就执行不过。");

                    sets.Add(new SetClause(column, new ParameterValue(value, column)));
                }
            }

            return new UpdateStatement
            {
                Table = table,
                Sets = sets,
                Where = where,
            };
        }
    }
}
