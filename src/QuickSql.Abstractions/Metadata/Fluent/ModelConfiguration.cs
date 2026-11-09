namespace QuickSql.Metadata.Fluent;

/// <summary>同层冲突的记录器：同一个事实被写两次就在配置期抛，不等启动解析
/// ——启动期要求断言的是异常，不是"取后写的那条"。实体级与成员级共用这一条尺子。</summary>
sealed class FactBag(string owner)
{
    HashSet<string>? _facts;

    public void Record(string fact, object? value)
    {
        _facts ??= [];

        if (!_facts.Add(fact))
            throw new MetadataConflictException(owner, [$"Fluent 规则 {fact} 已被设置，本次又设置为 {value}", "同层重复配置按启动期失败处理"]);
    }
}

/// <summary>Fluent 配置（优先级最高的来源层）。只作为解析期的输入，解析完就冻结进 EntityContract，
/// 三个消费点（DDL / 绑定 / 校验）不再回读这里。</summary>
public sealed class FluentMember
{
    readonly FactBag _facts;

    public FluentMember(string memberName)
    {
        MemberName = memberName;
        _facts = new FactBag(memberName);
    }

    public string MemberName { get; }

    public string? ColumnName { get; private set; }

    public int? MaxLength { get; private set; }

    public int? Precision { get; private set; }

    public int? Scale { get; private set; }

    public bool? Required { get; private set; }

    public bool? IsKey { get; private set; }

    public bool? IsIdentity { get; private set; }

    public bool? IsIgnored { get; private set; }

    /// <summary>并发列（乐观锁，只比较不书写）。与 ConcurrencyToken 特性同一格，优先级链上 Fluent 在上。</summary>
    public bool? IsConcurrencyToken { get; private set; }

    /// <summary>应用给值那一族（ConcurrencyToken(AppSetsValue = true) 的 Fluent 等价物）：
    /// 新值由应用在保存前生成，库拿快照比 WHERE、把现值写进 SET。要先用 PropertyBuilder.IsConcurrencyToken
    /// 或特性把这一列声明成并发列，这一格才被采信。</summary>
    public bool? AppSetsValue { get; private set; }

    public string? StoreTypeName { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>列默认值（与 PropertyBuilder.HasDefaultValue 同一格的读取侧）。
    /// 值域门禁不在这一格跑——它在 EntityContractFactory 收尾那一份（G1 与 G2 共用），这里只存。</summary>
    public object? DefaultValue { get; private set; }

    internal bool IsEmpty => ColumnName is null && MaxLength is null && Precision is null && Scale is null
                             && Required is null && IsKey is null && IsIdentity is null && IsIgnored is null
                             && IsConcurrencyToken is null && AppSetsValue is null && StoreTypeName is null && Comment is null
                             && DefaultValue is null;

    public sealed class PropertyBuilder(FluentMember member)
    {
        public PropertyBuilder HasColumnName(string name)
        {
            member._facts.Record(nameof(HasColumnName), name);
            member.ColumnName = name;
            return this;
        }

        public PropertyBuilder HasMaxLength(int length)
        {
            member._facts.Record(nameof(HasMaxLength), length);
            member.MaxLength = length;
            return this;
        }

        public PropertyBuilder HasPrecision(int precision, int scale)
        {
            member._facts.Record(nameof(HasPrecision), precision + "," + scale);
            member.Precision = precision;
            member.Scale = scale;
            return this;
        }

        public PropertyBuilder HasDbType(string storeTypeName)
        {
            member._facts.Record(nameof(HasDbType), storeTypeName);
            member.StoreTypeName = storeTypeName;
            return this;
        }

        public PropertyBuilder HasComment(string comment)
        {
            member._facts.Record(nameof(HasComment), comment);
            member.Comment = comment;
            return this;
        }

        /// <summary>列默认值（CodeFirst 建表与补列时发 DEFAULT）。只收常量字面量档
        /// （string、bool、整型、浮点、decimal）且要与列型精确同型（可空列剥掉 Nullable 比对）；
        /// DateTime（把建表时刻冻进 DDL）、byte[]（无字面量形）、枚举（字面量形各家不同）、
        /// 自增/主键列都在 EntityContractFactory 收尾那一格被拒——那里两侧共用一份门禁。
        /// 不支持配 null：可空的列用不着默认，非空的列配 null 是自相矛盾。</summary>
        public PropertyBuilder HasDefaultValue(object value)
        {
            member._facts.Record(nameof(HasDefaultValue), value);
            member.DefaultValue = value;
            return this;
        }

        public PropertyBuilder IsRequired(bool required = true)
        {
            member._facts.Record(nameof(IsRequired), required);
            member.Required = required;
            return this;
        }

        public PropertyBuilder IsKey(bool isKey = true)
        {
            member._facts.Record(nameof(IsKey), isKey);
            member.IsKey = isKey;
            return this;
        }

        public PropertyBuilder IsIdentity(bool isIdentity = true)
        {
            member._facts.Record(nameof(IsIdentity), isIdentity);
            member.IsIdentity = isIdentity;
            return this;
        }

        public PropertyBuilder IsIgnore()
        {
            member._facts.Record(nameof(IsIgnore), true);
            member.IsIgnored = true;
            return this;
        }

        /// <summary>声明成并发列（ConcurrencyToken 特性的 Fluent 等价物，优先级更高）。</summary>
        public PropertyBuilder IsConcurrencyToken(bool isConcurrencyToken = true)
        {
            member._facts.Record(nameof(IsConcurrencyToken), isConcurrencyToken);
            member.IsConcurrencyToken = isConcurrencyToken;
            return this;
        }

        /// <summary>把并发列声明成应用给值那一族（ConcurrencyToken(AppSetsValue = true) 的等价物）：
        /// 新值由应用生成，列进 SET 与 INSERT 列清单，WHERE 仍比快照。</summary>
        public PropertyBuilder AppSetsValue(bool appSetsValue = true)
        {
            member._facts.Record(nameof(AppSetsValue), appSetsValue);
            member.AppSetsValue = appSetsValue;
            return this;
        }
    }
}

public sealed class FluentEntity
{
    readonly Dictionary<string, FluentMember> _members = [];
    readonly FactBag _facts;

    internal FluentEntity(string owner) => _facts = new FactBag(owner);

    public string? TableName { get; private set; }

    public string? Schema { get; private set; }

    public string? Comment { get; private set; }

    public IReadOnlyDictionary<string, FluentMember> Members => _members;

    /// <summary>配置期只有 CLR 成员名；列名要等解析链跑完才有（同一层的列名覆写会影响它）。</summary>
    public IReadOnlyList<FluentIndex> Indexes => _indexes;

    readonly List<FluentIndex> _indexes = [];

    internal void SetTable(string name, string? schema)
    {
        _facts.Record(nameof(SetTable), name);
        TableName = name;
        Schema = schema;
    }

    internal void SetComment(string comment)
    {
        _facts.Record(nameof(SetComment), comment);
        Comment = comment;
    }

    internal FluentMember Member(string name)
    {
        if (!_members.TryGetValue(name, out var member))
            _members[name] = member = new FluentMember(name);

        return member;
    }

    internal void AddIndex(FluentIndex index)
    {
        _facts.Record(nameof(AddIndex) + ":" + index.Name, string.Join(",", index.MemberNames));
        _indexes.Add(index);
    }
}

/// <summary>Fluent 侧的索引声明：成员名 + 唯一性。Name 可空，留空由约定生成。</summary>
public sealed record FluentIndex(string? Name, IReadOnlyList<string> MemberNames, bool Unique, bool Descending = false);

/// <summary>一个 OrmOptions 里的全部 Fluent 配置。</summary>
public sealed class ModelConfiguration
{
    readonly Dictionary<Type, FluentEntity> _entities = [];

    public EntityBuilder<T> Entity<T>() where T : class
    {
        if (!_entities.TryGetValue(typeof(T), out var entity))
            _entities[typeof(T)] = entity = new FluentEntity(typeof(T).Name);

        return new EntityBuilder<T>(entity);
    }

    public FluentEntity? Find(Type clrType) => _entities.GetValueOrDefault(clrType);

    /// <summary>配置过的实体清单（预登记口）：
    /// DbContext.OnModelCreating 用它把点过名的实体预登记进会话那份注册表——摸过之前
    /// SchemaSyncer 看不见它们，而"配了模型却同步不到"是比漏配更难查的假象。
    /// 顺序按首次点名的次序（字典枚举序），只读。</summary>
    public IEnumerable<Type> KnownEntityTypes => _entities.Keys;
}

/// <summary>ModelConfiguration 的 EF 同形薄壳：DbContext.OnModelCreating 的形参类型。
/// 它自己不存任何事实——配置落在 EntityBuilder 那一整套既有机制上（同层冲突仍由 FactBag 启动期抛），
/// 这一格存在的理由只有"挂钩的签名要像 EF"那一半。
/// 覆写即整份模型：派生类覆写 OnModelCreating 之后，这一只上下文走的是
/// 这里配出的那一份，组装根上的 orm 级全局默认（QuickSqlOrmOptions.Model）不再参与——
/// 与 EF 的同一条语义；要全局默认就别覆写。</summary>
public sealed class ModelBuilder
{
    readonly ModelConfiguration _configuration = new();

    /// <summary>配置一只实体（EF 同形）。同一实体多次 Entity() 是同一只 builder 的延续，
    /// 不是覆盖——与 ModelConfiguration.Entity 同一口径。</summary>
    public EntityBuilder<T> Entity<T>(Action<EntityBuilder<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = _configuration.Entity<T>();

        configure(builder);

        return builder;
    }

    /// <summary>取回一只实体的 builder 而不立即配置（要连着多层写时用）。</summary>
    public EntityBuilder<T> Entity<T>() where T : class => _configuration.Entity<T>();

    internal ModelConfiguration Configuration => _configuration;
}

public sealed class EntityBuilder<T> where T : class
{
    readonly FluentEntity _entity;

    internal EntityBuilder(FluentEntity entity) => _entity = entity;

    public EntityBuilder<T> ToTable(string name, string? schema = null)
    {
        _entity.SetTable(name, schema);
        return this;
    }

    public EntityBuilder<T> HasComment(string comment)
    {
        _entity.SetComment(comment);
        return this;
    }

    public EntityBuilder<T> HasIndex(params string[] memberNames)
        => HasIndex(null, unique: false, memberNames);

    public EntityBuilder<T> HasUniqueIndex(params string[] memberNames)
        => HasIndex(null, unique: true, memberNames);

    public EntityBuilder<T> HasIndex(string? name, bool unique, params string[] memberNames)
    {
        if (memberNames.Length == 0)
            throw new QuickSqlException($"实体 {typeof(T).Name} 的索引 {name} 至少要有一个成员");

        _entity.AddIndex(new FluentIndex(name, memberNames, unique));
        return this;
    }

    public FluentMember.PropertyBuilder Property<TProp>(System.Linq.Expressions.Expression<Func<T, TProp>> accessor)
        => new(_entity.Member(MemberName(accessor)));

    static string MemberName(System.Linq.Expressions.LambdaExpression accessor)
        => accessor.Body is System.Linq.Expressions.MemberExpression m
            ? m.Member.Name
            : throw new QuickSqlException($"Fluent 配置只接受属性访问表达式，收到：{accessor.Body}");
}
