using System.Globalization;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Write;

/// <summary>插入链（L3 多值语句 + 客户端分块）。分两段跑：取值段（列合成 → AuditValue → 契约校验）一次，
/// 发射段（分块 → 方言 → CudBuilding → ParameterBinding）按出口形态各一次。分开是因为三个出口（不回填 /
/// 只回填自增键 / 连并发列一起回填）产出的文本不同，而取值段的副作用（钩子写实体、校验拒绝）必须只发生一次
/// ——否则 CreatedAt 会被填两个不同的时间。值级钩子按构建次数触发而不是按执行次数（同一条语句先 ToSql() 再
/// 执行会构建两次，ParameterBinding 因此过两遍，钩子必须幂等）；AuditValue 更严——它只可能跑一次。</summary>
sealed class InsertEngine<T> : IInsert<T> where T : class
{
    readonly QuickSqlOrm _orm;
    readonly T[] _rows;

    Preparation? _preparation;
    InsertPlan? _plain;
    InsertPlan? _withKeys;
    InsertPlan? _withGenerated;

    // 批量阶梯的三格请求。null 不是"没决定"，是"这条出口自己的默认"：ExecuteAffrowsAsync 默认文本层，
    // ExecuteBulkAsync 默认 Auto（判据落在下面的 Decide / ExecuteAffrowsAsync）。
    BulkMethod? _requested;
    FallbackPolicy _fallback = FallbackPolicy.DegradeAndLog;
    int? _rowsPerRoundTrip;

    public InsertEngine(QuickSqlOrm orm, T[] rows)
    {
        _orm = orm;
        _rows = rows;
    }

    // ── 离线出口（不连库） ────────────────────────────────────────

    public InsertPlan Explain() => Plan(Backfill.None);

    public InsertPlan ExplainKeys() => Plan(Backfill.Keys);

    /// <summary>「库里给的那些列」= 自增列 ∪ 并发列（跟踪器的回填集）。并发列的值不进列清单、只进 WHERE，
    /// 插完不读回来，手里的值就永远是空的，下一次保存必假冲突。</summary>
    public InsertPlan ExplainGenerated() => Plan(Backfill.Generated);

    public string ToSql() => Plan(Backfill.None).ToSql();

    public string ToSql(out IReadOnlyList<AdoParameter> parameters)
    {
        var plan = Plan(Backfill.None);

        if (!plan.IsSingleStatement)
            throw new QuickSqlException($"分块后没有单一参数表（{plan.Reason}）。取 Explain().Chunks[i].Sql.Parameters。");

        parameters = plan.Chunks[0].Sql.Parameters;

        return plan.Chunks[0].Sql.Text;
    }

    public string ToLogSql() => Plan(Backfill.None).ToLogSql();

    // ── 批量阶梯 ────────────────────────────────────────────

    public IInsert<T> WithBulk(BulkMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog, int? rowsPerRoundTrip = null)
    {
        if (rowsPerRoundTrip is { } requested && requested < 1)
            throw new QuickSqlException($"rowsPerRoundTrip={requested}：每块至少 1 行。要到阶梯最底那一档请传 {BulkMethod.SingleRow}，不是把这块压到 0。");

        _requested = method;
        _fallback = fallback;
        _rowsPerRoundTrip = rowsPerRoundTrip;

        // 每块行数是发射段的输入，改过它，缓存里那三份文本就过期了。取值段那份不动——
        // AuditValue 只许跑一次（与上面抬头的两段之分同一条理由）。
        _plain = _withKeys = _withGenerated = null;

        return this;
    }

    [RequiresUnreferencedCode("判「这一家有没有装载通道」靠一次按类型名的反射查找（IBulkStrategy.ResolveCopyChannel 的抬头）：被裁剪的驱动会让这一颗报出与完整运行时不同的层。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public BulkReport ExplainBulk(BulkMethod? requested = null)
        => Decide(Prepare(), requested ?? _requested ?? BulkMethod.Auto).Report;

    // ── 执行出口 ──────────────────────────────────────────────────────

    /// <summary>文本层出口：认得的请求是 MultiValue 与 SingleRow 那一档（连同 WithBulk 里的每块行数），
    /// 指向装载通道的请求在这里当场拒、不是默默当没看见——这一颗返回的只是一颗行数，装不下
    /// "走了哪一层、几次往返、为什么"，而装载通道不产文本，两个改写钩子会集体失效。
    /// 这条出口因此不欠修剪税：不可能走到装载通道，签名上没有 RequiresUnreferencedCode。</summary>
    public async Task<int> ExecuteAffrowsAsync(CancellationToken ct = default)
    {
        if (_requested is BulkMethod.Auto or BulkMethod.CopyChannel)
            throw new QuickSqlException(
                $"ExecuteAffrowsAsync 走不了 {_requested}：那一档要的是「走了哪一层、几次往返、为什么」，而它的返回值只是一颗行数。" +
                "装载通道请用 ExecuteBulkAsync；只压每块行数或按请求逐条请用 WithBulk(MultiValue/SingleRow, rowsPerRoundTrip: …)。");

        var plan = Plan(Backfill.None);
        var tag = Tag();
        var affrows = 0;

        foreach (var chunk in plan.Chunks)
            affrows += await _orm.Ado.NonQueryAsync(chunk.Sql, tag, ct).ConfigureAwait(false);

        return affrows;
    }

    [RequiresUnreferencedCode("转调装载通道：驱动自家的批量类不在 DbProviderFactory 的抽象面上，只能按类型名找（接口那一句标注的镜像，IL2046 要求两边一致）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public async Task<BulkReport> ExecuteBulkAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();
        var decision = Decide(preparation, _requested ?? BulkMethod.Auto);
        var report = decision.Report;
        var tag = Tag();

        // 留痕在动手之前：降级之后那句写还可能失败，而"它退到哪一层"正是排查那一半要用的信息。
        // DegradeSilently 走不到这里（纯优化路径不必声张）。
        if (report.Degraded && _fallback == FallbackPolicy.DegradeAndLog && _orm.Aop.BulkDegraded is { } degraded)
            _orm.Aop.Observe(() => degraded(new BulkDegradedEvent(report.Requested, report.Method, report.RowCount, report.Reason)));

        int written;

        if (decision.Channel is { } channel)
        {
            // 分表时一片一次递交（装载通道的目标表一次一个，混不了两片的行），
            // 所以"几次往返"是逐片取整再相加。
            var batches = CopyRequests(preparation);
            var width = 0;
            var trips = 0;

            written = 0;

            foreach (var batch in batches)
            {
                // rowsPerRoundTrip 在装载通道这一档说的是"一次递交几行"，与文本层同一个旋钮、
                // 同一条只压不抬：这里没有参数预算那道界，上限只有这一片的行数本身。
                var slice = _rowsPerRoundTrip is { } cap && cap < batch.Rows.Count ? cap : batch.Rows.Count;

                width = Math.Max(width, slice);
                trips += (batch.Rows.Count + slice - 1) / slice;

                for (var first = 0; first < batch.Rows.Count; first += slice)
                    written += await _orm.Ado
                        .CopyAsync(channel, Slice(batch, first, slice), tag, ct)
                        .ConfigureAwait(false);
            }

            // 通道报回的数与递交的数不等 = 部分写入。少报的数比少一列危险（绿着交出看着合法的结果），
            // 所以这里抛而不是把 report 改小。
            if (written != report.RowCount)
                throw new QuickSqlException(
                    $"{_orm.Provider.Kind} 的装载通道报回 {written}/{report.RowCount} 行：这一家的批量入口给不出「进了几行」，" +
                    "不能假设这一批完整落库（§12.1 那句「失败不能定位到行」在这一格里就是整批不可信）。");

            return report with { ChunkSize = width, RoundTrips = trips };
        }

        // 文本层：把决策里的估算换成发射段的实数（块数与每块行数都由 Build 算，两处不许各算一遍）。
        var plan = Plan(Backfill.None);

        report = report with { ChunkSize = plan.RowsPerStatement, RoundTrips = plan.Chunks.Count };

        written = 0;

        foreach (var chunk in plan.Chunks)
            written += await _orm.Ado.NonQueryAsync(chunk.Sql, tag, ct).ConfigureAwait(false);

        return report;
    }

    /// <summary>批量阶梯今天能落的三档，由这一颗纯函数选：不建文本、不碰连接、不发钩子
    /// （ExplainBulk 与 ExecuteBulkAsync 共用它，"离线看到的层"与"执行走的那一层"没有第二套判据可漂移）。
    /// 装载通道那一条分支必须在建文本之前定下来（否则 CudBuilding 为一句丢掉的分块语句白跑一遍）；
    /// 文本层那两档的往返次数由 Build 算，BulkReport 先带公式估算、执行时由实数覆写。</summary>
    [RequiresUnreferencedCode("调用 IBulkStrategy.ResolveCopyChannel() 那一次按类型名的反射查找：税从这里往上冒到两条公开出口（ExplainBulk / ExecuteBulkAsync）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    BulkDecision Decide(Preparation preparation, BulkMethod requested)
    {
        RequireColumns(preparation);

        var provider = _orm.Provider;
        var rows = _rows.Length;

        if (rows == 0)
            return new BulkDecision(new BulkReport(requested, BulkMethod.MultiValue, 0, 0, 0, false,
                "0 行：一层都不必选。不建文本、不取连接，执行直接返回 0。"), null);

        if (requested is BulkMethod.Auto or BulkMethod.CopyChannel)
        {
            var channel = provider.Bulk?.ResolveCopyChannel();
            var refusal = Refusal(provider, channel, preparation.Columns, WriteConnectionString(preparation));

            if (refusal is null)
            {
                // 装载通道这一档没有参数预算那道界，所以 _rowsPerRoundTrip 的上限就是这批的行数；
                // 不压时一次递交（执行段用同一组数）。
                var width = _rowsPerRoundTrip is { } cap && cap < rows ? cap : rows;
                var trips = CopyTrips(preparation, width);

                return new BulkDecision(new BulkReport(requested, BulkMethod.CopyChannel, rows, trips, width, false,
                    $"{rows} 行走 {provider.Kind} 的装载通道（§12.1 的 L1）：不发 SQL 文本、不走参数，" +
                    (preparation.Groups is { Count: > 1 } shardGroups
                        ? $"分 {shardGroups.Count} 张片、共 {trips} 次递交（一片一次递交，跨片不合并）。"
                        : trips == 1 ? "一次递交。" : $"分 {trips} 次递交、每次 {width} 行（块宽是请求压下来的）。") +
                    "通道内部怎么分批是驱动的事，对上层是一次递交一块；改写类钩子（CudBuilding / ParameterBinding）在这一档不适用。"), channel);
            }

            // 请求过装载通道而拿不到：Never 不许退（那是唯一能防住静默逐条的手段）。
            // Auto 不算降级——它没有承诺过任何一层，取到阶梯上第一档满足条件的层就是它的定义，
            // 留痕位也不派发。
            if (requested == BulkMethod.CopyChannel)
            {
                if (_fallback == FallbackPolicy.Never)
                    throw new QuickSqlException($"请求了 {provider.Kind} 的装载通道而它不可用：{refusal}。FallbackPolicy.Never 不允许退回文本层。");

                return new BulkDecision(TextReport(preparation, requested, BulkMethod.MultiValue, degraded: true) with
                {
                    Reason = $"{refusal} → 退回 L3 多值语句。{TextReason(preparation, BulkMethod.MultiValue)}",
                }, null);
            }

            // 不降级不等于不解释：那句拒绝要进 Reason。现场缺的常常是一颗连接串参数
            // （MySQL 血统的 AllowLoadLocalInfile 就是第一颗），Reason 里点名拒的是哪一层、为什么。
            // 措辞用"取第一档满足条件的层"，不用"退回"——那个词只属于承诺过而没拿到的档。
            return new BulkDecision(TextReport(preparation, requested, BulkMethod.MultiValue, degraded: false) with
            {
                Reason = $"{refusal} → Auto 取阶梯上第一档满足条件的层。{TextReason(preparation, BulkMethod.MultiValue)}",
            }, null);
        }

        return new BulkDecision(TextReport(preparation, requested, requested, degraded: false), null);
    }

    /// <summary>这一家能不能整批进装载通道的判据，交回的是话而不是布尔：拒绝的理由要进 BulkReport.Reason
    /// 给现场读，也要进 Never 那句异常，两处同一份文本。null = 可以进。连接串闸门交回的话由通道自己编
    /// （哪颗参数放行哪一家的装载是驱动侧的事实，内核不认识任何一家的连接串键名）；这一句读的是在册那条
    /// 主库串——判宽了装载当场抛（驱动的话，可行动），判窄了退 L3（慢而不错）。</summary>
    static string? Refusal(IDbProvider provider, IBulkCopyChannel? channel, ColumnContract[] columns, string connectionString)
    {
        if (channel is null)
            return $"{provider.Kind} 没有可用的装载通道（IBulkStrategy.ResolveCopyChannel() 交回 null：这一家没接，或这一支驱动里没有装载类）";

        // 一列不合格就整批退回，不是把那一列从通道里省掉：省一列等于那一列的值不进库，那是丢写。
        foreach (var column in columns)
            if (!channel.SupportsColumn(column))
                return $"{column.ColumnName}（CLR {column.ClrType.Name}）不在 {provider.Kind} 装载通道收下的类型里";

        return channel.UnavailableBecauseOf(connectionString);
    }

    /// <summary>写侧那一条串：装载通道落在哪座集群，闸门就问哪一座的主库串
    /// （写永远是主库那一格，判据在 AdoExecutor.RentAsync 一处，不另立一套）。</summary>
    string WriteConnectionString(Preparation preparation)
        => _orm.Sources.PrimaryConnectionString(DataSourceRouting.ClusterOf(preparation.Contract.Table));

    BulkReport TextReport(Preparation preparation, BulkMethod requested, BulkMethod method, bool degraded)
        => new(requested, method, _rows.Length, Trips(preparation, method), ChunkRows(preparation, method), degraded, TextReason(preparation, method));

    string TextReason(Preparation preparation, BulkMethod method)
    {
        var provider = _orm.Provider;
        var budget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var rows = _rows.Length;

        if (method == BulkMethod.SingleRow)
            return $"{rows} 行按请求逐条（L5）：{rows} 次往返。语义与批量一致（§12.4 的等价性），代价是往返。";

        var statementRows = ChunkRows(preparation, method);

        return $"{rows} 行 × {preparation.Columns.Length} 列，参数预算 {budget} → 每块 {statementRows} 行（{Trips(preparation, method)} 块），L3 多值语句。" +
               (statementRows < StatementEmitter.RowsPerStatement(budget, preparation.Columns.Length)
                   ? " 每块行数是请求压下来的（WithBulk 的 rowsPerRoundTrip），不是预算算出来的。"
                   : string.Empty);
    }

    /// <summary>每块几行——Build 里那一句的同一个公式，两处不许各写一遍算式：都由这一个出口取。
    /// SingleRow 不是第二套逐条实现，就是把块宽定成 1：一行一块 = 一次改写 = 一次往返，分岔点在 Build 之下。</summary>
    int ChunkRows(Preparation preparation, BulkMethod method)
    {
        if (method == BulkMethod.SingleRow)
            return 1;

        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;

        return Math.Min(StatementEmitter.RowsPerStatement(budget, preparation.Columns.Length), _rowsPerRoundTrip ?? int.MaxValue);
    }

    /// <summary>往返次数。分表时逐片取整再相加：三片各 7 行、块宽 10 是 3 次而不是一次——
    /// 一块的 VALUES 只进得了一张表，跨片合并那"半块"没有能落的目标。</summary>
    int Trips(Preparation preparation, BulkMethod method)
    {
        var width = ChunkRows(preparation, method);

        if (preparation.Groups is not { } groups)
            return (_rows.Length + width - 1) / width;

        var trips = 0;

        foreach (var (_, rows) in groups)
            trips += (rows.Length + width - 1) / width;

        return trips;
    }

    /// <summary>装载通道的递交次数：不分表就是 ceil(总行数 / 块宽)；分表时逐片取整再相加（一次递交只有一个目标表）。
    /// Decide 的估算与 ExecuteBulkAsync 的实数共用这一颗，离线看到的层与执行走的那一层没有第二套判据。</summary>
    int CopyTrips(Preparation preparation, int width)
    {
        if (preparation.Groups is not { } groups)
            return (_rows.Length + width - 1) / width;

        var trips = 0;

        foreach (var (_, rows) in groups)
            trips += (rows.Length + width - 1) / width;

        return trips;
    }

    /// <summary>递给装载通道的那一批，分表时一片一份（一次递交只有一个目标表，混不了两片的行）；
    /// 非分表实体只产出一颗。</summary>
    List<BulkCopyRequest> CopyRequests(Preparation preparation)
    {
        if (preparation.Groups is not { } groups)
            return [CopyRequestFor(preparation, preparation.Contract.Table, null)];

        var requests = new List<BulkCopyRequest>(groups.Count);

        foreach (var group in groups)
            requests.Add(CopyRequestFor(preparation, group.Table, group.Rows));

        return requests;
    }

    /// <summary>CopyRequests 的产物：列清单照文本层（自增列与并发列都不进，理由与 Prepare 那两条 continue
    /// 同一条），每格过 IValueBinder 那一扇门。目标表名由方言 EmitTableName 产，COPY 落的那一张与 INSERT 写的必然是同一张。</summary>
    BulkCopyRequest CopyRequestFor(Preparation preparation, TableInfo table, int[]? ordinals)
    {
        var binder = _orm.Provider.Values;
        var columns = preparation.Columns;
        var count = ordinals?.Length ?? preparation.Cells.Length;
        var payload = new object?[count][];

        // 名字按列物化一次：装载通道不接 DbParameter，但类型归一长在这颗门上，
        // 另起一条"不带名字的转换"就是多一份要维护的 Normalize。逐格插值在宽表上是上百万次字符串分配。
        var names = new string[columns.Length];

        for (var column = 0; column < names.Length; column++)
            names[column] = $"c{column}";

        for (var row = 0; row < payload.Length; row++)
        {
            var source = preparation.Cells[ordinals is null ? row : ordinals[row]];
            var cells = new object?[columns.Length];

            for (var column = 0; column < columns.Length; column++)
                cells[column] = binder.CreateParameter(names[column], source[column], columns[column]).Value;

            payload[row] = cells;
        }

        return new BulkCopyRequest(DestinationTable(preparation, table), columns, payload);
    }

    /// <summary>把已算好的那一批切出第 first 行起的 width 行。切的是行数组，不重走取值与归一：
    /// 每片只算一次，格子值与列清单在几次递交之间是同一份——"行序就是入参顺序"靠这一点才成立。</summary>
    static BulkCopyRequest Slice(BulkCopyRequest batch, int first, int width)
    {
        var count = Math.Min(width, batch.Rows.Count - first);
        var rows = new object?[count][];

        for (var row = 0; row < count; row++)
            rows[row] = batch.Rows[first + row];

        return batch with { Rows = rows };
    }

    string DestinationTable(Preparation preparation, TableInfo table)
    {
        var dialect = _orm.Provider.Dialect;
        var writer = new SqlWriter(new StringBuilder(table.Name.Length + 8), [], dialect.ParameterPrefix);

        dialect.EmitTableName(writer, table);

        return writer.ToSqlString();
    }

    /// <summary>0 列的插入只剩 DEFAULT VALUES 一种写法，本版本未接入。两个读者（文本层建语句之前、
    /// 装载通道选层之前）都要问——"零列的 COPY"恰好是能绿着把 0 个字段写进 N 行的形状，不能比文本层更晚红。</summary>
    static void RequireColumns(Preparation preparation)
    {
        if (preparation.Columns.Length == 0)
            throw new QuickSqlException($"{preparation.Table} 的每一列都不进列清单（自增列或并发列），插入只剩 DEFAULT VALUES 这一种写法，本版本未接入（§12.3）。");
    }

    /// <summary>决策 + 决策依据的那一个通道对象。Channel 只在 method == CopyChannel 时非 null：
    /// "能不能用"与"用它"是同一次判定的两个产物，执行时不再取第二次 ResolveCopyChannel（那一取会看到另一个实例）。</summary>
    sealed record BulkDecision(BulkReport Report, IBulkCopyChannel? Channel);


    public async Task<long> ExecuteIdentityAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();
        var keys = preparation.Keys;

        if (_rows.Length != 1)
            throw new QuickSqlException($"ExecuteIdentityAsync 只用于单行插入（当前 {_rows.Length} 行）：多行请用 ExecuteReturnKeysAsync 一次拿全。");

        if (keys.Length == 0)
            throw new QuickSqlException($"{preparation.Table} 没有自增列，回填不出标识值。主键是客户端生成的话直接用 ExecuteAffrowsAsync。");

        if (keys.Length > 1)
            throw new QuickSqlException($"{preparation.Table} 有 {keys.Length} 个自增列，一份 long 装不下：请用 ExecuteReturnKeysAsync 拿实体。");

        // 整数性在这里判，而不是读回来再判：读回来才发现类型不对时，行已经写进库了。
        var member = Nullable.GetUnderlyingType(keys[0].ClrType) ?? keys[0].ClrType;

        if (member != typeof(long) && member != typeof(int) && member != typeof(short) && member != typeof(byte))
            throw new QuickSqlException($"自增列 {keys[0].MemberName} 是 {member.Name}，不是整数：ExecuteIdentityAsync 的返回类型是 long，请改用 ExecuteReturnKeysAsync。");

        await ExecuteReturnKeysAsync(ct).ConfigureAwait(false);

        return Convert.ToInt64(preparation.Schema.GetValue(_rows[0], preparation.KeyOrdinals[0]), CultureInfo.InvariantCulture);
    }

    public Task<List<T>> ExecuteReturnKeysAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();

        if (preparation.Keys.Length == 0)
            throw new QuickSqlException($"{preparation.Table} 没有自增列：没有键可回填。要受影响行数请用 ExecuteAffrowsAsync。");

        return ReadBackAsync(preparation, preparation.KeyOrdinals, Plan(Backfill.Keys), ct);
    }

    /// <summary>把「值归库管的那几列」（自增列 ∪ 并发列）读回实体——ExecuteReturnKeysAsync 的超集，
    /// 没有并发列时两档产出逐字相同的文本、走同一次往返。跟踪器的那一档插入走这里。</summary>
    public Task ExecuteGeneratedAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();

        if (preparation.GeneratedOrdinals.Length == 0)
            throw new QuickSqlException($"{preparation.Table} 既没有自增列也没有并发列：没有任何一列的值归库管，回填那一档无事可做。");

        return ReadBackAsync(preparation, preparation.GeneratedOrdinals, Plan(Backfill.Generated), ct);
    }

    /// <summary>回填的那一段。ordinals 已经按 Returning 的列序排好，
    /// 回包的第 column 列直接落进实体的对应槽位，不需要再查一次名字。</summary>
    async Task<List<T>> ReadBackAsync(Preparation preparation, int[] ordinals, InsertPlan plan, CancellationToken ct)
    {
        var expected = ordinals.Length;
        var tag = Tag();

        foreach (var chunk in plan.Chunks)
        {
            var rowOrdinals = chunk.RowOrdinals;
            var first = chunk.FirstRow;
            var backfilled = 0;

            await _orm.Ado.WriteWithResultAsync(chunk.Sql, tag, (reader, index) =>
            {
                if (index == 0 && reader.FieldCount != expected)
                    throw new QuickSqlException($"{_orm.Provider.Kind} 的回填结果集有 {reader.FieldCount} 列，期望 {expected} 列：发射层与键策略对不上，不能假设回填可用。");

                // 回包第 index 行 ↔ 这一块的第 index 行（回填映射的全部内容）。
                // 分表时那一块行在入参数组里不连续，块自带那份下标表；不分表时是 null，
                // 走 FirstRow + 行号（不多一次数组分配）。
                var entity = _rows[rowOrdinals is null ? first + index : rowOrdinals[index]];

                for (var column = 0; column < expected; column++)
                    preparation.Schema.SetValue(entity, ordinals[column], reader.GetValue(column));

                backfilled++;
            }, ct).ConfigureAwait(false);

            // 上面那句列数检查只在"至少回来一行"时才走得到：有的驱动执行成功、行也真进去了，
            // 回包却是 FIELDS=0 ROWS=0，实体上的自增列留在 default。少一行比少一列更危险：
            // 少一列当场红，少几行是绿着把错值交出去，调用方拿到的是"看着合法"的 0 而不是异常。
            if (backfilled != chunk.RowCount)
                throw new QuickSqlException(
                    $"{_orm.Provider.Kind} 的插入执行成功却只回填了 {backfilled}/{chunk.RowCount} 行：" +
                    "这一家的回填形态没有把键发回客户端，不能假设回填可用。虚谷实测：语句收下、行进去了、结果集是空的。");
        }

        return [.. _rows];
    }

    // ── 构建 ──────────────────────────────────────────────────────────

    InsertPlan Plan(Backfill backfill)
    {
        // 三条分支各自缓存一份，而不是 ref 三元链套一个 switch：编译器不接受嵌套的 ref 条件运算符，
        // 换成按枚举下标取的数组等于给每条链多一次分配。
        if (backfill == Backfill.None)
            return _plain ??= Build(Prepare(), backfill);

        return backfill == Backfill.Keys
            ? _withKeys ??= Build(Prepare(), backfill)
            : _withGenerated ??= Build(Prepare(), backfill);
    }

    /// <summary>取值段：列合成 → 钩子填充 → 契约校验 → 行 × 列的实参值。只跑一次。</summary>
    Preparation Prepare()
    {
        if (_preparation is { } done)
            return done;

        var contract = _orm.Schemas.Contract<T>();
        var schema = _orm.Schemas.Schema<T>();
        var validator = _orm.Schemas.Validator<T>();
        var audit = _orm.Aop.AuditValue;

        var columns = new List<ColumnContract>(contract.Columns.Length);
        var ordinals = new List<int>(contract.Columns.Length);
        var keys = new List<ColumnContract>();
        var keyOrdinals = new List<int>();
        // 没有并发列的实体占绝大多数，这两张表懒出来：先建好再判断等于向每条批量插入收一笔"为别人设计的列"的钱。
        List<ColumnContract>? tokens = null;
        List<int>? tokenOrdinals = null;

        for (var ordinal = 0; ordinal < contract.Columns.Length; ordinal++)
        {
            var column = contract.Columns[ordinal];

            // 自增列不进列清单：它的值由库决定，写进去只会得到一个"填的数被忽略"的错。
            // 要显式写自增列是 IDENTITY_INSERT 那条路（本版本未接）。
            if (column.IsIdentity)
            {
                keys.Add(column);
                keyOrdinals.Add(ordinal);

                continue;
            }

            // 并发列同样不进列清单，理由与自增列同一条：那一列的值不归这一侧决定（rowversion 由库递增）。
            // 它也不进 AuditValue 与契约校验——一列不写出去，就没有可校验的值。
            // 应用给值那一族（AppSetsValue，如 ABP 的 ConcurrencyStamp）例外：新值由应用生成、要随行写出去，
            // 所以进列清单、也进 AuditValue 与校验；它不需要回读（值就是刚写的这一个）。
            if (column.IsConcurrencyToken && !column.AppSetsValue)
            {
                (tokens ??= []).Add(column);
                (tokenOrdinals ??= []).Add(ordinal);

                continue;
            }

            columns.Add(column);
            ordinals.Add(ordinal);
        }

        // 回填集 = 自增列 ∪ 并发列，两半的下标表按同一顺序并起来：回包按位置落进实体，
        // Returning 的列序与这张下标表必须是同一把尺子。没有并发列时两半是同一份数组。
        ColumnContract[] keyColumns = [.. keys];
        int[] identityOrdinals = [.. keyOrdinals];

        var traces = new List<HookTrace>();
        var cells = new object?[_rows.Length][];
        var failures = new List<ValidationFailure>();
        var rowFailures = new List<ValidationFailure>();
        var step = 0;

        for (var row = 0; row < _rows.Length; row++)
        {
            var entity = _rows[row] ?? throw new QuickSqlException($"第 {row} 行是 null：插入集合里不能有 null 实体。");

            cells[row] = new object?[columns.Count];

            // 顺序是「先填充、后校验」：租户列这类 NOT NULL 列正是靠 AuditValue 补上的，
            // 反过来会把钩子刚填好的值判成缺，或把本该由钩子补的列静默放过。
            for (var column = 0; column < columns.Count; column++)
            {
                var ordinal = ordinals[column];
                var value = schema.GetValue(entity, ordinal);

                // 未订阅即零成本：不订阅就不构造事件对象。
                if (audit is null)
                {
                    cells[row][column] = value;
                    continue;
                }

                var @event = new AuditValueEvent(entity, columns[column], value, "insert", written => schema.SetValue(entity, ordinal, written));

                // 改写类钩子的异常直接向上失败：吞掉等于写出一份"填充没发生"的行。
                audit(@event);

                if (@event.WasSet)
                {
                    traces.Add(new HookTrace(step++, "AuditValue", HookCategory.Rewrite, $"{columns[column].MemberName} = {StatementEmitter.Text(value)}", $"{columns[column].MemberName} = {StatementEmitter.Text(@event.CurrentValue)}"));
                    value = @event.CurrentValue;
                }

                cells[row][column] = value;
            }

            if (validator.Mode == ValidationMode.Off)
                continue;

            rowFailures.Clear();

            if (validator.TryValidate(entity, rowFailures))
                continue;

            // 单行时不加行号：那时"哪一行"没有意义，消息文本已经点了成员与列。
            foreach (var failure in rowFailures)
                failures.Add(_rows.Length == 1 ? failure : failure with { Message = $"[第 {row} 行] {failure.Message}" });
        }

        if (failures.Count > 0)
            throw new ValidationFailedException(
                $"{_rows.Length} 行插入被拒绝：{failures.Count} 项违反库契约（ValidationMode.{validator.Mode}）。整批先校验后执行，一行都没写进库。",
                failures);

        return _preparation = new Preparation(
            contract,
            schema,
            [.. columns],
            [.. ordinals],
            keyColumns,
            identityOrdinals,
            tokens is null ? keyColumns : [.. keyColumns, .. tokens],
            tokenOrdinals is null ? identityOrdinals : [.. identityOrdinals, .. tokenOrdinals],
            cells,
            traces)
        {
            Groups = SplitGroups(contract, schema),
        };
    }

    /// <summary>分表实体把整批按目标片分组（片内保留入参顺序的行下标，片的先后按各片首行第一次出现的先后），
    /// 非分表实体交回 null = "整批同一张表，照旧按连续区间切块"。算在取值段而不是发射段：分片键的读数
    /// 要在 AuditValue 之后定格，发射段再读就可能读到钩子改过的与没改过的两份值，同一批行进了不同的片。
    /// 分片键为空的那一行当场抛：一条都不写，不把行静默送进"本月"那张表。</summary>
    List<(TableInfo Table, int[] Rows)>? SplitGroups(EntityContract contract, IEntityTypeSchema<T> schema)
    {
        if (contract.Table.Split is not { } split)
            return null;

        var ordinal = Array.IndexOf(contract.Columns, contract.ByMember[split.MemberName]);
        var groups = new List<(TableInfo Table, List<int> Rows)>();
        var index = new Dictionary<TableInfo, int>();

        for (var row = 0; row < _rows.Length; row++)
        {
            var table = SplitRouting.Resolve(contract.Table, split, schema.GetValue(_rows[row], ordinal));

            if (index.TryGetValue(table, out var at))
                groups[at].Rows.Add(row);
            else
            {
                index[table] = groups.Count;
                groups.Add((table, [row]));
            }
        }

        return [.. groups.ConvertAll(g => (g.Table, g.Rows.ToArray()))];
    }

    /// <summary>发射段：按参数预算的公式分块，每块一份独立 AST + DialectSql + 参数表。</summary>
    InsertPlan Build(Preparation preparation, Backfill backfill)
    {
        var provider = _orm.Provider;
        var dialect = provider.Dialect;
        var columns = preparation.Columns;
        var budget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var traces = new List<HookTrace>(preparation.Traces);
        var step = traces.Count;

        if (_rows.Length == 0)
            return new InsertPlan([], 0, columns.Length, 0, budget, "0 行：不产生任何语句，执行直接返回 0。", traces) { Provider = provider };

        RequireColumns(preparation);

        // 回填集为空就没有那一档：带 Returning 的文本会生成一条「看着像在回填」的普通 INSERT。
        // 执行口自己会拒（ReadBackAsync 之前先查），但离线口是给人审阅用的，读到一条假 OUTPUT 就已经误导。
        ColumnContract[] returning = backfill switch
        {
            Backfill.None => [],
            Backfill.Keys => Require(preparation.Keys, "没有自增列：回填主键那一档根本没有东西可回。主键由调用方自己生成的话直接用 Explain()/ExecuteAffrowsAsync。"),
            _ => Require(preparation.Generated, "既没有自增列也没有并发列：没有任何一列的值归库管，回填那一档无事可做。"),
        };

        // 参数预算先留十分之一的余量（文本里还有表名/列名/OUTPUT），再除以每行参数个数，至少 1 行。
        // 请求把块宽压下来或按请求逐条，都走同一个出口取数（见 ChunkRows）。
        var budgetRows = StatementEmitter.RowsPerStatement(budget, columns.Length);
        var rowsPerStatement = ChunkRows(preparation, _requested ?? BulkMethod.MultiValue);
        var degraded = false;

        // L3 → L5 的那一跳：没有批量 RETURNING/OUTPUT 的库要回填，只能逐条。
        // SQL Server 走不到（SupportsBulkKeyRetrieval = true），达梦/Oracle 系必然走到。
        if (backfill != Backfill.None && !provider.Keys.SupportsBulkKeyRetrieval)
        {
            rowsPerStatement = 1;
            degraded = true;
        }

        // 逐条之后键落在哪里：资格收窄到三件同时成立——这一家没有批量 RETURNING、声明了"语句后回读"、
        // 而回填集恰好是一颗自增列。并发列与多颗键不在回读句给得出的东西里，照旧走拒绝路径，不假装覆盖。
        var postRead = degraded && provider.Keys.SupportsPostStatementKeyRead && returning is [{ IsIdentity: true }];

        var chunks = new List<InsertChunk>((_rows.Length + rowsPerStatement - 1) / rowsPerStatement);

        // 建一段文本：不分表时是"连续的这几行"（ordinals 为 null，回填按 FirstRow + 行号对回去），
        // 分表时是"这一张片的那几行"，下标不连续，块自带那份下标表。
        // 两条路共用同一段发射与钩子代码——差别只在表位给哪张表、哪几行进这一块。
        void EmitChunk(TableInfo table, int[]? ordinals, int first, int count)
        {
            var ast = new InsertStatement
            {
                Table = new TableRef(0, QuickSqlOrm.AliasAt(0), table, preparation.Contract),
                Columns = columns,
                Rows = ordinals is null
                    ? RowsOf(preparation.Cells, first, count, columns)
                    : RowsOf(preparation.Cells, ordinals, columns),

                // 回读形不发 RETURNING：键不是这条 INSERT 的结果集，是同一句命令尾巴上那条 SELECT 的。
                Returning = postRead ? [] : returning,
            };

            var writer = new SqlWriter(new StringBuilder(EstimateLength(columns, count)), [], dialect.ParameterPrefix);
            dialect.Emit(in ast, writer);

            if (postRead)
            {
                writer.Append(" ; ");
                provider.Keys.EmitPostStatementKeyRead(writer, returning[0]);
            }

            var emitted = new DialectSql(writer.ToSqlString(), writer.Parameters);
            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "insert", traces, ref step), traces, ref step);

            chunks.Add(new InsertChunk(ordinals is null ? first : ordinals[0], count, ast, sql) { RowOrdinals = ordinals });
        }

        if (preparation.Groups is null)
        {
            for (var first = 0; first < _rows.Length; first += rowsPerStatement)
                EmitChunk(preparation.Contract.Table, null, first, Math.Min(rowsPerStatement, _rows.Length - first));
        }
        else
        {
            foreach (var (table, rows) in preparation.Groups)
                for (var first = 0; first < rows.Length; first += rowsPerStatement)
                {
                    var count = Math.Min(rowsPerStatement, rows.Length - first);
                    EmitChunk(table, rows[first..(first + count)], 0, count);
                }
        }

        var reason = degraded
            ? $"{_rows.Length} 行 × {columns.Length} 列：{provider.Kind} 无批量 RETURNING 而这次要回填 → 退到 L5 逐条（{_rows.Length} 块）" +
              (postRead ? "，每块句尾接语句后回读（键在同一句命令里带回，§14 的那颗口子）" : "，而这一家连语句后回读也没有：构建到 EmitReturning 即拒") +
              $"。语义与批量一致（§12.4 的等价性），代价是 {_rows.Length} 次往返。"
            : $"{_rows.Length} 行 × {columns.Length} 列，参数预算 {budget} → 每块 {rowsPerStatement} 行（{chunks.Count} 块），L3 多值语句。";

        if (_requested == BulkMethod.SingleRow)
            reason += " 块宽 1 是按请求来的（WithBulk(SingleRow)），不是这一家给不出批量。";
        else if (_rowsPerRoundTrip is { } cap && cap < budgetRows && !degraded)
            reason += $" 块宽 {rowsPerStatement} 是请求压下来的（预算允许 {budgetRows} 行）。";

        if (preparation.Groups is { Count: > 1 } shards)
            reason += $" 这批行分布在 {shards.Count} 张片上：块按片切，跨片不共用一条语句（一条 INSERT 只有一个目标表）。";

        if (chunks.Count > 1)
            reason += " 跨块不是原子的（§12.4 第 8 项）：要整批原子就包 §6 的事务。";

        return new InsertPlan(chunks, _rows.Length, columns.Length, rowsPerStatement, budget, reason, traces) { Provider = provider };
    }

    ColumnContract[] Require(ColumnContract[] columns, string whyEmpty)
        => columns.Length > 0 ? columns : throw new QuickSqlException($"{Prepare().Table} {whyEmpty}");

    /// <summary>每格都参数化；ParameterBinding 的回放在 StatementEmitter.ApplyBinding，三条写链共用一份。
    /// 不分表走这一颗，一块不给它多分配一个下标数组（为分表把连续区间物化成 int[] 是让整批调用方替它付钱）。</summary>
    static IReadOnlyList<IReadOnlyList<SqlExpression?>> RowsOf(object?[][] cells, int first, int count, ColumnContract[] columns)
    {
        var rows = new IReadOnlyList<SqlExpression?>[count];

        for (var row = 0; row < count; row++)
        {
            var source = cells[first + row];
            var values = new SqlExpression?[columns.Length];

            // 每一格都是参数：这一版不产生 DEFAULT 格（那要走"部分列插入"）。
            // 值为 null 也照样参数化成 NULL，而不是让那一格落 DEFAULT——有默认值约束的列上这两者不同。
            for (var column = 0; column < columns.Length; column++)
                values[column] = new ParameterValue(source[column], columns[column]);

            rows[row] = values;
        }

        return rows;
    }

    /// <summary>分表那一块：行在入参数组里不连续（按月切表时相邻两行可以隔三个月），按那一份下标表取行，
    /// 其余与上面那颗相同。内层不抽共享方法：总共三行，抽取要为每次分块多走一层调用与一个数组。</summary>
    static IReadOnlyList<IReadOnlyList<SqlExpression?>> RowsOf(object?[][] cells, int[] ordinals, ColumnContract[] columns)
    {
        var rows = new IReadOnlyList<SqlExpression?>[ordinals.Length];

        for (var row = 0; row < rows.Length; row++)
        {
            var source = cells[ordinals[row]];
            var values = new SqlExpression?[columns.Length];

            for (var column = 0; column < columns.Length; column++)
                values[column] = new ParameterValue(source[column], columns[column]);

            rows[row] = values;
        }

        return rows;
    }

    /// <summary>只用于 StringBuilder 的预容量：每颗参数按 "@p123, " 估 7 个字符。</summary>
    static int EstimateLength(ColumnContract[] columns, int rows)
    {
        var length = 96 + rows * (columns.Length * 7 + 4);

        foreach (var column in columns)
            length += column.ColumnName.Length + 3;

        return length;
    }

    /// <summary>集群从契约的表位算：写侧永远是主库那一格，所以这里只带名字，不带角色——
    /// 那个结论由 AdoExecutor.RentAsync 一处下，两处各下一遍就会漂移。</summary>
    ExecutionTag Tag()
    {
        var table = Prepare().Contract.Table;

        return new ExecutionTag("insert", [table.Name], DataSourceRouting.ClusterOf(table));
    }

    /// <summary>取值段的产物。Cells 已经是 AuditValue 之后的值，
    /// 所以三个出口共用它 = 同一份数据、几种文本。</summary>
    sealed record Preparation(
        EntityContract Contract,
        IEntityTypeSchema<T> Schema,
        ColumnContract[] Columns,
        int[] Ordinals,
        ColumnContract[] Keys,
        int[] KeyOrdinals,
        ColumnContract[] Generated,
        int[] GeneratedOrdinals,
        object?[][] Cells,
        IReadOnlyList<HookTrace> Traces)
    {
        /// <summary>分表实体的目标片分组，null = 不分表（整批同一张表，切块照旧走连续区间）。
        /// 片的内容是那几行在入参数组里的下标，一个块装的行可以不连续，回填仍按 InsertChunk.RowOrdinals 对得回去。
        /// 在取值段一次算完：发射段、装载通道、往返次数估算三处都要分组，而分片键值刚被 AuditValue 填过——
        /// 三处各读各的就会给出三份可能不同的分片答案。</summary>
        public IReadOnlyList<(TableInfo Table, int[] Rows)>? Groups { get; init; }

        public string Table => Contract.Table.Name;
    }

    /// <summary>发射段要不要带 Returning，以及带哪几列。三档而不是布尔：「回填主键」（公开口那一档）与
    /// 「回填库里给的那些列」（跟踪器那一档）是两件事，只有后者在 OUTPUT 里多出并发列一列。</summary>
    enum Backfill
    {
        None,

        /// <summary>只有自增列。没有并发列时与  逐字同文。</summary>
        Keys,

        /// <summary>自增列 ∪ 并发列。</summary>
        Generated,
    }
}
