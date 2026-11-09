using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace QuickSql.Benchmarks.Offline;

/// <summary>基准用的假驱动：连接/命令都是空壳，<c>ExecuteDbDataReader</c> 直接吐一张预加载的
/// <see cref="DataTableReader"/>。<b>SQL 文本不参与执行</b>（假驱动没有查询处理器），所以这套数字量的是
/// "QuickSql 的物化与编排 + 一个不干活的数据源"，对应 §12.8 第一行括号里那句"不含 ADO 驱动自身开销"。
/// 三件事因此被排除在测量之外，报告里必须一起说明：</summary>
/// <remarks>
/// ① 网络与 TDS 解析；② 真驱动每次读 <c>string</c> 都新建对象，而 <see cref="DataTableReader"/> 返回的是
/// 表里那一个实例 —— 所以每行字节数是<b>乐观下界</b>，换真驱动只会更红；③ <c>WHERE</c>/<c>OFFSET</c> 不生效，
/// 行数由这里灌进去多少决定。要拿到带真驱动的数，只能把这几个基准搬到活库上跑，那是另一片（§14 记着）。
/// <para><b>④ 本版起还有一件：事务</b>。<see cref="OfflineConnection.BeginDbTransaction"/> 从 v0.27 之前那句
/// "基准路径只读"的抛错换成了桩（第 11 行要量 <c>SaveChanges</c>，而它有写必开事务），于是锁、日志、提交往返
/// 这三样也不在测量之内 —— 写侧那两格（第 6 行与第 11 行）量的都是"QuickSql 把一条语句搭出来并交出去"的开销，
/// 不是"库收下它"的开销。</para>
/// <para><b>计数器为什么挂静态槽位而不是挂构造参数</b>：这一版最初把 <see cref="WriteCounters"/> 顺着
/// factory → connection → command 传下去，而 connection 与 command 都在被测量的窗口里 —— 多一个引用字段
/// 就是每只对象 +8 B。v28 头两场（<c>v28-gate-warm.log</c>、<c>v28-gate-2.log</c>，两场分配逐字相同）
/// 因此报了四笔回归，其中两笔根本不是第 11 行的：<b>第 1 行 5 464 → 5 472</b>（截距 6 872 → 6 896 是 +24，
/// 同形 ADO 基线 1 408 → 1 424 是 +16，相减 +8 = 一次查询的一只连接加两只命令，而基线那条链是"一只连接一只命令"），
/// <b>第 6 行 ① 8 599 832 → 8 599 960</b>（全路径 +384、基线 +256，即 16 块 × 同一个形状）。两笔的每一项都是 8 的整数倍，
/// 且第 6 行的 ②（纯构建段，不连库）与第 3 / 4 / 5 / 10 行七格一字未动 —— 那正好是"钱在连接与命令对象上"的形状。
/// 挂到槽位上之后连接与命令的字段表回到 v0.27 的样子，被测量对象图里不再有为量具添的东西。
/// <b>代价要说清</b>：槽位是全局可变的，所以它只在第 11 行的 <c>Save()</c> 期间挂上、退出时摘下。
/// v0.28 那一版它是 <c>Collect()</c> 的最后一行，"后面没有别的行会被它污染"当时只是顺带成立；本版在它后面
/// 挂了第 12 行（读侧物化链，一次都不写），于是这一条<em>第一次真的由 <c>Gates.Save</c> 的 <c>finally</c> 兜住</em>，
/// 不再靠"恰好排在最后"。</para>
/// <para>为什么不用现成的内存库：SQLite 驱动会把"provider 无关"这件事请出基准现场，而这里要的恰恰是
/// QuickSql 自己的编排开销。列名与列序由 <c>ExpectedColumns</c> 校验，所以"SQL 改了但假结果集没跟上"
/// 会变成一次抛错而不是一个看起来正常的数字。</para>
/// </remarks>
sealed class OfflineFactory(Func<string, DataTable?> resolve) : DbProviderFactory
{
    /// <summary>数据源那侧的计数槽位，<b>只在 §12.8 第 11 行的测量窗口里非 null</b>（挂/摘的地点与理由见类注释④）。
    /// 读写都不分配托管对象，所以"挂着槽位"这件事本身不改任何一格的字节数；会改字节数的是把它做成字段。</summary>
    public static WriteCounters? Counters;

    public override DbConnection CreateConnection() => new OfflineConnection(resolve);
}

/// <summary>数据源那侧的两个计数：<b>发过几条不返回结果集的语句</b>、<b>开过几条事务</b>。
/// 为什么不读 QuickSql 自报的那个数：§12.5 第 2 条 ③ 明写两档的 <c>Rows</c> 是<em>两种口径</em> ——
/// 合并档那个数是<em>语句数</em>，逐条档那个数是<em>行数</em>，于是拿它去证"合并档合成了一条"是循环论证
/// （它本来就是被定义成那样的）。从数据源那侧数才是独立见证。
/// <para>事务那一半为什么也要数：合并档省下的是<em>往返</em>，而 <c>DbContext.SaveChangesAsync</c>
/// 一次保存只开<em>一条</em>事务，两档都一样 —— 不报这一位，读到"语句从 13 变 1"的人会以为事务也少了 13 倍。
/// 两个数一起报才是这一格的形状：语句少一个量级、事务一条不少。</para></summary>
sealed class WriteCounters
{
    public int Statements;

    public int Transactions;

    public void Reset()
    {
        Statements = 0;
        Transactions = 0;
    }
}

sealed class OfflineConnection(Func<string, DataTable?> resolve) : DbConnection
{
    ConnectionState _state = ConnectionState.Closed;

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "offline";

    public override string DataSource => "offline";

    public override string ServerVersion => "0.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Open() => _state = ConnectionState.Open;

    public override void Close() => _state = ConnectionState.Closed;

    /// <summary>v0.27 之前这里直接抛「假驱动没有事务：基准路径只读。」—— 那是读侧基准的自述，
    /// 而 §12.8 第 6 行（批量插入）走的是 <c>InsertEngine</c>，不碰事务，所以那句一活就是六版。
    /// 第 11 行要量 <c>SaveChanges</c>，而它<em>有写就必开事务</em>（<c>DbContext.SaveChangesAsync</c>
    /// 那句 <c>using var tran = Orm.BeginTransaction()</c>），于是这一抛正好挡在本格前面。
    /// 换成桩而不是绕开事务：绕开要改 <c>src/</c>（本版不动它），而"保存开一条事务"本身就是被量那一段的形状。
    /// <para><b>它给不了的东西要说清</b>：锁、日志、往返、回滚之后的数据，全都不存在 —— 这一格量的仍然只是
    /// QuickSql 侧的编排，与类注释那句"不含 ADO 驱动自身开销"同一条边界，只是现在连"事务"这一项也在边界外。
    /// 也不校验隔离级别与嵌套：传进来什么就存什么，所以"传错隔离级别"在这里不会被抓（那不是本格的判据）。</para>
    /// <para>计数走 <see cref="OfflineFactory.Counters"/> 那颗槽位而不是走字段 —— 连接对象每查询一只，
    /// 给它加字段就是给第 1 行与第 6 行各添一笔量具的钱（实测与算法见类注释④）。</para></summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (OfflineFactory.Counters is { } counters)
            counters.Transactions++;

        return new OfflineTransaction(this, isolationLevel);
    }

    protected override DbCommand CreateDbCommand() => new OfflineCommand(this, resolve);
}

/// <summary>事务桩：只要 <c>QuickSqlTransaction</c> 会调到的那几个成员存在且不抛（<c>Commit</c> /
/// <c>Rollback</c> / 连接与隔离级别两个读点）。提交与回滚都没有可撤的东西 —— 见
/// <see cref="OfflineConnection.BeginDbTransaction"/> 那段边界。<b>不留"提交过没有"这一位</b>：
/// 留了就得有人读，而第 11 行的判据是语句条数与字节，事务那一半由 <see cref="WriteCounters.Transactions"/>
/// 从开囗那侧数（一次保存几条事务），比在这颗桩上记布尔更直接。</summary>
sealed class OfflineTransaction(OfflineConnection connection, IsolationLevel level) : DbTransaction
{
    protected override DbConnection DbConnection => connection;

    public override IsolationLevel IsolationLevel => level;

    public override void Commit() { }

    public override void Rollback() { }
}

sealed class OfflineCommand(OfflineConnection connection, Func<string, DataTable?> resolve) : DbCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; } = connection;

    protected override DbParameterCollection DbParameterCollection { get; } = new OfflineParameterCollection();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }

    public override void Prepare() { }

    /// <summary>写基准（§12.8 第 6 行，以及本版新添的第 11 行）要的只是"这条语句走完了执行层"，所以这里返回一个占位数。
    /// <b>它不是受影响行数</b>：假驱动没有存储，真正的行数在集成测试那边按库里的数据证（§13 的写侧用例）。
    /// 这一格留 1 而不是抛错的代价是"读侧基准里混进写"这件事不再会被本方法抓到 ——
    /// 换来的是第 6 行与第 11 行有可测对象，而读侧那几行的 SQL 文本本来也不参与执行（见类注释）。
    /// <para>第 11 行还顺手要一个"发过几条语句"的数（<see cref="WriteCounters"/>）：只有槽位挂着计数器的那段窗口
    /// 会走 <c>Statements++</c>，其余九行跑到这一句时槽位是 null，于是一次字段读加一次跳转 —— 这一格钉的是字节，
    /// 而读一个引用字段不分配托管对象。<b>不要把它读成"本版没给量具添钱"</b>：添过，最初那一版是加在连接与命令的
    /// 字段上，那才是真给第 1 / 6 行添钱的形状，四笔回归的账与改法见类注释④。</para></summary>
    public override int ExecuteNonQuery()
    {
        if (OfflineFactory.Counters is { } counters)
            counters.Statements++;

        return 1;
    }

    public override object? ExecuteScalar()
        => throw new NotSupportedException("假驱动只产结果集：标量出口要另配一条结果表。");

    protected override DbParameter CreateDbParameter() => new OfflineParameter();

    /// <summary>每次调用新建 reader：一次查询一个 reader 是真驱动的契约，复用会让"per-query 分配"这一格少算一个对象。</summary>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var table = resolve(CommandText);

        if (table is null)
            throw new InvalidOperationException($"假驱动没有为这条 SQL 准备结果集：{CommandText}");

        return table.CreateDataReader();
    }
}

sealed class OfflineParameter : DbParameter
{
    public override DbType DbType { get; set; } = DbType.Object;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}

sealed class OfflineParameterCollection : DbParameterCollection
{
    readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _items.Add((DbParameter) value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
            Add(value!);
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains(value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection) _items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((DbParameter) value);

    public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);

    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter) value);

    public override void Remove(object value) => _items.Remove((DbParameter) value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}
