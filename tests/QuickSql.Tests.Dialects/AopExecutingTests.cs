using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Aop;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Values;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§10.1 执行前那一道门（<see cref="QuickSqlAop.Executing"/>）的离线册。
///
/// <para><b>这一格为什么能离线证</b>：要判的不是"驱动把句子送到了库"，是<em>内核在把命令交给驱动之前
/// 有没有先播报一次、播报的是不是同一句、被闸门叫停时是不是闭口不谈</em>。把
/// <see cref="IConnectorFactory"/> 换成一根只记账不连库的桩，这三件事就都在桩的账本上，
/// 而发射点、顺序、<c>Observe</c> 的异常边界一处没被替身顶掉。内层仍是 <see cref="SqlServer"/>
/// 那份真 provider（方言、binder、键策略照抄），换掉的只有 <c>NoDriverFactory</c> 那一条连接出口。</para>
///
/// <para><b>这一册判不动的</b>：真驱动面前的重试与超时（归集成册），以及 §12.8 那条"未订阅零成本"的字节账
/// —— 这里只用一条断言钉住"未订阅时不播报也不报错"这个形状，不量分配。</para></summary>
public sealed class AopExecutingTests
{
    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    static Order Row() => new()
    {
        CustomerName = "客户1",
        CustomerId = 1,
        Amount = 10m,
        State = OrderState.Paid,
        CreatedAt = Moment,
    };

    [Fact]
    public async Task The_statement_is_announced_before_the_driver_sees_it()
    {
        var journal = new List<string>();
        var probe = new SendProbe(journal);

        CommandExecutingEvent? announced = null;

        var orm = BuildOrm(probe, aop: new QuickSqlAop
        {
            Executing = e =>
            {
                journal.Add("Executing");
                announced = e;
            },
            Executed = _ => journal.Add("Executed"),
        });

        var rows = await orm.Insert(Row()).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, rows);

        // 顺序是这一格的全部裁决：播报在驱动之前、回执在驱动之后。
        Assert.Equal(["Executing", "Send", "Executed"], journal);

        // 播报的那一句与发出去的那一句是同一颗 DialectSql：文本与参数都还在原处。
        Assert.NotNull(announced);
        Assert.Equal(probe.SentText, announced.Sql.Text);
        Assert.Equal("insert", announced.OperationKind);
        Assert.Contains("QuickSqlOrder", announced.TableNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_statement_is_never_announced()
    {
        var journal = new List<string>();
        var probe = new SendProbe(journal);

        var orm = BuildOrm(probe, aop: new QuickSqlAop
        {
            CommandTerminating = _ => true,
            Executing = _ => journal.Add("Executing"),
        });

        // 闸门排在播报之前：一句被叫停的话不该在日志里留下一行"正在执行"，而它确实一个字节都没交给驱动。
        await Assert.ThrowsAsync<QuickSqlCommandCancelledException>(
            async () => await orm.Insert(Row()).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Empty(journal);
        Assert.Null(probe.SentText);
    }

    [Fact]
    public async Task A_broken_announcer_cannot_break_the_write()
    {
        var journal = new List<string>();
        var probe = new SendProbe(journal);

        Exception? surfaced = null;

        var orm = BuildOrm(probe, aop: new QuickSqlAop
        {
            Executing = _ => throw new InvalidOperationException("日志代码自己炸了"),
            Error = e =>
            {
                journal.Add("Error");
                surfaced = e.Exception;
            },
        });

        // 硬规则 3：观察类钩子的异常只进 Error，不许盖掉这次写入。
        Assert.Equal(1, await orm.Insert(Row()).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["Error", "Send"], journal);
        Assert.NotNull(surfaced);
        Assert.Equal("日志代码自己炸了", surfaced!.Message);
    }

    [Fact]
    public async Task Nobody_listening_still_sends_the_statement()
    {
        var journal = new List<string>();
        var probe = new SendProbe(journal);

        // 未订阅时这道门只是两次分支预测：句子照发，不因为没人听就少跑一步。
        Assert.Equal(1, await BuildOrm(probe).Insert(Row()).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["Send"], journal);
    }

    static QuickSqlOrm BuildOrm(SendProbe probe, QuickSqlAop? aop = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = new ProbeProvider(SqlServer.Provider(SqlServer.NoDriverFactory.Instance), probe),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop ?? new QuickSqlAop(),
        });

    /// <summary>桩的账本：谁排在前、谁排在后，以及发出去的那句文本。</summary>
    sealed class SendProbe(List<string> journal)
    {
        public string? SentText { get; private set; }

        public void Send(string sql)
        {
            journal.Add("Send");
            SentText = sql;
        }
    }

    /// <summary>只换掉连接出口的那一层：其余能力（方言、类型映射、binder、键策略）逐字转交内层真 provider。</summary>
    sealed class ProbeProvider(IDbProvider inner, SendProbe probe) : IDbProvider
    {
        public DbKind Kind => inner.Kind;
        public DbCapabilities Capabilities => inner.Capabilities;
        public IConnectorFactory Connectors { get; } = new ProbeConnectors(probe);
        public ISqlDialect Dialect => inner.Dialect;
        public ITypeMap Types => inner.Types;
        public IValueBinder Values => inner.Values;
        public IExpressionRules Expressions => inner.Expressions;
        public ICodeFirstDialect CodeFirst => inner.CodeFirst;
        public ISchemaReader Schema => inner.Schema;
        public IKeyStrategy Keys => inner.Keys;
        public IConcurrencyStrategy Concurrency => inner.Concurrency;
        public IChangeGuard ChangeGuard => inner.ChangeGuard;
        public IBulkStrategy? Bulk => inner.Bulk;
    }

    sealed class ProbeConnectors(SendProbe probe) : IConnectorFactory
    {
        public DbConnection CreateConnection(string connectionString) => new ProbeConnection(probe);

        public DbCommand CreateCommand(DbConnection connection) => new ProbeCommand(probe, connection);

        public bool TryEnsureLoaded(out string? failure)
        {
            failure = null;
            return true;
        }
    }

    sealed class ProbeConnection(SendProbe probe) : DbConnection
    {
        ConnectionState _state = ConnectionState.Closed;

        [AllowNull] public override string ConnectionString { get; set; } = SqlServer.OfflineConnectionString;
        public override string Database => "aop-probe-db";
        public override string DataSource => "probe";
        public override string ServerVersion => "0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new ProbeCommand(probe, this);
    }

    /// <summary>只认 <c>ExecuteNonQueryAsync</c> 这一条出口：撞上别的形状就说明写路径换了出口，那是要显式承认的变更。</summary>
    sealed class ProbeCommand(SendProbe probe, DbConnection? connection) : DbCommand
    {
        DbParameterCollection? _parameters;

        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get => connection; set => _ = value; }
        protected override DbParameterCollection DbParameterCollection => _parameters ??= new ProbeParameters();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new ProbeParameter();

        public override int ExecuteNonQuery() => throw NotInThisBook(nameof(ExecuteNonQuery));
        public override object? ExecuteScalar() => throw NotInThisBook(nameof(ExecuteScalar));

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw NotInThisBook(nameof(ExecuteDbDataReader));

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            probe.Send(CommandText);

            return Task.FromResult(1);
        }

        static NotSupportedException NotInThisBook(string member)
            => new($"{member} 不由这一册作证：插入链只走异步的 non-query 出口，撞上它就说明这条链换了形状。");
    }

    sealed class ProbeParameter : DbParameter
    {
        public override DbType DbType { get; set; } = DbType.Object;
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }

        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }

        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }

        public override void ResetDbType() => DbType = DbType.Object;
    }

    /// <summary>参数集合的最低实现：执行层只往上添，不按下标取，所以这一颗只是让 <c>Add</c> 有个去处。</summary>
    sealed class ProbeParameters : DbParameterCollection
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
                _items.Add((DbParameter) value!);
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains(value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => _items.ToArray().CopyTo(array, index);

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
}
