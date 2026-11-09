using System.Data.Common;
using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Write;

namespace QuickSql.Tracking;

/// <summary>一个实体类型的跟踪器（会话状态机的落地）。按类型切一份：脏判定要按列下标读写实体，
/// 写入要 InsertEngine 与 IEntityTypeSchema，这些都是泛型的——把泛型参数留在类型上而不是方法上，
/// 调度层就只需要一张 Type → ITypeTracker 的表，全程不需要 MakeGenericMethod；T 只由泛型调用点带进来。
/// 这一份里没有事务也没有连接：三档写入由 DbContext 在一条事务里依次驱动，WritePhaseAsync 与 PostFlush
/// 分成两步就是为此——任何一档抛了都不能结算，否则会得到「回滚了，但会话以为写过了」。</summary>
sealed class EntityTracker<T> : ITypeTracker where T : class
{
    readonly QuickSqlOrm _orm;
    readonly EntityContract _contract;

    /// <summary>这一类实体所属的集群，从契约的表位算一次。存在而不是每句现算，是因为删除与并发回读那两条路
    /// 是逐行建 tag 的，而这一格对整个类型是常量。写侧的结论永远是主库，只带集群名；角色由 RentAsync 判。</summary>
    readonly string _cluster;

    readonly IEntityTypeSchema<T> _schema;
    readonly ColumnContract[] _columns;

    /// <summary>主键列的下标，顺序即  的列序（复合主键按契约声明序）。</summary>
    readonly int[] _keyOrdinals;

    /// <summary>这一列能不能进 SET。排除主键（改它的语义是「删一行 + 加一行」）、自增列（库里不接受写，
    /// 那是 IDENTITY_INSERT 那条路）与并发列（只比较不书写，值由库给）。
    /// 三者都是「响了」而不是「剔掉」：静默剔掉的后果是「改了主键保存三次没生效」，那查不出来。</summary>
    readonly bool[] _writable;

    /// <summary>并发列的下标（列序）。空数组就是「这个实体没有并发列」，三档写入走普通语句、
    /// NonQueryAsync、0 行只进报告。所有分岔都从这一个数组的长度起，
    /// 没有并发列的实体一条语句都不多、一颗参数都不多。</summary>
    readonly int[] _tokens;

    /// <summary>应用自维护的并发列，分两族：int Version 那一档在 SET 里写 Version = Version + 1、
    /// 写完后直接在实体上 +1；AppSetsValue 那一族（如 ABP 的 ConcurrencyStamp）新值已在实体上，
    /// 脏判定自然把它带进 SET，不自增、不回读。库给值的 rowversion 支持已移除。
    /// 空数组就是「没有应用自维护的并发列」。</summary>
    readonly int[] _appMaintainedTokens;

    /// <summary>应用给值的并发列（AppSetsValue）。已包含在 _appMaintainedTokens 里（同样不回读），
    /// 单列一份是因为它们的 SET 走脏判定（实体上已有新值）而不是 +1 改写——WriteSingularAsync 那一圈要跳过它们。</summary>
    readonly int[] _appSetsTokens;

    readonly TableRef _table;

    /// <summary>合并语句里那一份字面行集的引用。它不是一张表：_table 是库里那一行，
    /// 这一只只是"这一批要写进去的值"，两者靠 ON 上的主键对上。别名固定 t1（一条语句里只有这两个表源参与）；
    /// 单行桶不引用它，那些语句里连 t1 都不出现。</summary>
    readonly TableRef _rowSet;

    readonly int _identifierChars;

    /// <summary>会话那一侧的调度器。回来路只为一件事：临时主键的账记在主端那一档，拿着临时值的是依赖方那一档，
    /// 两边要能对上话。走 ChangeTracker 而不是让各档互相持有引用，是因为依赖是按 Type 说的。</summary>
    readonly ChangeTracker _session;

    /// <summary>本型每根关系边的外键列下标（契约关系换算到列序）。没有关系边时是 null，
    /// 那条改写路径在没有关系的类型上一次都不跑。边的目标是 Type 而不是档：拿着目标类型去问会话，
    /// 才不需要在这里出现 T。</summary>
    readonly (Type Target, int[] Ordinals)[]? _fkEdges;

    readonly Dictionary<IdentityKey, Node> _identity = new();
    readonly Dictionary<object, Node> _byRef = new(ReferenceEqualityComparer.Instance);
    readonly List<Node> _order = [];

    /// <summary>临时主键 → 那一只节点。键是规范化成 long 的临时值而不是原始装箱值：依赖方的外键列与主端的
    /// 主键列可以一个是 int 一个是 long（合法 DDL），而装箱相等判的是"同型同值"——差一型就查不到，
    /// 那颗负数就会被静默写进库。懒建：没发过临时键的类型连一个字典都不占。</summary>
    Dictionary<long, Node>? _temps;

    /// <summary>临时键的发号器，只朝负数走。不随保存归零：换过一轮号就重放一遍，
    /// "这个负数其实是某只实例自己填的键"这种碰撞正好是重放能造出来的。</summary>
    long _tempCursor;

    readonly bool _hasIdentity;

    /// <summary>这一型有没有集合边（级联的闸门）。为假时 Add/Remove 一句都不多付：
    /// 绝大多数实体没有导航，而级联的遍历要为每一次调用新建一份游标。</summary>
    readonly bool _cascades;

    bool _mayHaveWrites;

    public EntityTracker(QuickSqlOrm orm, ChangeTracker session, Schema.IEntitySchemaRegistry? schemas = null)
    {
        _orm = orm;
        _session = session;
        // 会话级模型覆写（DbContext.OnModelCreating）那颗口：null = 照旧走组装根的注册表。
        _schema = (schemas ?? orm.Schemas).Schema<T>();
        _contract = _schema.Contract;
        _cluster = DataSourceRouting.ClusterOf(_contract.Table);
        _columns = _contract.Columns;

        var keys = new List<int>();
        var tokens = new List<int>();
        var writable = new bool[_columns.Length];

        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            var column = _columns[ordinal];

            if (column.IsPrimary)
                keys.Add(ordinal);

            if (column.IsConcurrencyToken)
                tokens.Add(ordinal);

            _hasIdentity |= column.IsIdentity;

            // 应用给值那一族（AppSetsValue）要进 SET（新值在实体上、由应用生成），其余并发列只比较不书写
            writable[ordinal] = !column.IsPrimary
                && !column.IsIdentity
                && (!column.IsConcurrencyToken || column.AppSetsValue);
        }

        _keyOrdinals = [.. keys];
        _cascades = _contract.Collections.Count > 0;
        _writable = writable;
        _tokens = [.. tokens];

        // 并发列分两族：应用给值的（AppSetsValue）新值已在实体上，脏判定自然带进 SET；
        // 其余应用自维护的（int Version 一派）在 SET 里写 Version = Version + 1，写完后实体直接 +1。
        // 两族都不回读；库给值的 rowversion 支持已移除，非整型又没标 AppSetsValue 的令牌在启动期正面拒。
        var appMaintained = new List<int>();
        var appSets = new List<int>();

        foreach (var ordinal in _tokens)
        {
            var column = _columns[ordinal];

            if (column.AppSetsValue)
            {
                appSets.Add(ordinal);
                appMaintained.Add(ordinal);
            }
            else if (IsIntegerClrType(column.ClrType))
                appMaintained.Add(ordinal);
            else
                throw new QuickSqlException(
                    $"{_contract.Table.Name} 的并发列 {column.MemberName}（{column.ClrType.Name}）没有可用的令牌维护方式：" +
                    "库给值的 rowversion 支持已移除；非整型并发列请用 [ConcurrencyToken(true)] 由应用给新值，" +
                    "整型并发列用 [ConcurrencyToken] int / long / short / byte（ORM 在 UPDATE 里 +1）。");
        }

        _appMaintainedTokens = [.. appMaintained];
        _appSetsTokens = [.. appSets];
        // 目标怎么引用自己由方言定（WriteTargetAlias）：T-SQL 与 PG 系给一个独立别名 t0，
        // Oracle 血统不接受目标别名、只能用表名本身。要问方言而不是现场写死：同一份 _table
        // 既进 UPDATE 的 SET/WHERE，也进冲突重读那一条 SELECT——两处必须同名。
        _table = new TableRef(0, _orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
        _rowSet = new TableRef(1, QuickSqlOrm.AliasAt(1), _contract.Table);

        // 边的外键成员名换算成列下标，只在启动期这一次：改写路径每行每列都要用，而契约只按名字说话。
        // ByMember 认不出这些名字是内核不变量破了（关系解析判过：约定命中的必须是已映射列），
        // 所以这里不判、让它按自己的形状响。
        if (_contract.Relationships.Count > 0)
        {
            var edges = new (Type, int[])[_contract.Relationships.Count];

            for (var i = 0; i < edges.Length; i++)
            {
                var relationship = _contract.Relationships[i];
                var ordinals = new int[relationship.MemberNames.Count];

                for (var column = 0; column < ordinals.Length; column++)
                    ordinals[column] = Array.IndexOf(_columns, _contract.ByMember[relationship.MemberNames[column]]);

                edges[i] = (relationship.TargetType, ordinals);
            }

            _fkEdges = edges;
        }

        // 预容量：一条逐行 UPDATE 的标识符就是表名 + 别名 + 主键列名那几处；
        // 并发列在 WHERE 与 OUTPUT 里各出现一次，按两列的量一起算进去。
        _identifierChars = _contract.Table.Name.Length + _table.Alias.Length * 3 + (_columns.Length + _tokens.Length) * 16 + 128;
    }

    public Type EntityType => typeof(T);

    public EntityContract Contract => _contract;

    public int Count => _order.Count;

    /// <summary>交的是 _order 本身，不是拷贝：这条链上的每一处遍历（DetectChanges、会话那侧的 AnyDue
    /// 与两道发射前闸门）都是按会话规模跑的，拷一份 1 000 元素的引用数组就是 8 KB 一句都不发的工作。
    /// 枚举期间不改这张表——本类改状态与脏位、不改顺序，会摘掉节点的 PostFlush 与 Entries() 各自留着拷贝。</summary>
    public IReadOnlyList<Node> TrackedNodes => _order;

    /// <summary>只在「变脏 / 新增 / 删除」时置真，之后不再置假（Clear 与 PostFlush 例外）。
    /// 于是它可以偶然多 true 一次：SaveChanges 那一档扫到零个节点，返回空报告——
    /// 反方向的错（该写而没写）才是不能接受的，这个位只朝安全的方向偏。</summary>
    public bool MayHaveWrites => _mayHaveWrites;

    /// <summary>它看的是状态，不是语句：本档里既有 Added 又有 Deleted 就是「这一轮同一张表既占位又腾位」，
    /// 而库里那条业务唯一索引（行号、位置、编码那一类）只认得到此刻表里有什么——先插就撞上还赖在表里的那一行。
    /// 一条 Added 之后又被 Remove 掉的行在 ApplyState 那一格已经转成 Detach 了，这里不会为它多报一次。</summary>
    public bool HasInsertsAndDeletes
    {
        get
        {
            if (!_mayHaveWrites)
                return false;

            var added = false;
            var deleted = false;

            foreach (var node in _order)
            {
                if (node.State == EntityState.Added)
                    added = true;
                else if (node.State == EntityState.Deleted)
                    deleted = true;

                if (added && deleted)
                    return true;
            }

            return false;
        }
    }

    public bool HasTempKeys => _temps is { Count: > 0 };

    /// <summary>依赖方手里那颗值问这一档：它是不是本档发的临时键、换得出的真实键是哪一颗。
    /// 判"换不出"靠的是节点手里的键此刻还是不是那个临时值，而不是查某张"已回填"的表：
    /// 回填由 InsertEngine 直接写进实体，实体本身就是那一张表。节点已经不在引用表里就是孤的
    /// ——标删一只还没写出去的新增行等于忘掉它，而引用还留在依赖方手里。</summary>
    public TempProbe ProbeTemp(object? value, out object? real)
    {
        real = null;

        // 临时键恒为负整数，这一判就够：非整型（Guid 键、字符串键）与没填（null）都不是本档发出去的东西。
        var candidate = Signed(value);

        if (_temps is null || candidate is null)
            return TempProbe.NotTemp;

        if (!_temps.TryGetValue(candidate.Value, out var node))
            return TempProbe.NotTemp;

        if (!_byRef.TryGetValue(node.Entity, out var held) || !ReferenceEquals(held, node))
            return TempProbe.Orphaned;

        // 与查号同一个尺子：主端那一列是 bigint、依赖方那一列是 int 时，装出来的箱是两个型，
        // 而"还是不是当初那颗"判的是数值——按装箱相等判会把"还没换"读成"换出来了"，然后原样写回去。
        var current = KeyValues((T) node.Entity)[0];

        if (current is null || Unset(current) || Signed(current) == candidate)
            return TempProbe.Pending;

        real = current;

        return TempProbe.Resolved;
    }

    /// <summary>带符号整型的规范化读数（_temps 那把 long 尺子的出处），装不下就返回 null。
    /// short/byte 不在这里：临时键只会由 int/long 主键发出去，一根 short 外键要捏住它得先自己写强制转换
    /// ——那种列在探测这里就被当成"不是我发的号"放过去（已知边界）。</summary>
    static long? Signed(object? value) => value switch
    {
        int small => small,
        long wide => wide,
        _ => null,
    };

    // ── 入口：物化 / Add / Attach / Update / Remove ────────────────────

    /// <summary>物化路径（身份映射）。返回会话里该拿的那一只实例：同引用已跟踪 → 原样；
    /// 同键已跟踪 → 那一只，新副本丢弃；都不认识 → 登记为 Unmodified。不覆盖已有实例的值：
    /// 那只可能已经被改过而还没保存，用库里的值盖回去等于擦掉用户的改动（"重读并让库赢"是 Refresh 的活）。</summary>
    public Node Track(object entity)
    {
        RequireKey();

        if (_byRef.TryGetValue(entity, out var tracked))
            return tracked;

        var typed = (T) entity;
        var values = KeyValues(typed);

        if (IsDefinite(values))
        {
            var key = new IdentityKey(values);

            if (_identity.TryGetValue(key, out var canonical))
                return canonical;

            return Register(entity, EntityState.Unmodified, key, typed);
        }

        // 主键还没值：这一行不进身份映射，否则两只都没填键的新实例会被认成同一只。
        // 「同一个键被新增两次」那一档由临时主键接住了（Add 会发号）；物化路径不动：
        // 库里递出来的行本来就该有键，没键是库表设计的问题，不替它编一个。
        return Register(entity, EntityState.Unmodified, null, typed);
    }

    /// <summary>标为新增。与 Track 的分别：这里「同键已有另一只实例」要抛而不是丢弃——
    /// 物化时库把同一行递两次，去重是对的；用户把同一个键递两次，那是他手里有两只对象，静默选一只就是无痕。</summary>
    public Node Add(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RequireKey();

        var typed = (T) entity;

        if (_byRef.TryGetValue(entity, out var known))
        {
            if (known.State == EntityState.Deleted)
                throw new QuickSqlException($"{_contract.Table.Name} 的这个实例已经被标成删除，不能再标成新增：同一个实例上的两种意图没有一种顺序能同时执行。要从头插就新建一个实例。");

            return known;
        }

        var values = KeyValues(typed);
        IdentityKey? key = null;

        if (IsDefinite(values))
        {
            var candidate = new IdentityKey(values);

            if (_identity.TryGetValue(candidate, out var clash))
                throw new QuickSqlException(Collision(clash, values));

            key = candidate;
        }

        var node = Register(entity, EntityState.Added, key, typed);

        if (key is null)
            AssignTempKey(node, typed, values);

        // 级联插入：顺这一张实体的集合边把子实体一起带进会话。放在新登记之后、且在
        // "已跟踪就直接还回原节点"的早退之外——递归靠的正是「只有新登记才往下走」。
        if (_cascades)
            _session.Cascade(this, entity, CascadeMode.Insert);

        return node;
    }

    /// <summary>给一只「键还没值」的新增实例发一个临时主键——"子实体先 Add、父实体的键等库给"那一种引用
    /// 终于有个东西可以捏着，而不是写出去一个空外键。只服务一种形状：单列、带符号整型、自增的主键
    /// （自增意味着值本来就要等 INSERT 给回来，负数意味着手里这颗不可能是库里的键）；客户端生成的键
    /// （Guid 那一派）不代填——空着是填错了而不是"还没生成"。临时值进实体、不进身份映射：
    /// Find(那个负数) 查不出东西，快照保持"没填"。写出去的语句永远不带它：自增列不进列清单，
    /// 依赖方手里那一颗在语句构建之前被换掉——换不掉就拒，任何情况下都不让临时值进库。</summary>
    void AssignTempKey(Node node, T entity, object?[] values)
    {
        if (!_hasIdentity || _keyOrdinals.Length != 1)
            return;

        var type = Nullable.GetUnderlyingType(_columns[_keyOrdinals[0]].ClrType) ?? _columns[_keyOrdinals[0]].ClrType;

        if (type != typeof(int) && type != typeof(long))
            return;

        // 再认一次手里那颗值：0 / Guid.Empty 才是"没填"，别的都是调用方自己选的键，不抢他的号。
        if (values[0] is not int and not long || !Unset(values[0]!))
            return;

        var temps = _temps ??= [];

        long candidate;

        do
            candidate = --_tempCursor;
        while (temps.ContainsKey(candidate) || _identity.ContainsKey(new IdentityKey([Box(candidate, type)])));

        temps[candidate] = node;
        _schema.SetValue(entity, _keyOrdinals[0], Box(candidate, type));
    }

    static object Box(long value, Type type) => type == typeof(long) ? value : (int) value;

    /// <summary>纳入会话并承认为「库里已有、当前值即库值」。主键必须有值：没有值就没有 WHERE，
    /// 之后任何一次改动都会变成一句定位不到的 UPDATE。</summary>
    public Node Attach(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RequireKey();

        var typed = (T) entity;

        if (_byRef.TryGetValue(entity, out var known))
            return known;

        var values = RequireDefinite(typed, "Attach");
        var key = new IdentityKey(values);

        if (_identity.TryGetValue(key, out var clash))
            throw new QuickSqlException(Collision(clash, values) + " Attach 不会替换会话里的那一只：要丢弃它先 Entry(那只).State = Detached。");

        return Register(entity, EntityState.Unmodified, key, typed);
    }

    /// <summary>承认为「库里已有」并且每一列都算脏——Attach + Entry(...).State = Modified 的合成形态，
    /// 「我知道它变了，别比了，全写」。</summary>
    public Node Update(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var node = Attach(entity);

        if (node.State == EntityState.Added)
            throw new QuickSqlException($"{_contract.Table.Name} 的这个实例已经在会话里被标为新增：Update 会把意图改成按主键 UPDATE，而那一行可能根本还没进库。" +
                "要么让它保持新增，要么先 Entry(...).State = Detached 再重来。");

        ApplyState(node, EntityState.Modified);

        return node;
    }

    /// <summary>标为删除。没跟踪过的实例也纳入再标（与同类库同形），同样要求主键有值。
    /// 唯一的例外是「本来就还是新增」：库里没有这一行，删它的正确执行方式是忘掉它，不是发一句 DELETE。</summary>
    public Node Remove(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RequireKey();

        var typed = (T) entity;

        if (_byRef.TryGetValue(entity, out var node))
        {
            if (node.State == EntityState.Added)
            {
                // 父本轮还没写出去，删它的正确执行方式是忘掉它。这里不替它连带忘掉子实体：
                // 那要把"哪些是这一串带进来的"记成一份账，一旦记错就是静默漏删；
                // 子还留在会话里 = 它的键落不下去，SaveChanges 会在级联落键那一格响亮地拒。
                Detach(node);

                return node;
            }

            node.State = EntityState.Deleted;
            _mayHaveWrites = true;

            if (_cascades)
                _session.Cascade(this, entity, CascadeMode.Delete);

            return node;
        }

        var values = RequireDefinite(typed, "Remove");
        var key = new IdentityKey(values);

        if (_identity.TryGetValue(key, out var clash))
            throw new QuickSqlException(Collision(clash, values) + " 要删的是会话里那一只：对它调用 Remove。");

        var registered = Register(entity, EntityState.Deleted, key, typed);

        if (_cascades)
            _session.Cascade(this, entity, CascadeMode.Delete);

        return registered;
    }

    /// <summary>这只引用是不是在本档里。 用它把 Entry 的拒绝说准。</summary>
    public bool Owns(object entity) => _byRef.ContainsKey(entity);

    /// <summary>这一行在本档里已经是哪只实例（Canonical）。没有主键、键还没值、或这一行没进过身份映射
    /// 都返回 null：本句只是问，不登记，也不响——键没值的实例按规则不在映射里。</summary>
    public object? Canonical(object entity)
    {
        if (_keyOrdinals.Length == 0)
            return null;

        var values = KeyValues((T) entity);

        return IsDefinite(values) && _identity.TryGetValue(new IdentityKey(values), out var node) ? node.Entity : null;
    }

    public EntityState? StateOf(object entity)
        => _byRef.TryGetValue(entity, out var node) ? node.State : null;

    public object?[] KeyValuesOf(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return KeyValues((T) entity);
    }

    /// <summary>级联落键：把父那一串的键值写进本档这几列外键。先换型再写——schema 的 setter 是强转的装箱写入，
    /// 主端 bigint 配子端 int 是合法 DDL，不换就是现场一次 InvalidCastException（与 AdaptKey 同一条理由）。
    /// 装不下要响亮地拒：静默截断出来的是一颗库里不属于任何父行的键。</summary>
    public void SetForeignKeys(object entity, int[] ordinals, object?[] values)
    {
        var typed = (T) entity;

        for (var i = 0; i < ordinals.Length; i++)
            _schema.SetValue(typed, ordinals[i], AdaptKeyValue(values[i], _columns[ordinals[i]]));
    }

    object? AdaptKeyValue(object? value, ColumnContract column)
    {
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (value is null || type == value.GetType())
            return value;

        // 整型之间按 long 过一遍：这是唯一到得了这一格的键型（发号那一档只发 int/long），
        // 子端那一列可以是 int/long/short/byte 里的任何一种。
        if (value is int or long && IsIntegerClrType(type))
        {
            var wide = Convert.ToInt64(value);

            try
            {
                return Convert.ChangeType(wide, type);
            }
            catch (OverflowException)
            {
                throw new QuickSqlException($"{_contract.Table.Name} 的 {column.MemberName}（列 {column.ColumnName}）是 {type.Name}，" +
                    $"装不下主端给回来的键 {wide}：外键列比主键列窄，这一版不替它截断 —— 把这两列做成同型。");
            }
        }

        throw new QuickSqlException($"{_contract.Table.Name} 的 {column.MemberName}（列 {column.ColumnName}）是 {type.Name}，" +
            $"而主端那一列是 {value.GetType().Name}：级联要把父的键落成子的外键，两列不同型又没有可说的换型规则 —— 把这两列做成同型。");
    }

    /// <summary>取入口。不代替 Attach：「查一个入口顺手把实体纳入会话」是一种没人说过的写入意图
    /// （先 Attach 再改 State）。返回类型是泛型的那一档：收 lambda 的 Property 重载长在 EntityEntry〈T〉 上，
    /// 退回非泛型就只剩收字符串的那一条，名字笔误要等到运行期才现形。</summary>
    public EntityEntry<T> Entry(object entity)
    {
        // 与 Add/Attach/Remove 同一款守卫：null 落到下面的型别判断会以一句 NRE 收场，
        // "传了 null"是调用方的错，要按调用方的错报。
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is not T typed)
            throw new QuickSqlException($"{_contract.Table.Name} 的跟踪器收到的不是 {typeof(T).Name} 而是 {entity.GetType().Name}：派生实例要按<em>基类</em>取档（Set<{typeof(T).Name}>().Entry(...)），" +
                "各占一份契约是继承映射的事，P2/P3 才接线（§8.2 的按类型切开）。");

        if (!_byRef.TryGetValue(typed, out var node))
            throw new QuickSqlException($"{typeof(T).Name} #{Text(KeyValues(typed))} 不在会话里：先 Set<{typeof(T).Name}>().Attach(...) 或 Add(...)，再取 Entry（§8.3）。");

        return new TrackedEntry<T>(this, node);
    }

    // ── 状态与侦测 ────────────────────────────────────────────────────

    /// <summary>EntityEntry.State 的落地。四个赋值各有各的连带效果，集中写在这里而不是散到各个入口：
    /// DetectChanges 与显式赋值必须走同一套规则——否则「显式 Modified 之后再侦测」会掉回 Unmodified，
    /// 那就是把用户明说的「全写」撤销了。</summary>
    public void ApplyState(Node node, EntityState next)
    {
        if (next == node.State)
            return;

        switch (next)
        {
            case EntityState.Detached:
                Detach(node);

                return;

            case EntityState.Unmodified:
                // 「承认当前值就是库里的值」：重拍快照 + 清覆盖 + 清脏。
                // 不重拍的话，下一次侦测会拿旧快照把这一行立刻判回脏——那句赋值就只是「再说一遍」。
                node.State = next;
                Reset(node, (T) node.Entity);

                return;

            case EntityState.Modified:
                RequireKey();
                RequireDefinite((T) node.Entity, "标为 Modified");
                node.State = next;
                MarkAllWritable(node);
                _mayHaveWrites = true;

                return;

            case EntityState.Deleted:
                RequireKey();

                if (node.State == EntityState.Added)
                    Detach(node);
                else
                    node.State = next;

                _mayHaveWrites = true;

                return;

            case EntityState.Added:
                if (node.Registered is not null)
                    throw new QuickSqlException($"{_contract.Table.Name} 的主键 {Text(KeyValues((T) node.Entity))} 在会话里已经被当成库里已有的一行：标成新增就是要再插一行同键的，库里必然拒绝。" +
                        "要插新行就给它一个新键（自增列的话新建一只实例，把键留在 0）。");

                node.State = next;
                _mayHaveWrites = true;

                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(next), next, "未知 EntityState。");
        }
    }

    /// <summary>逐列比较 + 覆盖合成（脏列合成里「发现」那一半）。
    /// 返回本次改了状态的实例数，0 就是「这一轮什么都没变」——测试与日志要的就是这个数。</summary>
    public int DetectChanges()
    {
        var changed = 0;

        foreach (var node in _order)
        {
            // 新增与删除不由比较决定：新增的行库里还没有值可比，删除的行整行都不进 SET。
            if (node.State is EntityState.Added or EntityState.Deleted)
                continue;

            var entity = (T) node.Entity;
            var any = false;

            for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
            {
                // 覆盖位先问：显式标脏与显式标不脏的列，那次 GetValue（装箱）+ Equals 的结果根本没人消费
                // ——求值序调换后，这一列从"读+比"降为一次数组读，显式 Modified 的实体在每轮侦测里为每列省一笔。
                var dirty = node.Overrides[ordinal] ?? !Equals(_schema.GetValue(entity, ordinal), node.Snapshot[ordinal]);

                node.Dirty[ordinal] = dirty;
                any |= dirty;
            }

            // 「改了又改回来」在这里回到 Unmodified，所以「曾经碰过」不会变成一次多余的 UPDATE。
            var next = any ? EntityState.Modified : EntityState.Unmodified;

            if (next == node.State)
                continue;

            node.State = next;
            changed++;

            if (any)
                _mayHaveWrites = true;
        }

        return changed;
    }

    /// <summary>清数据。顺带把每个节点的状态归到 Detached：DbChangeTracker.Clear 对调用方的承诺就是
    /// "所有实例转 Detached"，而手里那只 EntityEntry 是视图不是拷贝——不改它的话这句承诺只写在文档里。</summary>
    public void Clear()
    {
        foreach (var node in _order)
            node.State = EntityState.Detached;

        _identity.Clear();
        _byRef.Clear();
        _order.Clear();
        _temps?.Clear();
        _mayHaveWrites = false;
    }

    /// <summary>内存视图的快照（Local.Entries）。含已标删的实例——它们确实还在会话里，
    /// 直到那一句 DELETE 执行完。</summary>
    public IReadOnlyList<T> SnapshotEntities() => [.. _order.Select(node => (T) node.Entity)];

    /// <summary>按主键在内存里找（Local.Find）。只看得到进过身份映射的那些：
    /// 键还没值的实例按规则不在映射里，所以这里查不到，而它确实在会话里。</summary>
    public T? Find(object?[] values)
    {
        if (values.Length != _keyOrdinals.Length)
            throw new QuickSqlException($"{_contract.Table.Name} 的主键有 {_keyOrdinals.Length} 列，这里给了 {values.Length} 个值。");

        return _identity.TryGetValue(new IdentityKey(values), out var node) ? (T) node.Entity : null;
    }

    internal PropertyEntry Property(Node node, string memberName)
    {
        if (!_contract.ByMember.TryGetValue(memberName, out var column))
            throw new QuickSqlException($"{_contract.Table.Name} 没有名为 {memberName} 的列：导航属性与未映射的计算成员没有脏标记可言（§10.2 的映射层不认它）。");

        return new TrackedProperty<T>(this, node, Array.IndexOf(_columns, column), column);
    }

    /// <summary>给 TrackedProperty 用的三扇小窗：读写实体上的一格，以及"有人强制标脏了"这个通知。
    /// 走方法而不是把 _schema 暴露出去，是为了让快照那三张数组只有一个写入点（本类）。</summary>
    internal object? Value(T entity, int ordinal) => _schema.GetValue(entity, ordinal);

    internal void Write(T entity, int ordinal, object? value) => _schema.SetValue(entity, ordinal, value);

    internal void MarkWrites() => _mayHaveWrites = true;

    /// <summary>用库里的最新值覆盖实体并重置快照（Refresh 的落地）。不在会话里的实例先登记为 Unmodified；
    /// 已在的按状态分别处理——Added 与 Modified 不动（用户有未保存的改动，库值不该覆盖），
    /// 其余状态重拍快照、清脏、清覆盖。</summary>
    internal void ApplyFreshValues(object entity, object?[] freshValues)
    {
        RequireKey();

        var typed = (T) entity;

        for (var i = 0; i < _columns.Length; i++)
            _schema.SetValue(typed, i, freshValues[i]);

        if (_byRef.TryGetValue(entity, out var node))
        {
            if (node.State is not (EntityState.Added or EntityState.Modified))
                ApplyState(node, EntityState.Unmodified);
        }
        else
        {
            var values = KeyValues(typed);
            var key = IsDefinite(values) ? new IdentityKey(values) : (IdentityKey?) null;

            Register(entity, EntityState.Unmodified, key, typed);
        }
    }

    /// <summary>这一行此刻会进 SET 的列（脏 且 可写），按列序、给成员名。与 Node.Dirty 的分别是后者
    /// 还包含主键那几列——主键进不了语句，「这一列被判定改过了」与「这一列会被写出去」是两句话。</summary>
    internal IReadOnlyList<string> ModifiedMembers(Node node)
    {
        List<string>? names = null;

        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            if (!node.Dirty[ordinal] || !_writable[ordinal])
                continue;

            (names ??= []).Add(_columns[ordinal].MemberName);
        }

        return names is null ? [] : names;
    }

    /// <summary>这一行此刻会不会被写出去（IsModified 与 HasChanges 共用的判据）。Unmodified 也要算：
    /// Property(...).IsModified = true 写下去的是覆盖位，状态要等下一次 DetectChanges 才被推成 Modified，
    /// 只看 Modified 的话关掉自动侦测的调用方在 if (HasChanges) SaveChanges() 上会静默跳过强制写入；
    /// Modified 而 SET 为空（每一列都标成不脏）算"不会"。判据是"有没有任何一列脏"，不是"脏且进得了 SET"：
    /// 用后者会把 Unwritable 那一档拒写变成走不到的死路——SaveChanges 先看这一句决定要不要开事务，
    /// 只脏了一列主键的节点在"能不能进 SET"那把尺子下是干净的，整次保存提前返回空报告，
    /// 「改了主键保存三次没生效」就真的没生效了，而这里是唯一能听见它的地方。</summary>
    public bool WillWrite(Node node) => node.State switch
    {
        EntityState.Added or EntityState.Deleted => true,
        EntityState.Modified or EntityState.Unmodified => AnyDirty(node),
        _ => false,
    };

    static bool AnyDirty(Node node)
    {
        foreach (var dirty in node.Dirty)
            if (dirty)
                return true;

        return false;
    }

    public EntityEntry EntryOf(Node node) => new TrackedEntry<T>(this, node);

    // ── 三档写入 ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SaveChangesOp>> WritePhaseAsync(WriteOperation phase, List<HookTrace> traces, CancellationToken ct)
    {
        // 观察类钩子先发：它在语句建出来之前，订阅者改得动实体，
        // 改出来的值会被紧接着建出来的语句带出去（取值段读的是实体）。
        NotifyWritten(phase);

        return phase switch
        {
            WriteOperation.Insert => await WriteInsertsAsync(traces, ct).ConfigureAwait(false),
            WriteOperation.Update => await WriteUpdatesAsync(traces, ct).ConfigureAwait(false),
            _ => await WriteDeletesAsync(traces, ct).ConfigureAwait(false),
        };
    }

    /// <summary>把这一档要写的实体逐只交给观察者（QuickSqlAop.EntityWritten）。逐只一次：
    /// 一只实体在一轮里只被写一次，事件与「这一轮改了几行」一一对应。零成本：没订阅就不构造事件对象。</summary>
    void NotifyWritten(WriteOperation phase)
    {
        var written = _orm.Aop.EntityWritten;

        if (written is null)
            return;

        var state = phase switch
        {
            WriteOperation.Insert => EntityState.Added,
            WriteOperation.Update => EntityState.Modified,
            _ => EntityState.Deleted,
        };

        var kind = phase switch
        {
            WriteOperation.Insert => "insert",
            WriteOperation.Update => "update",
            _ => "delete",
        };

        foreach (var node in _order)
        {
            if (node.State != state)
                continue;

            written(new EntityWrittenEvent(node.Entity, kind));
        }
    }

    /// <summary>把依赖方手里那些临时外键换成主端此刻的真实键（临时主键回填）。必须跑在语句构建之前：
    /// 跑在后面就是先把错值发出去了。跑得动靠拓扑序——主端档的 Insert 排在依赖方之前 await 完，
    /// 此刻主端的键要么已经回来，要么这一轮没写它（后者只能拒）。改动落在实体上而不只是参数上：
    /// 那只实例之后还要被读、被比对、被下一次保存写，快照不用跟着改（PostFlush 会重拍）。
    /// Update 档也要过一遍：已在库里的行外键被赋成还没换出去的临时键时，那一句 SET 与插入档一样是写库里没有的键。</summary>
    void ResolveTempKeys(EntityState state)
    {
        if (_fkEdges is null)
            return;

        // 会话里没有临时键 = 这条路径一次都不该付钱（绝大多数保存连一个临时键都没发出去过）。
        if (!_session.HasPendingTemps)
            return;

        foreach (var node in _order)
        {
            if (node.State != state)
                continue;

            var entity = (T) node.Entity;

            foreach (var (target, ordinals) in _fkEdges)
                foreach (var ordinal in ordinals)
                {
                    var value = _schema.GetValue(entity, ordinal);

                    // 临时键恒为负整数：Guid / 字符串 / 可空列里的 null 都直接跳过，不必为它们问一遍。
                    if (value is not (int or long))
                        continue;

                    var probe = _session.ProbeTemp(target, value, out var real);

                    if (probe == TempProbe.NotTemp)
                        continue;

                    if (probe != TempProbe.Resolved)
                        throw UnresolvedTempKey(ordinal, target, probe, value);

                    _schema.SetValue(entity, ordinal, AdaptKey(real!, _columns[ordinal]));
                }
        }
    }

    /// <summary>把主端的真实键落成依赖方那一列装得下的型。外键列与主键列可以不同型
    /// （bigint 主键配 int 外键是合法 DDL），而 schema 的 setter 是强转的装箱写入——
    /// 型不对的现场是一次 InvalidCastException 而不是一句话，所以换型在这里做、装不下也在这里说。
    /// 只处理 int 与 long 两型（来路只有发号与探测两档），short 外键不会被当成"我发的号"走到这句。</summary>
    object AdaptKey(object real, ColumnContract column)
    {
        var value = real is int narrow ? (long) narrow : (long) real;
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (type == typeof(long))
            return value;

        if (type != typeof(int) || value is < int.MinValue or > int.MaxValue)
            throw new QuickSqlException($"{_contract.Table.Name} 的 {column.MemberName}（列 {column.ColumnName}）是 {type.Name}，" +
                $"装不下主端给回来的键 {value}（{real.GetType().Name}）：外键列比主键列窄或者不是整型，这一版不替它截断 —— 把这两列做成同型。");

        return (int) value;
    }

    QuickSqlException UnresolvedTempKey(int ordinal, Type target, TempProbe probe, object? value)
    {
        var column = _columns[ordinal];

        var why = probe switch
        {
            TempProbe.Orphaned => "对应的那只实例已经离开会话（标删一只还没写出去的新增行等于忘掉它，而引用还留在这一行手里）",
            _ when target == typeof(T) => "与本行在同一张表的同一批里，而类型序排不出同一类型内部的先后（自引用那一档欠在 §14）",
            _ => "本轮没有写出去，所以那个临时键换不出真实值",
        };

        return new QuickSqlException($"{_contract.Table.Name} 的 {column.MemberName}（列 {column.ColumnName}）此刻是临时主键 {value}，" +
            $"而它指向的 {target.Name} 那一行{why}。临时值不进库：这一句要么写一个库里没有的键，" +
            $"要么静默丢掉外键值，两种都比报错难查。要么先保存 {target.Name} 让库把键给回来，要么把这一行的外键自己填上。");
    }

    /// <summary>新增整批交给 InsertEngine：分块、参数预算、钩子、契约校验、OUTPUT 回填键那条链路已经钉住了，
    /// 在跟踪器里再实现一遍只会多出第二份要各自维护的缺陷。回填是这里唯一额外的要求：插完拿不到主键，
    /// 身份映射就登记不上，同一会话第二次保存会把那一行再插一遍；插完拿不到并发列，手里那一列是空的，
    /// 第一次保存的 WHERE 就对不上。</summary>
    async Task<IReadOnlyList<SaveChangesOp>> WriteInsertsAsync(List<HookTrace> traces, CancellationToken ct)
    {
        ResolveTempKeys(EntityState.Added);

        List<Node>? pending = null;

        foreach (var node in _order)
        {
            if (node.State != EntityState.Added)
                continue;

            (pending ??= []).Add(node);
        }

        if (pending is null)
            return [];

        var engine = new InsertEngine<T>(_orm, [.. pending.Select(node => (T) node.Entity)]);

        // 两档文本要在这里选好：有自增列就必须走回填那一档，
        // 否则实体手里的键是 0——身份映射登记不上，同一会话第二次保存会把那一行再插一遍。
        // 并发令牌全是应用自维护的，不需要回填——实体已经有初始值或新值。
        // 先 Explain 再执行不是多余：Explain 里跑契约校验，必须发生在第一条语句上线之前；
        // 回填档的执行口走同一个已缓存的计划，不会把 AuditValue 过第二遍。
        var backfill = _hasIdentity;
        var plan = backfill ? engine.ExplainGenerated() : engine.Explain();
        var rows = plan.RowCount;

        // 插入档的回放来自引擎那份计划（取值与两条改写钩子都在里面），它不在取值段的 traces 上：
        // 不并进来的话，一次 SaveChanges 的报告会说"没人改写过"，而同一次保存里那句 INSERT 确实是被改过的。
        traces.AddRange(plan.HookReplay);

        if (backfill)
            await engine.ExecuteGeneratedAsync(ct).ConfigureAwait(false);
        else
            await engine.ExecuteAffrowsAsync(ct).ConfigureAwait(false);

        return [Operation(WriteOperation.Insert, plan.Chunks.Count, rows,
            [.. plan.Chunks[0].Ast.Columns.Select(static column => column.ColumnName)], plan.Chunks[0].Sql.Text)];
    }

    /// <summary>改动：先按脏列掩码分桶，再逐桶决定发几条语句——同一种改动有两行以上、且这一型可合，
    /// 就合成一条 UPDATE … INNER JOIN (VALUES …) … ON 键相等 WHERE 变更守卫 的合并语句；否则一个实体一句。
    /// 三档不进合并，各有理由：带并发列的实体（WHERE 有逐行不同的快照值，多行语句的回包与行的对应关系拿不出来）、
    /// 单行桶（合并没有对象，多出来的 FROM (VALUES) 要付两倍的文本复杂度）、守卫覆盖不全的桶
    /// （只要有一列在该库不可比，整个桶退回逐条——把那一列从守卫里省掉的后果是丢写）。
    /// 合并那一档的行数口径与逐条不同：守卫把"库里已经是这个值"的行挡在 UPDATE 之外，Rows 是真变化的行数，
    /// 逐条那一档一直是命中数；合并之后 Rows 少于行数不再能区分「没变」与「已经不在了」，
    /// 要逐行归因就开单行桶。</summary>
    async Task<IReadOnlyList<SaveChangesOp>> WriteUpdatesAsync(List<HookTrace> traces, CancellationToken ct)
    {
        // 先换临时键再分桶：桶里的 Values 与合并语句的那一份字面行集都是从实体读出来的现值，
        // 换晚了就是先把 −1 抄进计划里（理由与插入档同一条）。
        ResolveTempKeys(EntityState.Modified);

        List<Bucket>? buckets = null;
        Dictionary<string, int>? index = null;
        var tag = new ExecutionTag("update", [_contract.Table.Name], _cluster);

        // 第一遍只判脏与取值，一条语句都不发：合并的前提是"先看清同一种改动在这一批里有几行"，
        // 逐行发射看不到后面还有没有同伴。
        foreach (var node in _order)
        {
            if (node.State != EntityState.Modified)
                continue;

            var entity = (T) node.Entity;
            List<int>? dirty = null;

            for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
            {
                if (!node.Dirty[ordinal])
                    continue;

                if (!_writable[ordinal])
                    throw Unwritable(ordinal, node);

                (dirty ??= []).Add(ordinal);
            }

            // 显式把每一列都标成不脏 = 这一行放弃写入。它照样会在 PostFlush 里结算掉，而不是下一次再试：
            // 留着一个「永远脏、永远不发语句」的状态，HasChanges 就再也不能当「有没有待写的东西」用。
            if (dirty is null)
                continue;

            var key = RequireDefinite(entity, "写回库里");
            var values = new object?[dirty.Count];

            for (var i = 0; i < values.Length; i++)
                values[i] = _schema.GetValue(entity, dirty[i]);

            int[] ordinals = [.. dirty];
            var names = ColumnNames(ordinals);
            var mask = string.Join(',', names);

            index ??= new(StringComparer.Ordinal);
            buckets ??= [];

            if (index.TryGetValue(mask, out var at))
                buckets[at].Rows.Add(new PendingUpdate(node, entity, ordinals, key, values));
            else
            {
                index[mask] = buckets.Count;
                buckets.Add(new Bucket(ordinals, names, [new PendingUpdate(node, entity, ordinals, key, values)]));
            }
        }

        if (buckets is null)
            return [];

        var operations = new List<SaveChangesOp>(buckets.Count);

        foreach (var bucket in buckets)
            operations.Add(bucket.Rows.Count > 1 && Mergeable(bucket)
                ? await WriteMergedAsync(bucket, traces, tag, ct).ConfigureAwait(false)
                : await WriteSingularAsync(bucket, traces, tag, ct).ConfigureAwait(false));

        return operations;
    }

    /// <summary>这一桶能不能合成一条。最前面一道问法：这一家有没有"把另一张表带进写语句"的语法
    /// （SupportsJoinedWrites）——不在这里问就只能到发射层问，而发射层抛的是构建期异常：
    /// 对一个没有 UPDATE…FROM 的库，SaveChanges 会在"两行同掩码"这种最常见形状上红，而不是退化成一句一实体。
    /// 带不带并发列是逐类型的结构性事实（合并语句压根不回填东西）；可不可比逐列问 provider。
    /// 顺序也是断言的一部分：这一道在逐列问守卫之前，盖不住的列都不会被问到。</summary>
    bool Mergeable(Bucket bucket)
    {
        if (!_orm.Provider.Dialect.SupportsJoinedWrites)
            return false;

        if (_tokens.Length > 0)
            return false;

        var guard = _orm.Provider.ChangeGuard;

        foreach (var ordinal in bucket.Ordinals)
            if (!guard.Compares(_columns[ordinal]))
                return false;

        return true;
    }

    /// <summary>一个实体一句。合并没接管的那些桶（单行、带并发列、守卫盖不全）都走这里，
    /// 文本保持与逐条路径一致的形状——"合并是加法而不是改写"。</summary>
    async Task<SaveChangesOp> WriteSingularAsync(Bucket bucket, List<HookTrace> traces, ExecutionTag tag, CancellationToken ct)
    {
        // 步号 = 追加时刻的 traces 长度：两个档各自从当前长度起，跨桶连续，
        // 而不必把 ref 带进异步口（C# 不允许异步方法有 ref 参数）。
        var step = traces.Count;
        var affected = 0;
        string? first = null;

        foreach (var row in bucket.Rows)
        {
            var sets = Sets(row);

            // 应用自维护的并发列（如 int Version）：在 SET 里加 Version = Version + 1。
            // 应用给值的那一族不加——它们的新值在实体上，Sets() 已按脏判定带进来了。
            foreach (var ordinal in _appMaintainedTokens)
            {
                if (Array.IndexOf(_appSetsTokens, ordinal) >= 0)
                    continue;

                sets.Add(new SetClause(_columns[ordinal], new SqlBinaryExpression(BinaryOperator.Add,
                    new ColumnExpression(_table, _columns[ordinal]), new ParameterValue(1, _columns[ordinal]))));
            }

            // 并发令牌全是应用自维护的，没有 OUTPUT/RETURNING 回读。
            var sql = Emit(new UpdateStatement
            {
                Table = _table,
                Sets = sets,
                Where = WhereWithTokens(row.Key, row.Node.Snapshot),
            }, sets.Count + row.Key.Length, traces, ref step);

            var rows = await _orm.Ado.NonQueryAsync(sql, tag, ct).ConfigureAwait(false);

            // 0 行 + 没有并发列：那一行不在了，行数进报告，不抛。
            // 0 行 + 有并发列 = 说不清是"别人改过"还是"行没了"，必须问一次，不能猜。
            if (rows == 0 && _tokens.Length > 0)
                throw await ConflictAsync(row.Entity, row.Node.Snapshot, row.Key, "update", sql, ct).ConfigureAwait(false);

            // 应用自维护的并发列：写成功后直接在实体上 +1。应用给值的那一族不 +1——实体手里已经是新值。
            foreach (var ordinal in _appMaintainedTokens)
            {
                if (Array.IndexOf(_appSetsTokens, ordinal) < 0)
                    IncrementTokenValue(row.Entity, ordinal);
            }

            affected += rows;
            first ??= sql.Text;
        }

        // 语句数就是行数：这一档没有"两行合一条"，报告里那两个数相等，与合并档正好相对。
        return Operation(WriteOperation.Update, bucket.Rows.Count, affected, bucket.ColumnNames, first!);
    }

    /// <summary>合并档：一批"键 + 要写的值"作为一个字面行集参与连接（SourceRows），N 只实体一条语句。
    /// 块大小按参数预算那一式算，分母是每行参数数 = 键列 + 改动列（守卫与 ON 引的都是行集那一侧的列）。</summary>
    async Task<SaveChangesOp> WriteMergedAsync(Bucket bucket, List<HookTrace> traces, ExecutionTag tag, CancellationToken ct)
    {
        var step = traces.Count;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var columns = SideColumns(bucket);
        var rowsPerStatement = StatementEmitter.RowsPerStatement(budget, columns.Length);
        var sets = SetsOf(bucket);
        var where = ChangeGuard(bucket);
        var on = KeyEquality();
        var rows = bucket.Rows;
        var statements = 0;
        var affected = 0;
        string? first = null;

        for (var at = 0; at < rows.Count; at += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Count - at);
            var values = new IReadOnlyList<SqlExpression>[count];

            for (var row = 0; row < count; row++)
                values[row] = SideRow(columns, rows[at + row]);

            var sql = Emit(new UpdateStatement
            {
                Table = _table,
                Sets = sets,
                Where = where,
                Joins = [new JoinClause(JoinKind.Inner, new SourceRows(_rowSet, columns, values), on)],
            }, columns.Length * count, traces, ref step);

            statements++;
            affected += await _orm.Ado.NonQueryAsync(sql, tag, ct).ConfigureAwait(false);
            first ??= sql.Text;
        }

        return Operation(WriteOperation.Update, statements, affected, bucket.ColumnNames, first!);
    }

    /// <summary>行集那一侧的列清单，顺序即每行参数的顺序：键在前、改动列在后。
    /// AS [t1] ([Id], [Name]) 那一份清单与 SideRow 都从这里出，两处不可能错开
    /// ——一旦错开，值就整体串列，那种 UPDATE 每家库都执行得动。</summary>
    ColumnContract[] SideColumns(Bucket bucket)
    {
        var columns = new ColumnContract[_keyOrdinals.Length + bucket.Ordinals.Length];
        var at = 0;

        foreach (var ordinal in _keyOrdinals)
            columns[at++] = _columns[ordinal];

        foreach (var ordinal in bucket.Ordinals)
            columns[at++] = _columns[ordinal];

        return columns;
    }

    static IReadOnlyList<SqlExpression> SideRow(ColumnContract[] columns, PendingUpdate row)
    {
        var cells = new SqlExpression[columns.Length];

        for (var i = 0; i < row.Key.Length; i++)
            cells[i] = new ParameterValue(row.Key[i], columns[i]);

        for (var i = 0; i < row.Values.Length; i++)
            cells[row.Key.Length + i] = new ParameterValue(row.Values[i], columns[row.Key.Length + i]);

        return cells;
    }

    /// <summary>逐行那句的 SET：值来自实体当前值，一颗参数一格。</summary>
    List<SetClause> Sets(PendingUpdate row)
    {
        var sets = new List<SetClause>(row.Values.Length);

        for (var i = 0; i < row.Values.Length; i++)
            sets.Add(new SetClause(_columns[row.Ordinals[i]], new ParameterValue(row.Values[i], _columns[row.Ordinals[i]])));

        return sets;
    }

    /// <summary>合并那句的 SET：右侧是行集那一侧的同一列，一整批只有一份 SET，
    /// 逐行的差别全在 VALUES 里——这是合并省下 SQL 文本编译的那一半（另一半是往返次数）。</summary>
    List<SetClause> SetsOf(Bucket bucket)
    {
        var sets = new List<SetClause>(bucket.Ordinals.Length);

        foreach (var ordinal in bucket.Ordinals)
        {
            var column = _columns[ordinal];

            sets.Add(new SetClause(column, new ColumnExpression(_rowSet, column)));
        }

        return sets;
    }

    /// <summary>变更守卫：改动列逐列问 provider「库里这一格与行集那一格是不是不同」，再 OR 起来。
    /// 只有改动列进守卫，键列不进（跟踪器不改主键，把键列也比一遍只会让谓词更长）。
    /// 右倾折是因为方言对同种连接词不加括号，这一串出来的是一条平铺的 OR。</summary>
    SqlExpression ChangeGuard(Bucket bucket)
    {
        var guard = _orm.Provider.ChangeGuard;
        var differences = new SqlExpression[bucket.Ordinals.Length];

        for (var i = 0; i < differences.Length; i++)
        {
            var column = _columns[bucket.Ordinals[i]];

            differences[i] = guard.BuildDifference(column, new ColumnExpression(_table, column), new ColumnExpression(_rowSet, column));
        }

        var where = differences[^1];

        for (var i = differences.Length - 2; i >= 0; i--)
            where = new SqlBinaryExpression(BinaryOperator.Or, differences[i], where);

        return where;
    }

    /// <summary>连接条件：主键逐列相等。与 KeyPredicate 的区别只在右侧——那里是一颗参数，
    /// 这里是行集的那一列（varchar 键与 nvarchar 行集的比较仍要看排序规则）。</summary>
    SqlExpression KeyEquality()
    {
        SqlExpression? on = null;

        foreach (var ordinal in _keyOrdinals)
        {
            var column = _columns[ordinal];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, column), new ColumnExpression(_rowSet, column));

            on = on is null ? equal : new SqlBinaryExpression(BinaryOperator.And, on, equal);
        }

        return on!;
    }

    IReadOnlyList<string> ColumnNames(int[] ordinals)
    {
        var names = new string[ordinals.Length];

        for (var i = 0; i < names.Length; i++)
            names[i] = _columns[ordinals[i]].ColumnName;

        return names;
    }

    /// <summary>删除：一句实体一句按主键的 DELETE。报告的 Columns 软删在册时交 SET 里点名的列，
    /// 没在册时交空——语句里没有 SET，把主键列填进去会让人以为它能像 UPDATE 那样挑列；
    /// 口径是"这条语句自己点名了哪几列"，不是"这条链的名字叫 delete"。</summary>
    async Task<IReadOnlyList<SaveChangesOp>> WriteDeletesAsync(List<HookTrace> traces, CancellationToken ct)
    {
        var statements = 0;
        var rows = 0;
        string? first = null;
        var step = traces.Count;

        // 软删在这一档同样生效，摘掉的每一行共用同一颗时间：一次 SaveChanges 摘三行是"一次删除动作"，
        // 各读一次时钟就把一件事记成三个时刻。规则为空时 soft 与 stamp 都是 null，物理删除路径不多付一次查表。
        var soft = _orm.Behaviors.HasAny ? _orm.Behaviors.RuleFor(typeof(T)) : null;
        var stamp = soft?.Stamp();
        // tag 只依赖表名与本会话，提到循环外，不在每条 DELETE 上新建一颗 ExecutionTag 外加一只一元数组。
        var deleteTag = new ExecutionTag("delete", [_contract.Table.Name], _cluster);

        foreach (var node in _order)
        {
            if (node.State != EntityState.Deleted)
                continue;

            // 删哪一行以快照为准：那是当初把这一行认成「库里那一条」的依据。
            // 主键被改过就不能按快照删（会删错一行），也不能按当前值删（那一行未必存在）—— 只能拒。
            foreach (var ordinal in _keyOrdinals)
                if (!Equals(_schema.GetValue((T) node.Entity, ordinal), node.Snapshot[ordinal]))
                    throw Unwritable(ordinal, node);

            object?[] key = [.. _keyOrdinals.Select(ordinal => node.Snapshot[ordinal])];

            // "已跟踪节点标删 → 发删除语句"这条路只查了"有没有主键列"：快照里躺着 0 / Guid.Empty 的键
            // 会发出一句 WHERE [Id] = 0——命中 0 行、无并发列时连冲突判据都不触发，删除意图静默丢失而报告只说「0 行」。
            // 发射前最后一道闸，覆盖所有到达路径。
            if (!IsDefinite(key))
                throw new QuickSqlException($"{_contract.Table.Name} 的这一行快照里主键没有可定位的值（{Text(key)}），而删除要按主键定位行：" +
                    "一句按 0 / 空键写的 WHERE 只会命中 0 行，报告却照样说「0 行」——删除意图就这么静默丢了。" +
                    "库递出来的行本来就该有键；客户端给键的表请先把值填上再标删。");

            var where = WhereWithTokens(key, node.Snapshot);
            var ast = new DeleteStatement { Table = _table, Where = where };
            var sql = Emit(ast, soft?.Rewrite(_contract, _table, where, stamp, node.Entity, _orm.Aop.SoftDeleteValues), key.Length + _tokens.Length, traces, ref step);

            var affected = await _orm.Ado.NonQueryAsync(sql, deleteTag, ct).ConfigureAwait(false);

            // 删除的并发谓词同样取快照：意图是「删掉我读到的那一版」。这一档不回填任何东西——
            // 语句成功就意味着行不在了。软删在册时 WHERE 仍是主键 + 并发列（软删不改条件），
            // 0 行仍然只有"版本不匹配"与"行已经不在了"两种成因；刻意不往 WHERE 加"且尚未置位"那一支，
            // 加了才需要"被挡住的行算不算一次并发冲突"那句裁决。
            if (affected == 0 && _tokens.Length > 0)
                throw await ConflictAsync((T) node.Entity, node.Snapshot, key, "delete", sql, ct).ConfigureAwait(false);

            rows += affected;
            statements++;
            first ??= sql.Text;
        }

        // 软删在册时那一格交的是 SET 里点名的列，没在册时交空（判据见方法注释）。
        // 按批算而不是按行算：一次 SaveChanges 的删除档只产一份报告，列名与"几句、几行"同一层。
        return statements == 0
            ? []
            : [Operation(WriteOperation.Delete, statements, rows, soft is null ? [] : soft.SetColumns(_contract), first!)];
    }

    /// <summary>整批写成功之后的结算（三档共用一次，拿到的是「全部成功」这个前提）。
    /// 删除的摘出会话；新增的把库刚给的主键登记进身份映射；其余全部重拍快照并转 Unmodified。</summary>
    public void PostFlush()
    {
        foreach (var node in _order.ToArray())
        {
            var entity = (T) node.Entity;

            // 没欠语句的实例不重拍快照：结算的代价不该随会话里的实例总数涨
            // （一次写两行不该读两万遍字段），未改动的 Unmodified 节点的快照本来就是当前值。
            if (node.State == EntityState.Unmodified)
                continue;

            switch (node.State)
            {
                case EntityState.Deleted:
                    Detach(node);

                    continue;

                case EntityState.Added:
                    // 插完才可能有键：自增列此刻才被 InsertEngine 回填进实体，所以登记身份的时刻在这里。
                    if (node.Registered is null)
                    {
                        var values = KeyValues(entity);

                        if (IsDefinite(values))
                        {
                            var key = new IdentityKey(values);

                            // 撞上别人只有一种来路：库里那一行的键原本被当成「没填」（所以没登记），
                            // 而会话里另一只先一步拿到了它。静默覆盖就是两只实例共用一个身份，
                            // 之后改哪一只都写同一行。
                            if (_identity.TryGetValue(key, out var clash))
                                throw new QuickSqlException(Collision(clash, values) + " 而这一行已经写进库了，所以退不回「没插」：要清干净请另开一个会话。");

                            _identity[key] = node;
                            node.Registered = key;
                        }
                    }

                    break;
            }

            node.State = EntityState.Unmodified;
            Reset(node, entity);
        }

        // 临时键的账到这一句就清完：三档全部发完，依赖方手里那些颗都已经被换掉
        // （整批抛在半路时这里走不到）。号本身不回退（_tempCursor 单调往负走），下一轮不会重放一个旧临时值。
        _temps?.Clear();

        _mayHaveWrites = false;
    }

    /// <summary>一个报告桶，也就是合并单位：列掩码相同的那几只实体。拿得出掩码当键而不出错：
    /// 同一个桶里每只实体的改动列集合逐字相同，只是行的取值不同——合并语句的那一份 SET 与列清单因此只有一份。
    /// Rows 是待写的行，不是回来的行数（那个数在报告里）。</summary>
    sealed record Bucket(int[] Ordinals, IReadOnlyList<string> ColumnNames, List<PendingUpdate> Rows);

    /// <summary>一只待写的行。Values 与 Ordinals 同序，两份在合并档共用、逐条档各取所需，
    /// 不需要在合并之前把值包成参数再拆回来。</summary>
    sealed record PendingUpdate(Node Node, T Entity, int[] Ordinals, object?[] Key, object?[] Values);

    SaveChangesOp Operation(WriteOperation operation, int statements, int rows, IReadOnlyList<string> columns, string sql)
        => new(operation, _contract.Table.Name, rows, statements, columns, sql);

    DialectSql Emit(UpdateStatement ast, int parameters, List<HookTrace> traces, ref int step)
    {
        var dialect = _orm.Provider.Dialect;
        var writer = StatementEmitter.Writer(dialect, StatementEmitter.EstimateLength(parameters, _identifierChars));

        dialect.Emit(in ast, writer);

        return Bind(new DialectSql(writer.ToSqlString(), writer.Parameters), "update", traces, ref step);
    }

    /// <summary>摘行那一句的发射。soft 非 null 时发的是软删改写后的那一棵 UPDATE（分岔在 StatementEmitter.EmitDelete，
    /// 与链式 Delete 和批量 DeleteRange 共用一份），文本仍走改写钩子，日志看到的就是真正发出去的那一句——
    /// 不会出现"日志里写着 DELETE、库里其实是 UPDATE"。</summary>
    DialectSql Emit(DeleteStatement ast, UpdateStatement? soft, int parameters, List<HookTrace> traces, ref int step)
        => Bind(StatementEmitter.EmitDelete(_orm.Provider.Dialect, ast, soft, _identifierChars, parameters), "delete", traces, ref step);

    /// <summary>两个钩子的顺序即语义：CudBuilding 改整句文本（必须在参数级改写之前，
    /// 否则改写者新加的参数没人过钩子），ParameterBinding 改值。跟踪器这一份与三条链同一份实现。</summary>
    DialectSql Bind(DialectSql sql, string operationKind, List<HookTrace> traces, ref int step)
        => StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, sql, operationKind, traces, ref step), traces, ref step);

    // ── 内部 ──────────────────────────────────────────────────────────

    /// <summary>登记。key 为 null 表示「不进身份映射」，而它仍然进引用表与跟踪序——
    /// 一个键还没填的实例依旧是会话里的一个实例，只是查不到它。</summary>
    Node Register(object entity, EntityState state, IdentityKey? key, T typed)
    {
        var node = new Node(entity, _columns.Length, _contract.Collections.Count) { State = state, Registered = key };

        Snapshot(node, typed);
        RememberCollections(node, typed);

        _byRef[entity] = node;
        _order.Add(node);

        if (key is not null)
            _identity[key.Value] = node;

        if (state is not EntityState.Unmodified)
            _mayHaveWrites = true;

        return node;
    }

    /// <summary>把这一张实体手里那几串集合位此刻的成员记成基线——摘行那一档唯一的判据：
    /// collection.Remove(x) 既不发事件也不留痕迹，会话只能拿上一次看见的那一串跟现在这一串比。
    /// 登记入口只有一处（Register），Add / Attach / 物化各记一次，Load 之后由 RememberCollections(T) 补记；
    /// 没有集合边的类型上这一步一次都不跑（node.Baselines 是 null）。</summary>
    void RememberCollections(Node node, T entity)
    {
        if (node.Baselines is not { } baselines)
            return;

        var edges = _contract.Collections;

        for (var slot = 0; slot < edges.Count; slot++)
            if (edges[slot].Cascade)
                baselines[slot] = CascadeWalker.LiveMembers(edges[slot], entity);
    }

    /// <summary>DbSet.Load 缝完一根集合导航之后重新记基线：从这一刻起"这一串里有谁"是一句捞过的声明，
    /// 之后从里面摘掉一只就是要删那一行。不在会话里的实例不记——会话对它没有账，也就没有要还的摘行。</summary>
    internal void RememberCollections(T parent)
    {
        if (_byRef.TryGetValue(parent, out var node))
            RememberCollections(node, parent);
    }

    void Detach(Node node)
    {
        // 按节点存的那个键摘，而不是按当前值重算：改过主键的实例若按新值去摘，摘掉的是别人的那一条。
        if (node.Registered is { } key && _identity.TryGetValue(key, out var held) && ReferenceEquals(held, node))
            _identity.Remove(key);

        _byRef.Remove(node.Entity);
        _order.Remove(node);

        // 状态归到 Detached 而不是留在摘掉前那一格：调用方手里可能还有一只 EntityEntry（它只是视图不持状态），
        // 留着 Modified 就成了「一个已经不在会话里的实例还欠着一条 UPDATE」。
        node.State = EntityState.Detached;
    }

    void Snapshot(Node node, T entity)
    {
        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
            node.Snapshot[ordinal] = _schema.GetValue(entity, ordinal);
    }

    void Reset(Node node, T entity)
    {
        Snapshot(node, entity);

        Array.Fill(node.Overrides, (bool?) null);
        Array.Clear(node.Dirty);
    }

    /// <summary>整行强制脏。只覆盖可写列：主键与自增列一旦进 Node.Overrides，发射前就会撞 Unwritable，
    /// 「把 State 设成 Modified」这一句最常见的写法变成必抛——那不是「全写」，那是没法写。</summary>
    void MarkAllWritable(Node node)
    {
        for (var ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            if (!_writable[ordinal])
                continue;

            node.Overrides[ordinal] = true;
            node.Dirty[ordinal] = true;
        }
    }

    object?[] KeyValues(T entity)
    {
        var values = new object?[_keyOrdinals.Length];

        for (var i = 0; i < values.Length; i++)
            values[i] = _schema.GetValue(entity, _keyOrdinals[i]);

        return values;
    }

    object?[] RequireDefinite(T entity, string what)
    {
        var values = KeyValues(entity);

        if (IsDefinite(values))
        {
            // 临时主键是「有值，但那不是库里的键」，躲得过上面那一判而躲不过这一句：
            // 按 -1 去定位一句 UPDATE/DELETE 命中 0 行，报告只会说"0 行"——静默丢写。
            // 判据用 ProbeTemp 而不是"看着像负数"：只有本档真发出去过的那一颗才算临时键，
            // 调用方自己填的负数键（库里真有这么一行）不该被这一句挡下来。
            if (values.Length == 1 && ProbeTemp(values[0], out _) != TempProbe.NotTemp)
                throw new QuickSqlException($"{_contract.Table.Name} 的这一行手里还是临时主键 {Text(values)}，而 {what} 要按主键定位库里那一行：" +
                    "那个负数不是库里的键，一句按它写的 WHERE 只会命中 0 行。先 SaveChanges 让库把真实的键给回来（§8.2 的 Tp）。");

            return values;
        }

        throw new QuickSqlException($"{_contract.Table.Name} 的主键现在没有值（{Text(values)}），而 {what} 要按主键定位行：" +
            "一句 WHERE [主键] = NULL 什么都匹配不到，却会安静地报告「0 行」。自增列请把它标为新增、让库先把键给回来；键由客户端生成的话先把值填上。");
    }

    void RequireKey()
    {
        if (_keyOrdinals.Length > 0)
            return;

        throw new QuickSqlException($"{_contract.Table.Name} 没有主键：跟踪器认不出「这一行」，而身份映射与 UPDATE/DELETE 的 WHERE 都建立在主键上（§8.2）。" +
            $"只读请走 orm.Select<{typeof(T).Name}>()。这就是 §8.2 那句「无 PK 自动不跟踪」今天的形态 —— 它是<em>拒绝跟踪并指路</em>，不是静默降级（§12.2）。");
    }

    static bool IsDefinite(object?[] values)
    {
        foreach (var value in values)
        {
            if (value is null || Unset(value))
                return false;
        }

        return true;
    }

    /// <summary>「这个值其实是没填」：整数 0 与 Guid.Empty。判得出来的依据是自增列的种子不是 0
    /// （IDENTITY(1,1) 与 SEQUENCE START WITH 的默认都不是），Guid.Empty 不会由 NEWID() 生成。
    /// 空串不算未填：字符串列没有「零值」这一说，"" 只能是有人写的。枚举主键会被判成已填
    /// （装箱后匹配不到这些 case），而 0 号枚举值确实是合法键——这一侧偏保守是对的方向。</summary>
    static bool Unset(object value) => value switch
    {
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        byte b => b == 0,
        uint u => u == 0,
        ulong ul => ul == 0,
        Guid g => g == Guid.Empty,
        _ => false,
    };

    /// <summary>CLR 型是不是有符号整型之一（含 nullable 包装）。应用自维护的并发列只认这四型，
    /// 因为只有它们在回填那一步有 +1 语义。uint/ulong 特意不算：与 IncrementTokenValue 的支持面对不上，
    /// 那类实体会安静通过启动期、到第一次 SaveChanges 的回填步才炸；收窄之后它们走回读——
    /// 值库不改，"只比较不书写"的并发语义原样成立。datetime2、byte[]（rowversion）等并发列不属于这一档。</summary>
    static bool IsIntegerClrType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(byte);
    }

    string Collision(Node clash, object?[] values)
        => $"{_contract.Table.Name} 的主键 {Text(values)} 已经有另一个实例在被跟踪（{clash.State}）。同键两只实例在会话里没有定义：写谁？";

    QuickSqlException Unwritable(int ordinal, Node node)
    {
        var column = _columns[ordinal];

        if (column.IsConcurrencyToken)
            return new($"{column.MemberName}（列 {column.ColumnName}）被判定为已改动，而它是并发列：那一列只比较、不书写（§8.2），进不了 SET。" +
                $"实体里现在是 {StatementEmitter.Text(_schema.GetValue((T) node.Entity, ordinal))}，会话认库里那一版是 {StatementEmitter.Text(node.Snapshot[ordinal])}。" +
                "两种来路：要么有人往这一列里写了值（库维护的并发列由库赋值，应用自维护的由 SET Version = Version + 1 自动递增，都不接受手动赋值），" +
                "要么上一批保存被回滚过（回滚不会把已经读进实体的值撤回，会话因此停在「以为写过了」的那一版）。" +
                "要么把这一列改回原值，要么显式 Property(...).IsModified = false，要么丢弃这一只实例。");

        return new($"{column.MemberName}（列 {column.ColumnName}）被判定为已改动，而它是主键或自增列，进不了 SET。" +
            "改「已经有值的主键」是换键外加把所有依赖方的外键那一格一起改过去，比 Tp 那一档远（临时主键只管「新行的键还没值」，§8.2），今天没有；" +
            "自增列不接受写，那是 IDENTITY_INSERT 那条路。要么把这一列改回原值，要么显式 Property(...).IsModified = false。");
    }

    static string Text(object?[] values)
        => string.Join("、", values.Select(static value => value?.ToString() ?? "NULL"));

    /// <summary>主键合取式。逐列 = 再 AND，不用 (a, b) IN ((…))：行值构造器的支持面在国产库里参差
    /// （PG 血统可用而达梦不稳），一句单行的 WHERE a = @p AND b = @q 没有任何一家需要特殊照顾。</summary>
    SqlExpression KeyPredicate(object?[] values)
    {
        SqlExpression? where = null;

        for (var i = 0; i < values.Length; i++)
        {
            var column = _columns[_keyOrdinals[i]];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, column), new ParameterValue(values[i], column));

            where = where is null ? equal : new SqlBinaryExpression(BinaryOperator.And, where, equal);
        }

        return where!;
    }

    /// <summary>标识部分 ∧ 并发列。并发谓词是并进 AST 的 WHERE、在发射之前，不是往文本尾巴上接一句 AND：
    /// UPDATE…OUTPUT…WHERE 与 UPDATE…SET…WHERE…RETURNING 两家的收尾位置不一样（T-SQL 的 OUTPUT 在 FROM 之前，
    /// PG 系的 RETURNING 在整句之后），文本级追加必在其中一家写出语法错，参数编号也会跟着乱。</summary>
    SqlExpression WhereWithTokens(object?[] key, object?[] snapshot)
    {
        var tokens = ConcurrencyPredicate(snapshot);

        return tokens is null ? KeyPredicate(key) : new SqlBinaryExpression(BinaryOperator.And, KeyPredicate(key), tokens);
    }

    /// <summary>并发列的合取式，值取快照而不是实体当前值：这一句回答的是「我读到的那一版还在不在」，
    /// 拿当前值去比就是拿「我想写成的样子」当「我以为库里的样子」，那样的 WHERE 永远命中 0 行。</summary>
    SqlExpression? ConcurrencyPredicate(object?[] snapshot)
    {
        SqlExpression? and = null;

        foreach (var ordinal in _tokens)
        {
            var column = _columns[ordinal];
            var value = snapshot[ordinal];
            var target = new ColumnExpression(_table, column);

            // 可空的并发列：库里那一格当时是 NULL 就只能写 IS NULL——[列] = @p 在哪家都是 UNKNOWN，
            // 每一次保存都成假冲突。值的比较形状归 provider，因为只有它知道 rowversion 该按什么比。
            var match = value is null
                ? new SqlUnaryExpression(UnaryOperator.IsNull, target)
                : _orm.Provider.Concurrency.BuildPredicate(target, column, value);

            and = and is null ? match : new SqlBinaryExpression(BinaryOperator.And, and, match);
        }

        return and;
    }

    /// <summary>应用自维护的并发列在写成功后直接 +1：库已经把 Version = Version + 1 写进去了，
    /// 实体手里那一格还停在旧值上，下一次保存的 WHERE 会拿旧值去比——假冲突。
    /// 这里读当前值、加一、写回去，与库那一侧的改写保持同步。</summary>
    void IncrementTokenValue(T entity, int ordinal)
    {
        var current = _schema.GetValue(entity, ordinal);

        object? next = current switch
        {
            int i => i + 1,
            long l => l + 1,
            short s => (short) (s + 1),
            byte b => (byte) (b + 1),
            _ => throw new QuickSqlException($"{_columns[ordinal].MemberName}（列 {_columns[ordinal].ColumnName}）是应用自维护的并发列，" +
                $"但它的 CLR 型是 {current?.GetType().Name ?? "null"}，不支持 +1 自增。应用自维护的并发列只支持 int / long / short / byte。"),
        };

        _schema.SetValue(entity, ordinal, next);
    }

    /// <summary>冲突补读那一格的值。GetValue 对 NULL 给的是 DBNull，直接塞进消息就是 "System.DBNull"，
    /// 所以这里折成 null。</summary>
    static object? Value(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    /// <summary>0 行那一条的成因分查："别人改过"与"这一行没了"在行数上一模一样，而处置相反
    /// （前者要合并，后者通常丢弃意图），所以抛出前按主键补读一次。代价只付在冲突那一条上。</summary>
    async Task<DbUpdateConcurrencyException> ConflictAsync(T entity, object?[] snapshot, object?[] key, string operation, DialectSql sql, CancellationToken ct)
    {
        var current = new Dictionary<string, object?>(_tokens.Length);
        var database = await ReadTokensAsync(key, ct).ConfigureAwait(false);
        var lines = new string[_tokens.Length];

        for (var i = 0; i < _tokens.Length; i++)
        {
            var column = _columns[_tokens[i]];
            var expected = snapshot[_tokens[i]];
            var actual = database is null ? null : database[i];

            current[column.MemberName] = _schema.GetValue(entity, _tokens[i]);

            lines[i] = $"{column.MemberName}: 会话以为是 {StatementEmitter.Text(expected)}，库里" +
                (database is null ? "已经没有这一行" : $"是 {StatementEmitter.Text(actual)}");
        }

        Dictionary<string, object?>? databaseValues = database is null ? null : ToTokenDictionary(database);

        return new DbUpdateConcurrencyException(
            $"{_contract.Table.Name} 的一句 {operation} 命中 0 行，而它带着并发列的谓词。" +
            (database is null ? "按主键补查也没有这一行：它被删掉了。" : "并发列在库里的现值与快照不同：它被别人改过。") +
            $"逐列：{string.Join("；", lines)}。" +
            $"最终执行的语句：{sql.Text}",
            entity, operation, sql.Text, current, databaseValues);
    }

    Dictionary<string, object?> ToTokenDictionary(object?[] values)
    {
        var dictionary = new Dictionary<string, object?>(values.Length);

        for (var i = 0; i < values.Length; i++)
            dictionary[_columns[_tokens[i]].MemberName] = values[i];

        return dictionary;
    }

    /// <summary>按主键补读那几列。只读并发列，不整行物化：整行刷新是 Refresh 那一格的活（未接线），
    /// 这里只要回答"这一行还在不在、那几列现在是什么"。这一条语句不过 CudBuilding 也不过参数钩子：
    /// 它的 WHERE 必须与刚才那句的标识部分逐字同一把尺子，被改写钩子换成别的东西就答错了题。</summary>
    async Task<object?[]?> ReadTokensAsync(object?[] key, CancellationToken ct)
    {
        var projections = new Projection[_tokens.Length];

        for (var i = 0; i < _tokens.Length; i++)
            projections[i] = new Projection(new ColumnExpression(_table, _columns[_tokens[i]]), null);

        var select = new SelectStatement { Columns = projections, From = new SourceTable(_table), Where = KeyPredicate(key) };

        var dialect = _orm.Provider.Dialect;
        var writer = StatementEmitter.Writer(dialect, StatementEmitter.EstimateLength(key.Length, _identifierChars));

        dialect.Emit(in select, writer);

        var sql = new DialectSql(writer.ToSqlString(), writer.Parameters);

        object?[] Map(DbDataReader reader)
        {
            var values = new object?[_tokens.Length];

            for (var i = 0; i < values.Length; i++)
                values[i] = Value(reader, i);

            return values;
        }

        return await _orm.Ado.FirstAsync(sql, Map, new ExecutionTag("concurrency-read", [_contract.Table.Name], _cluster), ct).ConfigureAwait(false);
    }
}
