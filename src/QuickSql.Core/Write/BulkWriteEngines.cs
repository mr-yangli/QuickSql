using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;
using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Write;

/// <summary>UpdateRange 链的落点：按主键整行更新。与 UpdateEngine 链的关键差别在两侧各让了一步——
/// 那条链由调用方点名改哪几列、一条语句；这一条列清单由契约给、一次往返可以并几条语句。
/// 表达式树一个都没有（列来自 EntityContract、值来自编译好的读写委托），所以入口零 RUC/RDC 税。</summary>
sealed class UpdateBulkEngine<T> : IUpdateBulk<T> where T : class
{
    readonly QuickSqlOrm _orm;
    readonly T[] _rows;
    readonly EntityContract _contract;
    readonly IEntityTypeSchema<T> _schema;
    readonly TableRef _table;
    readonly ColumnContract[] _settable;
    readonly int[] _settableOrdinals;
    readonly ColumnContract[] _keys;
    readonly int[] _keyOrdinals;

    BulkWriteMethod _requested = BulkWriteMethod.Auto;
    FallbackPolicy _fallback = FallbackPolicy.DegradeAndLog;
    bool _atomic = true;

    BulkWritePlan? _plan;

    /// <summary>调用方在主键之外钉的那些条件（Where）。null = 一颗都没有，
    /// Build 走原来的形状而一个表达式都不翻，"入口零反射税"对没用这一颗的调用方仍然成立。</summary>
    List<Expression<Func<T, bool>>>? _extra;

    /// <summary>取值段的格子：每行 × 每个可写列一颗，已经是 AuditValue 之后的值。
    /// null = 那一段还没跑过。主键列不在里面——它是用来定位那一行的，不是被写出去的列。</summary>
    object?[][]? _cells;

    /// <summary>取值段那几笔改写痕迹，发射段的 traces 以它打头（钩子按顺序、步号连续才回放得起来）。
    /// 没人订阅时是空表。</summary>
    IReadOnlyList<HookTrace> _valueTraces = [];

    public UpdateBulkEngine(QuickSqlOrm orm, T[] rows)
    {
        _orm = orm;
        _rows = rows;
        _contract = orm.Schemas.Contract<T>();
        _schema = orm.Schemas.Schema<T>();

        if (_contract.Table.Split is not null)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），UpdateRange 不接：一批行会散在几张片上，" +
                "而这一档今天没有按片分组的账。要批量改一片就选片后逐条走 orm.Update<T>().SplitTable(那一片)。");

        if (_contract.PrimaryKey.Length == 0)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 没有主键列：UpdateRange 按主键定位，WHERE 出不来。" +
                "要么给实体点主键（[PrimaryKey] 或约定名 Id），要么按条件走 orm.Update<T>().Set(...).Where(...)。");

        _keys = _contract.PrimaryKey;

        var settable = new List<ColumnContract>(_contract.Columns.Length);
        var setOrdinals = new List<int>(_contract.Columns.Length);
        var keyOrdinals = new List<int>(_keys.Length);

        for (var ordinal = 0; ordinal < _contract.Columns.Length; ordinal++)
        {
            var column = _contract.Columns[ordinal];

            // 主键进 WHERE 不进 SET；库里给值的列（自增/计算列）与并发列不接受书写——三条判据与
            // UpdateEngine.ColumnOf 逐字同源，那里拦调用方点名的列，这里拦契约整行的列。
            // 应用给值的并发列（AppSetsValue）例外：新值在实体上，随整行写出去（WHERE 仍比快照）。
            if (column.IsPrimary || column.ValueFromStore || (column.IsConcurrencyToken && !column.AppSetsValue))
                continue;

            settable.Add(column);
            setOrdinals.Add(ordinal);
        }

        if (settable.Count == 0)
            throw new QuickSqlException($"{_contract.Table.Name} 除了主键与库里给值的那几列没有可写的列：整行更新只剩 SET 空串，那样的语句没有意义。");

        _settable = [.. settable];
        _settableOrdinals = [.. setOrdinals];

        foreach (var key in _keys)
            keyOrdinals.Add(Array.IndexOf(_contract.Columns, key));

        _keyOrdinals = [.. keyOrdinals];

        _table = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
    }

    public IUpdateBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog)
    {
        _requested = method;
        _fallback = fallback;
        _plan = null;

        // 只作废发射段那份：换档改的是"几条语句、怎么并"，而取值段（AuditValue）的契约是
        // 同一批实体只许问一次。与 InsertEngine.WithBulk 同一条分寸。
        return this;
    }

    public IUpdateBulk<T> WithBatchTransaction(bool atomic = true)
    {
        _atomic = atomic;

        return this;
    }

    public IUpdateBulk<T> Where(Expression<Func<T, bool>> predicate)
    {
        // 存 lambda 不存文本，与 orm.Filters 同一条纪律（闭包在每次建文本时重新求值）；
        // 连写多次就是往清单末尾追加，翻译时按同样的顺序折成 AND 链。
        (_extra ??= []).Add(predicate);
        _plan = null;

        return this;
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public BulkWritePlan Explain()
    {
        var (method, _) = Decide(_requested);

        return _plan ??= Build(method);
    }

    public BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null) => Decide(requested ?? _requested).Report;

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public async Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default)
    {
        var (method, report) = Decide(_requested);

        if (_rows.Length == 0)
            return report;

        var plan = Build(method);

        // 决策里的估数换成发射段的实数（条数由 Build 算，两处不许各算一遍），与 InsertEngine 同一条覆写。
        report = report with { ChunkSize = plan.RowsPerCommand, RoundTrips = plan.Commands.Count };

        var tag = Tag();

        // 库里报回来的行数跨命令累加：主键之外还钉了条件时，这一颗就是"有没有行没被命中"的读数
        // （乐观锁的验收面），所以它必须来自执行段而不是决策段的估数。
        var affected = 0;

        async Task SendAll()
        {
            foreach (var command in plan.Commands)
                affected += await _orm.Ado.NonQueryAsync(command, tag, ct).ConfigureAwait(false);
        }

        // 一次提交那一档（WithBatchTransaction 的默认）。判据是语句数而不是命令数：并档把 2000 句并成
        // 一条命令，服务器侧仍然一句一提交，而每次提交 flush 一次 redo——同一批 2000 行整行更新实测
        // 6386 ms ∶ 263 ms，差距全部来自提交次数。UpdateRange 的语句数：并档与逐条档都是一行一句；
        // upsert 档一句命令就是一句，语句数是命令数。判错了就是该包的没包、不必包的白包。
        // 第四格：provider 声明了 Transactions 位才包——没有位的那一家走逐句 autocommit 直发，
        // 整批在库里不原子（行为诚实条款全文在位注释与用法册）。
        var statements = method == BulkWriteMethod.Upsert ? plan.Commands.Count : _rows.Length;

        if (BulkAtomicity.ShouldWrap(_atomic, statements)
            && TransactionCapability.Declared(_orm.Provider)
            && QuickSqlTransaction.For(_orm.Ado) is null)
        {
            using var tran = _orm.BeginTransaction(DataSourceRouting.ClusterOf(_contract.Table));

            await SendAll().ConfigureAwait(false);
            tran.Commit();

            return report with { UsedTransaction = true, AffectedRows = affected };
        }

        await SendAll().ConfigureAwait(false);

        return report with { AffectedRows = affected };
    }

    /// <summary>档位判定：纯函数，不建文本、不碰连接（判据与 InsertEngine.Decide 同一个形状）。
    /// Auto 拿不到 Merged 不算降级——它没承诺过任何一档；点名 Merged 而拿不到才算，拿不到时
    /// Never 抛、其余退逐条并带降级标记。参数预算在这里不参与：逐条那条路每颗参数都各走各的语句。</summary>
    (BulkWriteMethod Method, BulkWriteReport Report) Decide(BulkWriteMethod requested)
    {
        var provider = _orm.Provider;
        var rows = _rows.Length;

        if (rows == 0)
            return (BulkWriteMethod.Merged, new BulkWriteReport(requested, BulkWriteMethod.Merged, 0, 0, 0, false,
                "0 行：一档都不必选。不建文本、不取连接，执行直接返回 0。"));

        var supports = provider.Bulk?.SupportsMergedStatements == true;

        // SingleRow 是点名最底档；Merged 点名而拿不到 → 按 fallback 三档办；Auto 拿不到 → 取逐条、不算降级。
        if (requested == BulkWriteMethod.SingleRow)
            return Textless(BulkWriteMethod.SingleRow, requested, degraded: false,
                $"{rows} 行按请求逐条（一行一条语句、一次往返）。语义与并档一致（§12.4 的等价性），代价是 {rows} 次往返。");

        // Upsert 只在被点名时参与：Auto 的阶梯永远不取它（语义差不是速度差）。
        if (requested == BulkWriteMethod.Upsert)
        {
            var clause = UpsertClause(provider.Dialect);

            if (clause is null)
            {
                if (_fallback == FallbackPolicy.Never)
                    throw new QuickSqlException($"请求了 {provider.Kind} 的一句 upsert 档而它没交 UpsertClause：退路被 FallbackPolicy.Never 封死。");

                // 退到"这一家的最佳改档"：数字（往返与块宽）由那一家自己那条阶梯算，不另算一遍，
                // 只把 Requested 换回被点名的那一颗并把降级记上——语义跟着退，那句话必须让现场看见。
                var best = supports ? BulkWriteMethod.Merged : BulkWriteMethod.SingleRow;
                var (method, report) = Decide(best);

                return (method, report with
                {
                    Requested = requested,
                    Degraded = true,
                    Reason = $"{provider.Kind} 没接一句 upsert 的句尾（UpsertClause 交回 null）→ 退到这一家的最佳改档 {method}：" +
                             $"{report.Reason} 这一退换掉的是语义不只是速度：库里没有的那一行不会再插进去。",
                });
            }

            var upsertBudget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
            var upsertWidth = StatementEmitter.RowsPerStatement(upsertBudget, _settable.Length + _keys.Length);
            var upsertTrips = (rows + upsertWidth - 1) / upsertWidth;

            return (BulkWriteMethod.Upsert, new BulkWriteReport(requested, BulkWriteMethod.Upsert, rows, upsertTrips, upsertWidth, false,
                $"一句 upsert（{provider.Kind} 交的句尾）：{rows} 行 × 每行 {_settable.Length + _keys.Length} 颗参数" +
                $"（写入清单 = 可写 {_settable.Length} 列 + 主键 {_keys.Length} 列），参数预算 {upsertBudget} → " +
                $"每条命令 {upsertWidth} 行（{upsertTrips} 条命令），一条命令 = 一句 = 一次往返。" +
                $"库里没有的那一行会插进去（与 Merged 档的语义差）。{BulkAtomicity.Sentence(_atomic, upsertTrips)}"));
        }

        if (requested == BulkWriteMethod.Merged && !supports)
        {
            if (_fallback == FallbackPolicy.Never)
                throw new QuickSqlException($"请求了 {provider.Kind} 的多语句合并档而它没声明 SupportsMergedStatements：退路被 FallbackPolicy.Never 封死。");

            return Textless(BulkWriteMethod.SingleRow, requested, degraded: true,
                $"{provider.Kind} 没有多语句批的读数（SupportsMergedStatements=false）→ 退回逐条：{rows} 次往返。");
        }

        if (!supports)
            return Textless(BulkWriteMethod.SingleRow, requested, degraded: false,
                $"{rows} 行走逐条：{provider.Kind} 今天没有多语句批的读数，Auto 取阶梯上第一个满足条件的档（§12.1 写侧扩展）。");

        var budget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var width = StatementEmitter.RowsPerStatement(budget, _settable.Length + _keys.Length);
        var trips = (rows + width - 1) / width;

        return (BulkWriteMethod.Merged, new BulkWriteReport(requested, BulkWriteMethod.Merged, rows, trips, width, false,
            $"{rows} 行 × 每行 {_settable.Length + _keys.Length} 颗参数（SET {_settable.Length} 列 + WHERE {_keys.Length} 列），" +
            $"参数预算 {budget} → 每条命令 {width} 行（{trips} 条命令）。一条命令 = 一次往返；{BulkAtomicity.Sentence(_atomic, rows)}"));
    }

    (BulkWriteMethod Method, BulkWriteReport Report) Textless(BulkWriteMethod method, BulkWriteMethod requested, bool degraded, string reason)
        => (method, new BulkWriteReport(requested, method, _rows.Length, _rows.Length, 1, degraded, reason));

    /// <summary>取值段：把每行的可写列读成格子，逐格问一次 AuditValue（种类是 "update"），只跑一次——
    /// 两份文本与一次执行共用同一份格子。三条纪律：跑一次（这个钩子会写实体，同一只实体问两遍就盖两遍）；
    /// 主键列不进这一格（它是这条链上 WHERE 的判据，不是写出去的列，让它过钩子等于允许订阅方把定位条件改掉）；
    /// 未订阅即零成本（不构造事件对象，格子就是读出来的原值——仍然建格子是为了"同一批实体的两种文本
    /// 读的必须是同一份数"）。改写痕迹记在 _valueTraces，步号从 0 起，发射段接着往下编。</summary>
    object?[][] Prepare()
    {
        if (_cells is { } done)
            return done;

        var audit = _orm.Aop.AuditValue;
        var cells = new object?[_rows.Length][];
        var traces = new List<HookTrace>();

        for (var row = 0; row < _rows.Length; row++)
        {
            var entity = _rows[row] ?? throw new QuickSqlException($"第 {row} 行是 null：批量更新的集合里不能有 null 实体。");
            var writable = new object?[_settable.Length];

            for (var column = 0; column < _settable.Length; column++)
            {
                var ordinal = _settableOrdinals[column];
                var value = _schema.GetValue(entity, ordinal);

                if (audit is null)
                {
                    writable[column] = value;

                    continue;
                }

                var @event = new AuditValueEvent(entity, _settable[column], value, "update", written => _schema.SetValue(entity, ordinal, written));

                // 改写类钩子的异常直接向上失败（硬规则 3）：吞掉等于交出一份"填充没发生"的 SET 清单。
                audit(@event);

                if (@event.WasSet)
                {
                    traces.Add(new HookTrace(traces.Count, "AuditValue", HookCategory.Rewrite, $"{_settable[column].MemberName} = {StatementEmitter.Text(value)}", $"{_settable[column].MemberName} = {StatementEmitter.Text(@event.CurrentValue)}"));
                    value = @event.CurrentValue;
                }

                writable[column] = value;
            }

            cells[row] = writable;
        }

        _cells = cells;
        _valueTraces = traces;

        return cells;
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Build(BulkWriteMethod method)
    {
        if (method == BulkWriteMethod.Upsert)
            return BuildUpsert();

        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var width = method == BulkWriteMethod.SingleRow ? 1 : StatementEmitter.RowsPerStatement(budget, _settable.Length + _keys.Length);

        var values = Prepare();
        var traces = new List<HookTrace>(_valueTraces.Count);
        traces.AddRange(_valueTraces);

        var step = traces.Count;
        var commands = new List<DialectSql>((_rows.Length + width - 1) / width);

        // 过滤器与调用方点名的条件都只翻一次：同一趟 Build 里对同一张表翻译结果不变（谓词是构建期闭包常量），
        // 翻译出来的树不可变，行循环里只是把同一棵子树接到不同的键谓词上。逐行重付是 2000 次表达式树归约的账。
        SqlExpression[] filterSql;

        {
            var registrations = _orm.Filters.For(_table);
            filterSql = new SqlExpression[registrations.Count];

            for (var i = 0; i < registrations.Count; i++)
                filterSql[i] = _orm.Filters.Translate(registrations[i], _table);
        }

        SqlExpression[]? extraSql = null;

        if (_extra is not null)
        {
            extraSql = new SqlExpression[_extra.Count];

            for (var i = 0; i < _extra.Count; i++)
                extraSql[i] = _orm.Filters.TranslateFree(_extra[i], _table);
        }

        for (var first = 0; first < _rows.Length; first += width)
        {
            var count = Math.Min(width, _rows.Length - first);

            // 一条命令一份共享参数表：SqlWriter 的编号长在参数表上，
            // 并进来的每条语句拿到的名字天然不重号，执行层按名落地时不撞车。
            var parameters = new List<AdoParameter>(count * (_settable.Length + _keys.Length));
            var text = new StringBuilder(StatementEmitter.EstimateLength(count * (_settable.Length + _keys.Length), IdentifierChars()));

            for (var row = first; row < first + count; row++)
            {
                // 写入位取取值段那份（AuditValue 之后的格子），键位仍然直接读实体：那几列是定位条件，不过钩子。
                var entity = _rows[row];
                var writable = values[row];

                var sets = new SetClause[_settable.Length];

                for (var column = 0; column < _settable.Length; column++)
                    sets[column] = new SetClause(_settable[column], new ParameterValue(writable[column], _settable[column]));

                SqlExpression? where = null;

                for (var key = 0; key < _keys.Length; key++)
                {
                    var value = _schema.GetValue(entity, _keyOrdinals[key]);

                    if (value is null)
                        throw new QuickSqlException($"第 {row} 行的主键列 {_keys[key].ColumnName} 是 null：UpdateRange 按主键定位，null 键定位不到任何行。新行请用 InsertRange。");

                    var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, _keys[key]), new ParameterValue(value, _keys[key]));
                    where = where is null ? equal : new SqlBinaryExpression(BinaryOperator.And, where, equal);
                }

                // 调用方点名的条件排在主键之后、全局过滤器之前：形状与读侧同一条纪律（往末尾追加 AND）。
                if (extraSql is not null)
                    foreach (var predicate in extraSql)
                        where = new SqlBinaryExpression(BinaryOperator.And, where!, predicate);

                var finalWhere = where!;

                if (filterSql.Length > 0)
                    foreach (var filter in filterSql)
                        finalWhere = new SqlBinaryExpression(BinaryOperator.And, finalWhere, filter);

                var ast = new UpdateStatement { Table = _table, Sets = sets, Where = finalWhere };

                var writer = new SqlWriter(new StringBuilder(256), parameters, dialect.ParameterPrefix);
                dialect.Emit(in ast, writer);

                if (text.Length > 0)
                    text.Append(" ; ");

                text.Append(writer.ToSqlString());
            }

            var emitted = new DialectSql(text.ToString(), parameters);
            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "update", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var reason = method == BulkWriteMethod.SingleRow
            ? $"{_rows.Length} 行逐条：{_rows.Length} 条命令、每条一行。"
            : $"{_rows.Length} 行 × 每行 {_settable.Length + _keys.Length} 颗参数（SET {_settable.Length} 列 + WHERE {_keys.Length} 列），参数预算 {budget} → 每条命令 {width} 行（{commands.Count} 条命令）。";

        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            reason += $" 每条语句的 WHERE 里除了主键还叠着 {injected} 条全局过滤器（§10.7 的 orm.Filters）。";

        if (_extra is not null)
            reason += $" 每条语句的 WHERE 里另有调用方点名的 {_extra.Count} 条条件，叠在主键之后、全局过滤器之前。";

        reason += " " + BulkAtomicity.Sentence(_atomic, _rows.Length);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>StringBuilder 的预容量要用到的标识符字符数。两种形状共用一把尺：
    /// 并档每行一句 UPDATE，upsert 档一条命令一句多值 INSERT 加一段句尾（句尾长度由 BuildUpsert 自己补）。</summary>
    int IdentifierChars()
    {
        var chars = _contract.Table.Name.Length * 2 + 32;

        foreach (var column in _settable)
            chars += column.ColumnName.Length + 3;

        foreach (var key in _keys)
            chars += key.ColumnName.Length + 5;

        return chars;
    }

    /// <summary>Upsert 档的文本：一条命令一句 INSERT … VALUES (…),(…) 冲突子句，块宽仍按参数预算那把尺
    /// （每行参数 = 可写列 + 主键列，与并档同数）。发的是内核那一份 InsertStatement 走方言 Emit 的同一条出路，
    /// 句尾由这一家的 UpsertClause 补——列清单、参数表与取号与 InsertRange 共用一处真相。
    /// 两道拒收在这一层（建文本时判，纯档位口不交这笔税）：全局过滤器在场（那一句没有 WHERE 可以叠）；
    /// 主键由库给值而这一行的键仍是 CLR 默认值（那种键的冲突判定出不来，写进去的是新行而不是改那一行）。
    /// 钩子回放按 "update" 那一种：档位不是种类。</summary>
    [RequiresUnreferencedCode("全局过滤器的在场判定要问 orm.Filters 挂着多少条（同 Explain 那句）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan BuildUpsert()
    {
        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;

        var clause = UpsertClause(dialect)
                     ?? throw new QuickSqlException($"{_orm.Provider.Kind} 没交 UpsertClause：一句 upsert 档在这一家不可达，而计划已经定在这一档。");

        RefuseUpsertFilters();

        if (_extra is not null)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 的这批 {nameof(IUpdateBulk<T>)} 带着调用方点名的 {_extra.Count} 条条件，一句 upsert 档不接：" +
                "那一句发的是多值 INSERT 加冲突子句，冲突判定只认主键，而 WHERE 不在它的形状里——把条件静默丢掉就是" +
                "改到条件之外的行。要么退回 Merged 或 SingleRow 那一档（那里每行一句 UPDATE，条件叠得进去），要么自己先按条件读一遍再交这批行。");

        var columns = new ColumnContract[_settable.Length + _keys.Length];
        var ordinals = new int[columns.Length];
        var quoted = new string[columns.Length];

        Array.Copy(_settable, columns, _settable.Length);
        Array.Copy(_keys, 0, columns, _settable.Length, _keys.Length);

        for (var column = 0; column < _settable.Length; column++)
        {
            ordinals[column] = _settableOrdinals[column];
            quoted[column] = Quoted(dialect, _settable[column].ColumnName);
        }

        for (var key = 0; key < _keys.Length; key++)
        {
            ordinals[_settable.Length + key] = _keyOrdinals[key];
            quoted[_settable.Length + key] = Quoted(dialect, _keys[key].ColumnName);
        }

        var storeGivenKey = Array.Exists(_keys, static key => key.ValueFromStore);
        var width = StatementEmitter.RowsPerStatement(budget, columns.Length);

        var values = Prepare();
        var traces = new List<HookTrace>(_valueTraces.Count);
        traces.AddRange(_valueTraces);

        var step = traces.Count;
        var commands = new List<DialectSql>((_rows.Length + width - 1) / width);

        for (var first = 0; first < _rows.Length; first += width)
        {
            var count = Math.Min(width, _rows.Length - first);
            var parameters = new List<AdoParameter>(count * columns.Length);
            var rows = new IReadOnlyList<SqlExpression?>[count];

            for (var row = 0; row < count; row++)
            {
                var entity = _rows[first + row];
                var writable = values[first + row];
                var cells = new SqlExpression?[columns.Length];

                for (var column = 0; column < columns.Length; column++)
                {
                    // 与并档同一分岔：写入位在取值段那份里（AuditValue 之后），键位是冲突判据，不过钩子。
                    var value = column < _settable.Length ? writable[column] : _schema.GetValue(entity, ordinals[column]);

                    if (column >= _settable.Length)
                    {
                        // 键位上的两种"这颗键定位不到"各一句：null 与并档逐字同判，而零是这一档独有的——
                        // 主键由库给值时那个默认形态代表"还没键"，放过去就是插一行没人要的新行。
                        if (value is null)
                            throw new QuickSqlException($"第 {first + row} 行的主键列 {columns[column].ColumnName} 是 null：UpdateRange 按主键定位，null 键定位不到任何行。新行请用 InsertRange。");

                        if (storeGivenKey && StatementEmitter.IsUnassignedKey(value!))
                            throw new QuickSqlException(
                                $"第 {first + row} 行的主键列 {columns[column].ColumnName} 由库给值（自增 ∶ 序列），而它交来的是没给值的那个默认形态：" +
                                $"一句 upsert 的冲突判定出不来，写进去的会是一个新行而不是改了那一行。要么把键给实，要么这种现场走 SaveChanges 或 orm.InsertRange。");
                    }

                    // 写入位上的 null 不转成库默认值：这一条链的契约与 Merged 档相同——整行交出去，
                    // 那一格说的是 NULL 就是 NULL（与 InsertRange 的 null→DEFAULT 正对面）。
                    cells[column] = new ParameterValue(value, columns[column]);
                }

                rows[row] = cells;
            }

            var ast = new InsertStatement { Table = _table, Columns = columns, Rows = rows };

            var writer = new SqlWriter(
                new StringBuilder(StatementEmitter.EstimateLength(count * columns.Length, IdentifierChars()) + clause.Length),
                parameters, dialect.ParameterPrefix);

            dialect.Emit(in ast, writer);
            writer.Append(' ').Append(clause);

            var emitted = new DialectSql(writer.ToSqlString(), parameters);
            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "update", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var reason = $"一条命令 = 一句多值 upsert：{_rows.Length} 行 × 每行 {columns.Length} 颗参数（写入清单 = 可写 {_settable.Length} 列 + 主键 {_keys.Length} 列），" +
                     $"参数预算 {budget} → 每条命令 {width} 行（{commands.Count} 条命令）。句尾出自 {_orm.Provider.Kind}。";

        // 与并档正对面的一句话：这一档一句就是一句，所以语句数是命令数而不是行数（包事务的判据同源）。
        reason += " " + BulkAtomicity.Sentence(_atomic, commands.Count);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>向这一家要 upsert 的句尾（null = 不接）。三份列名清单都先过方言的取号口再交，
    /// provider 拿到的已经是这一家的引号与折叠形态，它自己不再猜。</summary>
    string? UpsertClause(ISqlDialect dialect)
    {
        var strategy = _orm.Provider.Bulk;

        if (strategy is null)
            return null;

        var settable = new string[_settable.Length];
        var keys = new string[_keys.Length];

        for (var column = 0; column < _settable.Length; column++)
            settable[column] = Quoted(dialect, _settable[column].ColumnName);

        for (var key = 0; key < _keys.Length; key++)
            keys[key] = Quoted(dialect, _keys[key].ColumnName);

        return strategy.UpsertClause(new UpsertColumns([.. settable, .. keys], settable, keys, name => Quoted(dialect, name)));
    }

    static string Quoted(ISqlDialect dialect, string identifier)
    {
        var writer = StatementEmitter.Writer(dialect, identifier.Length + 4);
        dialect.EmitIdentifier(writer, identifier);

        return writer.ToSqlString();
    }

    /// <summary>全局过滤器在场时一句 upsert 不可达：INSERT…冲突子句里没有 WHERE 可以叠过滤器，
    /// 而冲突那一支改的是"键相同的那一行"——租户、软删那道闸门在这句文本上无处安放。
    /// 拒在这里而不是放过：放过的后果是改宽（越过滤器的行被改）。</summary>
    void RefuseUpsertFilters()
    {
        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 上挂着 {injected} 条全局过滤器（§10.7 的 orm.Filters），一句 upsert 档不接：那一句没有 WHERE 可以叠过滤器，" +
                "冲突那一支会改到过滤器之外的行。要带过滤器批量改就走 orm.UpdateRange 的 Merged 或 SingleRow 档，那里每条语句的 WHERE 都叠得出。");
    }


    ExecutionTag Tag() => new("update", [_contract.Table.Name], DataSourceRouting.ClusterOf(_contract.Table));
}

/// <summary>DeleteRange 链的落点：按单列主键删整批。Merged 档是一句 WHERE 键 IN (…) 的单条语句，
/// 不是多语句批——所以这一条链的 Auto 在所有 provider 上都直接取 Merged，不读 SupportsMergedStatements
/// （那颗位只为 UpdateRange 的分号批存在）。复合主键当场拒并指路：IN 装不下元组，
/// 逐对 OR 展开把参数预算的账绕开了，两害都不如指路。</summary>
sealed class DeleteBulkEngine<T> : IDeleteBulk<T> where T : class
{
    readonly QuickSqlOrm _orm;
    readonly T[] _rows;
    readonly EntityContract _contract;
    readonly IEntityTypeSchema<T> _schema;
    readonly TableRef _table;
    readonly ColumnContract _key;
    readonly int _keyOrdinal;

    BulkWriteMethod _requested = BulkWriteMethod.Auto;
    FallbackPolicy _fallback = FallbackPolicy.DegradeAndLog;
    bool _atomic = true;

    /// <summary>软删那一句写进「时间」列的值，一颗属于这一批：切成几句 IN 都是同一个删除时刻，
    /// 而 Explain() 与紧随的执行在这条链上是两次构建，所以它不能挂在 Build 的局部上。
    /// 没有注册软删时它永远是 null。WithBulk 清 _plan 而不清它。</summary>
    DateTime? _deletedAt;

    BulkWritePlan? _plan;

    public DeleteBulkEngine(QuickSqlOrm orm, T[] rows)
    {
        _orm = orm;
        _rows = rows;
        _contract = orm.Schemas.Contract<T>();
        _schema = orm.Schemas.Schema<T>();

        if (_contract.Table.Split is not null)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），DeleteRange 不接：一批行会散在几张片上。" +
                "要批量删一片就选片后走 orm.Delete<T>().SplitTable(那一片).Where(...)。");

        if (_contract.PrimaryKey.Length == 0)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 没有主键列：DeleteRange 按主键定位，WHERE 出不来。" +
                "按条件删走 orm.Delete<T>().Where(...)，或先给实体点主键。");

        if (_contract.PrimaryKey.Length > 1)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 的主键是 {_contract.PrimaryKey.Length} 列复合键：IN 清单装不下元组，逐对 OR 展开又绕开参数预算的账。" +
                "请逐行走 orm.Delete<T>().Where(复合键等式)，或经 SaveChanges 摘行。");

        _key = _contract.PrimaryKey[0];
        _keyOrdinal = Array.IndexOf(_contract.Columns, _key);
        _table = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
    }

    public IDeleteBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog)
    {
        _requested = method;
        _fallback = fallback;
        _plan = null;

        return this;
    }

    public IDeleteBulk<T> WithBatchTransaction(bool atomic = true)
    {
        _atomic = atomic;

        return this;
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public BulkWritePlan Explain() => _plan ??= Build(Decide(_requested).Method);

    public BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null) => Decide(requested ?? _requested).Report;

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public async Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default)
    {
        var (method, report) = Decide(_requested);

        if (_rows.Length == 0)
            return report;

        var plan = Build(method);

        report = report with { ChunkSize = plan.RowsPerCommand, RoundTrips = plan.Commands.Count };

        var tag = Tag();

        var affected = 0;

        async Task SendAll()
        {
            foreach (var command in plan.Commands)
                affected += await _orm.Ado.NonQueryAsync(command, tag, ct).ConfigureAwait(false);
        }

        // 这条链上的语句数就是命令数：Merged 档一条命令一句 IN 清单，SingleRow 档一条命令一句等值。
        // "包不包"在这里几乎永远是假——一句 IN 删两万颗键本身就原子，包它只多付两次往返；
        // 真会包的只有键数超出一句的预算、这批被切成两句以上那一种。第四格与 UpdateBulk 同一条：
        // provider 声明了 Transactions 位才包，没有位的那一家逐句 autocommit 直发。
        if (BulkAtomicity.ShouldWrap(_atomic, plan.Commands.Count)
            && TransactionCapability.Declared(_orm.Provider)
            && QuickSqlTransaction.For(_orm.Ado) is null)
        {
            using var tran = _orm.BeginTransaction(DataSourceRouting.ClusterOf(_contract.Table));

            await SendAll().ConfigureAwait(false);
            tran.Commit();

            return report with { UsedTransaction = true, AffectedRows = affected };
        }

        await SendAll().ConfigureAwait(false);

        return report with { AffectedRows = affected };
    }

    (BulkWriteMethod Method, BulkWriteReport Report) Decide(BulkWriteMethod requested)
    {
        // 一句 upsert 在删除链上没有意义：那一句的"缺键就插"那一支要写的正是被叫删的那一行。
        // 拒在选层之前，所以 ExplainBulk 与执行两条出口都撞得到这一句。
        if (requested == BulkWriteMethod.Upsert)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 的 DeleteRange 不接 Upsert 档：那一句是 INSERT … 冲突子句，缺键的那一支写进去的是一个新行，" +
                "而这一条链要的恰好是没有那一行。删整批键用 Merged 档（一句 IN 清单），逐条用 SingleRow 档。");

        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var rows = _rows.Length;
        var method = requested == BulkWriteMethod.SingleRow ? BulkWriteMethod.SingleRow : BulkWriteMethod.Merged;
        var width = method == BulkWriteMethod.SingleRow ? 1 : StatementEmitter.RowsPerStatement(budget, 1);
        var trips = (rows + width - 1) / width;

        if (rows == 0)
            return (method, new BulkWriteReport(requested, method, 0, 0, 0, false,
                "0 行：一档都不必选。不建文本、不取连接，执行直接返回 0。"));

        var reason = method == BulkWriteMethod.SingleRow
            ? $"{rows} 行按请求逐条删：{rows} 条语句、{rows} 次往返。语义与批量一致（§12.4 的等价性），代价是往返。"
            : $"{rows} 颗键、参数预算 {budget} → 每条 {width} 颗（{trips} 条命令）。一句 IN 清单是单条语句，" +
              "不欠多语句能力，所以这一档在每家都可达（§12.1 写侧扩展）。" + BulkAtomicity.Sentence(_atomic, trips);

        return (method, new BulkWriteReport(requested, method, rows, trips, width, false, reason));
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Build(BulkWriteMethod method)
    {
        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var width = method == BulkWriteMethod.SingleRow ? 1 : StatementEmitter.RowsPerStatement(budget, 1);

        var traces = new List<HookTrace>();
        var step = 0;
        var commands = new List<DialectSql>((_rows.Length + width - 1) / width);

        // 软删在这一档同样生效，而一颗时间要跨过两件事保持不变：DeleteRange 会被切成若干句 IN
        // （各读一次时钟就把同一批行记成几个删除时间），而 Explain() 与紧随的执行是两次构建——
        // 所以时钟挂在引擎上而不是 Build 里：WithBulk 清 _plan 不清它，换档重 build 也还是同一个删除时刻。
        var soft = _orm.Behaviors.HasAny ? _orm.Behaviors.RuleFor(typeof(T)) : null;
        DateTime? stamp = null;

        if (soft is not null)
            stamp = _deletedAt ??= soft.Stamp();

        for (var first = 0; first < _rows.Length; first += width)
        {
            var count = Math.Min(width, _rows.Length - first);

            SqlExpression keys;

            if (method == BulkWriteMethod.SingleRow)
            {
                keys = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, _key),
                    new ParameterValue(ReadKey(first), _key));
            }
            else
            {
                // IN 清单：一颗键值一行，值在参数表里逐颗登记（发射段按 Parameters 顺序落地）。
                var values = new SqlExpression[count];

                for (var row = 0; row < count; row++)
                    values[row] = new ParameterValue(ReadKey(first + row), _key);

                keys = new InExpression(new ColumnExpression(_table, _key), values);
            }

            var where = _orm.Filters.ApplyTo(_table, keys);
            var ast = new DeleteStatement { Table = _table, Where = where };

            var emitted = StatementEmitter.EmitDelete(dialect, ast, soft?.Rewrite(_contract, _table, where, stamp),
                _contract.Table.Name.Length + _table.Alias.Length * 3 + 128);

            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "delete", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var reason = method == BulkWriteMethod.SingleRow
            ? $"{_rows.Length} 行逐条删：{_rows.Length} 条语句、每条一颗 {_key.ColumnName}。"
            : $"{_rows.Length} 颗 {_key.ColumnName}、参数预算 {budget} → 每条 {width} 颗（{commands.Count} 条 IN 语句）。";

        if (soft is not null)
            reason += $" 这一批已被 Behaviors.SoftDelete 改写成 UPDATE 置位（{soft.SetSummary(_contract)}，同一批共用一颗时间）：行没有不在，只是那一格写了 true。";

        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            reason += $" 每条语句的 WHERE 里叠着 {injected} 条全局过滤器（§10.7 的 orm.Filters）——命中范围比键清单窄。";

        // 这条链上的语句数就是命令数：一句 IN 清单是一条命令、SingleRow 档一句一命令，两档同形。
        reason += " " + BulkAtomicity.Sentence(_atomic, commands.Count);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>读一颗键值。null 键当场抛而不是从清单里悄悄省掉：省一颗等于那一行没删而调用方以为删了。</summary>
    object ReadKey(int row)
    {
        var entity = _rows[row] ?? throw new QuickSqlException($"第 {row} 行是 null：批量删除的集合里不能有 null 实体。");
        var value = _schema.GetValue(entity, _keyOrdinal);

        if (value is null)
            throw new QuickSqlException($"第 {row} 行的主键列 {_key.ColumnName} 是 null：按主键删定位不到任何行。");

        return value;
    }

    ExecutionTag Tag() => new("delete", [_contract.Table.Name], DataSourceRouting.ClusterOf(_contract.Table));
}

/// <summary>"这一批提交几次"那一格的话术，两条链共用一个形状（离线册按这句话断言，不许两处各写一遍）。
/// 默认"整批一次提交"的根据不是原子性而是速度：并档把往返并了而没把提交并了，每次提交 flush 一次 redo，
/// 同一台远端实例、同一批 2000 行整行更新实测 6386 ms ∶ 263 ms（裸 ADO 对照组 7419 ∶ 920），
/// 差距全部来自提交次数。判据是语句数而不是命令数：一条语句本身就原子，包它只多付两次往返。</summary>
static class BulkAtomicity
{
    /// <summary>要不要自己开一条事务。两格判据，缺一格就不包：调用方没关那颗开关
    /// （WithBatchTransaction(false) 那条退路是给"要用半批已落地做断点续传"的人留的，不是暗示的路），
    /// 而计划里有两句以上语句（一句本身就原子，包它只多付两次往返）。第三格在执行段：
    /// 环境事务在场时提交归开它的那个人，这一批已经是别人那一批的一部分。</summary>
    public static bool ShouldWrap(bool atomic, int statements) => atomic && statements > 1;

    public static string Sentence(bool atomic, int statements)
        => atomic
            ? statements > 1
                ? $"整批 {statements} 句一次提交：执行段在没有环境事务在场时自开一条事务（§12.4 第 8 项今天的默认，WithBatchTransaction(false) 可关）。"
                : "一条语句本身就原子，不必再包事务。"
            : "每条语句自己提交，半批可以在库里落地（WithBatchTransaction(false)）：要整批原子就自己包 §6 的事务。";
}
