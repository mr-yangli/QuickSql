using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Write;

namespace QuickSql;

/// <summary>集合同步链（SqlSugar 的 Storageable 同一语义）：给一个集合、给一把键（不给就用实体主键），
/// 与库对账出新增/修改/删除三个桶，执行与否由调用方定。对账与执行是两句话：ToStorage 只发读、一个字节都不写，
/// ExecuteCommandAsync 一口气全做，顺序固定删 → 改 → 增（唯一列换主的那类调用，先改后插会在旧值还在场上时
/// 撞唯一约束，先摘后种没有这一格）。键的两档：不给 WhereColumns 用契约主键（复合主键天然支持），给了就按点名的
/// 属性来；自增键"没填"（0/null）的行不查库直接进新增桶，清单里同一颗键出现两次当场拒。对账的形状要如实说：
/// 存在性判定按参数预算把键分块，每块发一句 OR 平铺的 SELECT、只投键列（不是 IN——多列键的行值 IN 没有可移植写法）；
/// 删除桶要读整表才能回答"库里有而清单里没有的是谁"，大表先想清楚再开；清单为空而删除桶开着当场拒。</summary>
/// <typeparam name="T">实体型。契约解析与键读口带反射税（G2 那一档），标注跟着入口走。</typeparam>
public interface IStorageable<T> where T : class
{
    /// <summary>指定对账的键。单颗属性（it => it.Sku）或匿名类型多颗（it => new { it.KeyA, it.KeyB }）。
    /// 不给这一句 = 用实体主键。</summary>
    IStorageable<T> WhereColumns(Expression<Func<T, object?>> keySelector);

    /// <summary>把"库里有而清单里没有"的那一半也对出来（DeleteList）。默认不开：它要读整表。
    /// 清单为空时这一句与 ToStorageAsync 组合当场拒。</summary>
    IStorageable<T> SplitDelete(bool on = true);

    /// <summary>带范围的删除桶：只在 scope 圈住的那一片里读与摘——"把这一组货架对成这份清单"
    /// 而不碰架上别的货；不圈范围的那一档是整表语义。范围不参与对账：增/改两桶仍按清单与全表对。</summary>
    IStorageable<T> SplitDelete(Expression<Func<T, bool>> scope);

    /// <summary>与库对账：只发读，不写。同步壳走 async 那一颗（与 SaveChanges 同一条路）。</summary>
    [RequiresUnreferencedCode("G2 契约按 Type 反射解析实体，键读口按反射编译。")]
    [RequiresDynamicCode("键读口编译成委托，谓词翻译的退路是编译表达式树。")]
    StorageResult<T> ToStorage(CancellationToken ct = default);

    /// <summary>与库对账：只发读，不写。见接口抬头那句"对账的形状要如实说"。</summary>
    [RequiresUnreferencedCode("G2 契约按 Type 反射解析实体，键读口按反射编译。")]
    [RequiresDynamicCode("键读口编译成委托，谓词翻译的退路是编译表达式树。")]
    Task<StorageResult<T>> ToStorageAsync(CancellationToken ct = default);
}

/// <summary>对账结果：三个桶都是调用方交来的或库原样读回的实体，执行器只认各自的桶。
/// DeleteList 里的实体是库原样读回的那一份（不是清单里的）——删除清单的本意就是
/// "库里有而清单里没有"，给你看的就是要摘的那些行本身。执行器空桶直接返回 0，不发一句 SQL。</summary>
/// <typeparam name="T">实体型，与链同一颗。</typeparam>
public sealed class StorageResult<T> where T : class
{
    readonly QuickSqlOrm _orm;
    readonly ColumnContract[] _columns;
    readonly Func<object, object>[] _getters;
    readonly bool _keysArePrimaryKey;

    internal StorageResult(IReadOnlyList<T> inserts, IReadOnlyList<T> updates, IReadOnlyList<T> deletes,
        QuickSqlOrm orm, ColumnContract[] columns, Func<object, object>[] getters, bool keysArePrimaryKey)
    {
        InsertList = inserts;
        UpdateList = updates;
        DeleteList = deletes;
        _orm = orm;
        _columns = columns;
        _getters = getters;
        _keysArePrimaryKey = keysArePrimaryKey;
    }

    /// <summary>库里对不上键的那些行 —— 新增桶。</summary>
    public IReadOnlyList<T> InsertList { get; }

    /// <summary>库里对上了键的那些行 —— 修改桶。</summary>
    public IReadOnlyList<T> UpdateList { get; }

    /// <summary>库里有而清单里没有的那些行 —— 删除桶。没开  时恒空。</summary>
    public IReadOnlyList<T> DeleteList { get; }

    /// <summary>一口气执行：删（若开）→ 改 → 增，返回三段受影响行数之和。</summary>
    [RequiresUnreferencedCode("删除谓词按键位属性名建树，翻译的退路是编译表达式树。")]
    [RequiresDynamicCode("删除谓词翻译的退路是编译表达式树。")]
    public async Task<int> ExecuteCommandAsync(CancellationToken ct = default)
    {
        var total = await ExecuteDeleteAsync(ct).ConfigureAwait(false);
        total += await ExecuteUpdateAsync(ct).ConfigureAwait(false);
        total += await ExecuteInsertAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>只执行新增桶（orm.InsertRange 那条链）。</summary>
    public async Task<int> ExecuteInsertAsync(CancellationToken ct = default)
        => InsertList.Count == 0
            ? 0
            : await _orm.InsertRange(InsertList).ExecuteAffrowsAsync(ct).ConfigureAwait(false);

    /// <summary>只执行修改桶，两条路按键位分岔：键就是主键时走 UpdateRange 的批量
    /// （命中数从报告的 AffectedRows 读——有行被别人删过时这里会少于桶行数，是读数不是错误）；
    /// 点名键不是主键时逐行走 orm.Update 链：SET 是键位之外可写的列，WHERE 是点名键那一颗，一行一句
    /// ——句数就是桶行数，大清单的代价如实记在这里。</summary>
    [RequiresUnreferencedCode("批量建文本要走全局过滤器的叠加（闭包求值），逐行档还要按契约编译列读口。")]
    [RequiresDynamicCode("批量更新的降级退路与逐行档的 Set 列 lambda 都是编译表达式树。")]
    public async Task<int> ExecuteUpdateAsync(CancellationToken ct = default)
    {
        if (UpdateList.Count == 0)
            return 0;

        if (_keysArePrimaryKey)
            return (await _orm.UpdateRange(UpdateList).ExecuteBulkAsync(ct).ConfigureAwait(false)).AffectedRows;

        var contract = _orm.Schemas.Contract<T>();
        var writable = Array.FindAll(contract.Columns,
            c => !c.IsPrimary && !c.ValueFromStore && !c.IsConcurrencyToken);
        var setters = new (Func<object, object> Get, Expression<Func<T, object>> Column)[writable.Length];

        for (var i = 0; i < writable.Length; i++)
        {
            setters[i] = (
                MemberAccessors.Getter(typeof(T), writable[i].MemberName,
                    $"实体 {typeof(T).Name} 上没有属性 {writable[i].MemberName}",
                    $"实体 {typeof(T).Name} 的属性 {writable[i].MemberName} 没有公共 getter"),
                StorageableChain<T>.MemberLambda(writable[i].MemberName));
        }

        var total = 0;

        foreach (var row in UpdateList)
        {
            var chain = _orm.Update<T>();

            foreach (var (get, column) in setters)
                chain = chain.Set<object>(column, get(row));

            var key = StorageableChain<T>.KeyOf(row, _getters, _columns);

            total += await chain.Where(StorageableChain<T>.MatchesAny(_columns, [key]))
                .ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>只执行删除桶：按键分块发 DELETE … WHERE (键=@a) OR …。跨块不是原子的（每句自己提交），
    /// 要原子请自己包 orm.BeginTransaction。</summary>
    [RequiresUnreferencedCode("删除谓词按键位属性名建树，翻译的退路是编译表达式树。")]
    [RequiresDynamicCode("删除谓词翻译的退路是编译表达式树。")]
    public async Task<int> ExecuteDeleteAsync(CancellationToken ct = default)
    {
        if (DeleteList.Count == 0)
            return 0;

        var keys = new List<StorageKey>(DeleteList.Count);
        foreach (var row in DeleteList)
            keys.Add(StorageableChain<T>.KeyOf(row, _getters, _columns));

        var total = 0;
        var size = StorageableChain<T>.ChunkSize(_columns.Length, _orm);

        for (var start = 0; start < keys.Count; start += size)
        {
            var chunk = keys.GetRange(start, Math.Min(size, keys.Count - start));
            total += await _orm.Delete<T>().Where(StorageableChain<T>.MatchesAny(_columns, chunk))
                .ExecuteAffrowsAsync(ct).ConfigureAwait(false);
        }

        return total;
    }
}

/// <summary>IStorageable 的本体。对账读走根链（无会话、不进会话闸），执行走
/// InsertRange/UpdateRange/Delete 三条既有的写链——这一格自己不发明任何一条写路。两读的宽度不一样：
/// 存在性那一读只投键列，删除桶那一读才把整行读回来（DeleteList 交出去的就是给人看的那一份）。</summary>
internal sealed class StorageableChain<T> : IStorageable<T> where T : class
{
    readonly QuickSqlOrm _orm;
    readonly List<T> _rows;
    string[]? _memberNames;
    bool _delete;
    Expression<Func<T, bool>>? _deleteScope;

    internal StorageableChain(QuickSqlOrm orm, IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(orm);
        ArgumentNullException.ThrowIfNull(rows);

        // 立刻物化：看到的那一份与对账的那一份必须是同一份（InsertRange 同一条纪律）。
        _orm = orm;
        _rows = [.. rows];
    }

    /// <inheritdoc/>
    public IStorageable<T> WhereColumns(Expression<Func<T, object?>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        var body = Unwrap(keySelector.Body);

        _memberNames = body switch
        {
            MemberExpression single => [single.Member.Name],
            NewExpression tuple when tuple.Members is { Count: > 0 } => [.. tuple.Arguments.Select(MemberNameOf)],
            _ => throw new QuickSqlException(
                "WhereColumns 只认两种形状：单颗属性（it => it.Sku）或匿名类型多颗（it => new { it.KeyA, it.KeyB }），"
                + $"交来的是 {body.NodeType}。"),
        };

        return this;
    }

    /// <inheritdoc/>
    public IStorageable<T> SplitDelete(bool on = true)
    {
        _delete = on;
        return this;
    }

    /// <inheritdoc/>
    public IStorageable<T> SplitDelete(Expression<Func<T, bool>> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        _delete = true;
        _deleteScope = scope;
        return this;
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode("G2 契约按 Type 反射解析实体，键读口按反射编译。")]
    [RequiresDynamicCode("键读口编译成委托，谓词翻译的退路是编译表达式树。")]
    public StorageResult<T> ToStorage(CancellationToken ct = default)
        => ToStorageAsync(ct).GetAwaiter().GetResult();

    /// <inheritdoc/>
    [RequiresUnreferencedCode("G2 契约按 Type 反射解析实体，键读口按反射编译。")]
    [RequiresDynamicCode("键读口编译成委托，谓词翻译的退路是编译表达式树。")]
    public async Task<StorageResult<T>> ToStorageAsync(CancellationToken ct = default)
    {
        var contract = _orm.Schemas.Contract<T>();
        var columns = ResolveKeys(contract);
        var getters = BuildGetters(columns);

        // 第一步：清单分拣。自增键"没填"的行不查库直接进新增桶（见接口抬头），
        // 其余行带着键去对账；同一颗键说两次话当场拒。
        List<T> inserts = [];
        List<(T Row, StorageKey Key)> keyed = [];

        foreach (var row in _rows)
        {
            var key = KeyOf(row, getters, columns);

            if (columns.Length == 1 && IsUnsetIdentity(columns[0], key.Values[0]))
            {
                inserts.Add(row);
                continue;
            }

            keyed.Add((row, key));
        }

        var seen = new HashSet<StorageKey>();

        foreach (var (_, key) in keyed)
            if (!seen.Add(key))
                throw new QuickSqlException(
                    $"Storageable 的清单里有重复键 [{FormatKey(key)}]：同步语义下同一颗键说两次话没法判增改，"
                    + "请先把清单去重再对账。");

        // 第二步：存在性对账——按参数预算分块，每块一句 OR 平铺的 SELECT，只投键列（见接口抬头）。
        var found = new HashSet<StorageKey>();
        var size = ChunkSize(columns.Length, _orm);
        var keyOnly = KeyProjector(columns);

        for (var start = 0; start < keyed.Count; start += size)
        {
            var chunk = new List<StorageKey>(Math.Min(size, keyed.Count - start));

            for (var i = start; i < Math.Min(start + size, keyed.Count); i++)
                chunk.Add(keyed[i].Key);

            var dbKeys = await _orm.Select<T>().Where(MatchesAny(columns, chunk)).Select(keyOnly)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var dbKey in dbKeys)
                found.Add(KeyOf(dbKey, getters, columns));
        }

        List<T> updates = [];

        foreach (var (row, key) in keyed)
            if (found.Contains(key))
                updates.Add(row);
            else
                inserts.Add(row);

        // 第三步：删除桶（opt-in）——整表读回来，键不在清单里的就是要摘的。
        List<T> deletes = [];

        if (_delete)
        {
            if (_rows.Count == 0)
                throw new QuickSqlException(
                    "SplitDelete 开着而清单为空：这句组合的语义是「清空整表」，同步链不为这一句代劳 —— "
                    + "要清表请直接 orm.Delete<T>().Where(...)，要同步就给数据。");

            var scan = _deleteScope is { } scope ? _orm.Select<T>().Where(scope) : _orm.Select<T>();
            var all = await scan.ToListAsync(ct).ConfigureAwait(false);

            foreach (var dbRow in all)
                if (!seen.Contains(KeyOf(dbRow, getters, columns)))
                    deletes.Add(dbRow);
        }

        return new StorageResult<T>(inserts, updates, deletes, _orm, columns, getters, KeysArePrimaryKey(columns, contract));
    }

    [RequiresUnreferencedCode("按键位属性名建表达式树（PropertyOrField 按名反射）。")]
    [RequiresDynamicCode("谓词翻译的退路是编译表达式树。")]
    internal static Expression<Func<T, bool>> MatchesAny(ColumnContract[] columns, IReadOnlyList<StorageKey> keys)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression? body = null;

        foreach (var key in keys)
        {
            Expression? tuple = null;

            for (var i = 0; i < columns.Length; i++)
            {
                var member = Expression.PropertyOrField(parameter, columns[i].MemberName);
                var equal = Expression.Equal(member, TypedConstant(key.Values[i], columns[i].ClrType, member.Type));
                tuple = tuple is null ? equal : Expression.AndAlso(tuple, equal);
            }

            body = body is null ? tuple! : Expression.OrElse(body, tuple!);
        }

        return Expression.Lambda<Func<T, bool>>(body!, parameter);
    }

    internal static StorageKey KeyOf(object row, Func<object, object>[] getters, ColumnContract[] columns)
    {
        var values = new object?[getters.Length];

        for (var i = 0; i < getters.Length; i++)
            values[i] = Canonical(getters[i](row), columns[i].ClrType);

        return new StorageKey(values);
    }

    /// <summary>对账那一句只要键列：x =&gt; new T { KeyA = x.KeyA, … }。落成投影翻译的成员初始化臂，
    /// 选列因此只端键位那几颗，收回来的那份 T 也只有键位有值——除了 KeyOf 没有第二处读它。
    /// 无参构造与逐列赋值是实体物化自己的形状（见 ReflectionEntitySchema），映射列又一律出自公共实例属性，
    /// 所以凡是读得动的实体都装配得出这一份，没有兜底分支可留。</summary>
    [RequiresUnreferencedCode("按键位属性名建表达式树（PropertyOrField 与 Bind）。")]
    [RequiresDynamicCode("投影委托由物化层编译，退路是编译表达式树。")]
    static Expression<Func<T, T>> KeyProjector(ColumnContract[] columns)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var seed = Expression.New(MemberAccessors.ParameterlessConstructor(typeof(T))
            ?? throw new QuickSqlException(
                $"实体 {typeof(T).Name} 没有无参构造器：对账的键投影装配不出实例（与实体物化同一颗判据）。"));

        var bindings = new MemberBinding[columns.Length];

        for (var i = 0; i < columns.Length; i++)
        {
            var member = Expression.PropertyOrField(parameter, columns[i].MemberName);

            bindings[i] = Expression.Bind(member.Member, member);
        }

        return Expression.Lambda<Func<T, T>>(Expression.MemberInit(seed, bindings), parameter);
    }

    /// <summary>点名键是不是主键（按属性名集合比）：修改桶执行据此走批量或逐行。</summary>
    internal static bool KeysArePrimaryKey(ColumnContract[] columns, EntityContract contract)
        => columns.Length == contract.PrimaryKey.Length
            && columns.Select(c => c.MemberName).SequenceEqual(contract.PrimaryKey.Select(k => k.MemberName));

    /// <summary>逐行更新那一档的 Set 列：成员包一层 CLR 装箱 Convert，落在 Set〈object〉 一档——
    /// 列型是运行期的事，泛型只有这一颗编译期的，不碰 MakeGenericMethod。</summary>
    [RequiresUnreferencedCode("按名取成员建表达式树（PropertyOrField）。")]
    internal static Expression<Func<T, object>> MemberLambda(string memberName)
    {
        var parameter = Expression.Parameter(typeof(T), "x");

        return Expression.Lambda<Func<T, object>>(
            Expression.Convert(Expression.PropertyOrField(parameter, memberName), typeof(object)), parameter);
    }

    internal static int ChunkSize(int columns, QuickSqlOrm orm)
    {
        var budget = orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;

        return Math.Max(1, (budget - 16) / columns);
    }

    ColumnContract[] ResolveKeys(EntityContract contract)
    {
        if (_memberNames is null)
        {
            if (contract.PrimaryKey.Length == 0)
                throw new QuickSqlException(
                    $"实体 {typeof(T).Name} 没有主键，Storageable 对不出键：给实体配主键，或用 WhereColumns 点名。");

            return contract.PrimaryKey;
        }

        return [.. _memberNames.Select(name => contract.ByMember.TryGetValue(name, out var column)
            ? column
            : throw new QuickSqlException(
                $"实体 {typeof(T).Name} 上没有 {name} 这一列：WhereColumns 点名要与实体契约对得上。"))];
    }

    [RequiresUnreferencedCode("键读口按反射编译。")]
    [RequiresDynamicCode("键读口编译成委托。")]
    static Func<object, object>[] BuildGetters(ColumnContract[] columns)
        => [.. columns.Select(column => MemberAccessors.Getter(
                typeof(T), column.MemberName,
                $"实体 {typeof(T).Name} 上没有键属性 {column.MemberName}",
                $"实体 {typeof(T).Name} 的键属性 {column.MemberName} 没有公共 getter"))];

    /// <summary>自增键"没填"的判据：null，或该值类型自己的默认值（int 的 0、Guid 的空）。非自增列不认这一条
    /// ——点名的键上 0 是一颗真实的键值，不是"还没填"。</summary>
    static bool IsUnsetIdentity(ColumnContract column, object? value)
    {
        if (!column.IsIdentity)
            return false;

        if (value is null)
            return true;

        var underlying = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
            || underlying == typeof(byte) || underlying.IsEnum)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0L;

        if (underlying == typeof(Guid))
            return value is Guid guid && guid == Guid.Empty;

        // 其余型的自增不是这一家的形状：不认"没填"，按真实键值对账。
        return false;
    }

    /// <summary>两侧同一把尺子：清单侧与库侧的键值都过这一颗（SQLite 把 int 交成长的那一族，
    /// 不过这一颗对账就是全错）。整型统一成 long、枚举统一成底层整数、字符串交回来的 Guid 过一遍 Parse。
    /// 统一成 long 的代价：ulong 键值高过 long.MaxValue 那一颗在这一步抛溢出——抛比错对账诚实。</summary>
    static object? Canonical(object? value, Type columnType)
    {
        if (value is null or DBNull)
            return null;

        var underlying = Nullable.GetUnderlyingType(columnType) ?? columnType;

        if (underlying.IsEnum)
            return Convert.ChangeType(value, Enum.GetUnderlyingType(underlying), CultureInfo.InvariantCulture);

        switch (underlying)
        {
            case var t when t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                          || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte):
                return Convert.ChangeType(value, typeof(long), CultureInfo.InvariantCulture);

            case var t when t == typeof(Guid) && value is string text:
                return Guid.Parse(text);

            case var t when t == typeof(decimal) && value is not decimal:
                return Convert.ChangeType(value, typeof(decimal), CultureInfo.InvariantCulture);

            default:
                return value;
        }
    }

    /// <summary>谓词那一侧的常量：按列的存储型造（对账用规范化值，发出去的句子要用列认的那个型）。
    /// 可空成员的常量按成员型补一颗 null/装箱，交给翻译器走它自己的提升比较。</summary>
    static ConstantExpression TypedConstant(object? value, Type columnType, Type memberType)
    {
        var underlying = Nullable.GetUnderlyingType(columnType) ?? columnType;

        if (value is null or DBNull)
            return Expression.Constant(null, memberType);

        object typed;

        if (underlying.IsEnum)
            typed = Enum.ToObject(underlying, Convert.ChangeType(value, Enum.GetUnderlyingType(underlying), CultureInfo.InvariantCulture)!);
        else if (value.GetType() == underlying)
            typed = value;
        else
            typed = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);

        return memberType != underlying
            ? Expression.Constant(typed, memberType)
            : Expression.Constant(typed, underlying);
    }

    static string FormatKey(StorageKey key)
        => string.Join(", ", key.Values.Select(v => v?.ToString() ?? "NULL"));

    static Expression Unwrap(Expression body)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } strip)
            body = strip.Operand;

        return body;
    }

    static string MemberNameOf(Expression argument)
        => Unwrap(argument) is MemberExpression member
            ? member.Member.Name
            : throw new QuickSqlException(
                "WhereColumns 的匿名形状里每一格都得是一颗属性（it.KeyA），不接受别的表达式。");
}

/// <summary>一颗键的规范化元组：单列键就是一格的数组。等值按逐格 Equals（两侧都过了 Canonical，
/// 类型统一之后才可比），散列逐格累计。</summary>
sealed class StorageKey(object?[] values)
{
    public object?[] Values { get; } = values;

    public bool Equals(StorageKey? other)
        => other is not null && Values.AsSpan().SequenceEqual(other.Values);

    public override bool Equals(object? obj) => obj is StorageKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var value in Values)
            hash.Add(value);

        return hash.ToHashCode();
    }
}
