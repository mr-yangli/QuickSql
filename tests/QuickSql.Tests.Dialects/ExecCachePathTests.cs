using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Values;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>写侧执行路文本缓存（v11.0.0 第二笔）的跨家离线钉。活库只有五族有 harness，
/// 其余各家执行路（缓存命中 + 直登记 facts + 位置形 namer + 钩子物化）此前没有证人——这一册用
/// 只记账不连库的桩把驱动出口换掉，内核发射/缓存/绑定一处不替，十九家逐家过。
///
/// <para>钉三件事：① 同形两批文本逐字相同（缓存命中）而每批参数值各归其批（facts 直登记不串批）；
/// ② CudBuilding 订阅时绕缓存走全量（钩子真 fired）；③ ParameterBinding 改写在缓存路上仍 reaches 驱动
/// （懒物化 + 改写回贴那一段没有因为缓存而丢）。判不动的：真驱动面前的重试/超时/回填回包（归集成册）。</para></summary>
public sealed class ExecCachePathTests
{
    static readonly DateTime Moment = new(2026, 3, 4, 5, 6, 7, 890);

    static Order[] Rows(int batch) =>
    [
        .. Enumerable.Range(1, 5).Select(i => new Order
        {
            CustomerName = $"b{batch}客户{i}",
            CustomerId = i,
            Amount = i + batch * 1000m,
            State = i % 2 == 0 ? OrderState.Paid : OrderState.Cancelled,
            CreatedAt = Moment,
            PaidAt = null,
            Note = $"b{batch}注{i}",
        })
    ];

    static object? Cell(Order row, string member) => member switch
    {
        nameof(Order.CustomerName) => row.CustomerName,
        nameof(Order.CustomerId) => row.CustomerId,
        nameof(Order.Amount) => row.Amount,
        nameof(Order.State) => row.State,
        nameof(Order.CreatedAt) => row.CreatedAt,
        nameof(Order.PaidAt) => row.PaidAt,
        nameof(Order.Note) => row.Note,
        _ => throw new NotSupportedException(member),
    };

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("kingbase")]
    [InlineData("opengauss")]
    [InlineData("gaussdb")]
    [InlineData("highgo")]
    [InlineData("gbase8c")]
    [InlineData("duckdb")]
    [InlineData("mysql")]
    [InlineData("tidb")]
    [InlineData("oceanbase")]
    [InlineData("oracle")]
    [InlineData("dameng")]
    [InlineData("shentong")]
    [InlineData("gbase8s")]
    [InlineData("tdengine")]
    [InlineData("xugu")]
    [InlineData("sqlite")]
    [InlineData("clickhouse")]
    public async Task Exec_cache_reuses_text_across_batches_and_binds_each_batch_own_values(string name)
    {
        var probe = new RecordingProbe();
        var orm = BuildOrm(name, probe);
        var columns = orm.Schemas.Contract<Order>().Columns.Where(static c => !c.IsIdentity).ToArray();
        var binder = orm.Provider.Values;

        for (var batch = 0; batch < 2; batch++)
            Assert.Equal(5, await orm.InsertRange(Rows(batch)).WithBulk(BulkMethod.MultiValue).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, probe.Sends.Count);

        // ① 缓存命中：两批文本逐字相同。
        Assert.Equal(probe.Sends[0].Text, probe.Sends[1].Text);

        // ② 每批参数值各归其批：名字/型别/宽度序列相同，值按该批 Cells 归一后逐颗对。
        Assert.Equal(probe.Sends[0].Params.Count, probe.Sends[1].Params.Count);
        Assert.Equal(5 * columns.Length, probe.Sends[0].Params.Count);

        for (var batch = 0; batch < 2; batch++)
        {
            var rows = Rows(batch);
            var at = 0;

            for (var row = 0; row < rows.Length; row++)
                for (var col = 0; col < columns.Length; col++)
                {
                    var sent = probe.Sends[batch].Params[at];
                    var expected = binder.FactsOf(sent.Name, Cell(rows[row], columns[col].MemberName), columns[col]);

                    Assert.Equal(expected.Value, sent.Value);
                    // Bind 对 DbType.Object 有意不表态（硬设会让部分驱动连非 DBNull 也拒），
                    // 桩参数留枚举默认值——对拍要对「Bind 会设成的值」而不是 facts 原值。
                    Assert.Equal(expected.DbType == DbType.Object ? default(DbType) : expected.DbType, sent.DbType);
                    Assert.Equal(expected.Size, sent.Size);
                    at++;
                }
        }

        // 名字序列两批同源（编号 = 发射序 = 绑定序）。
        Assert.Equal(probe.Sends[0].Params.Select(static p => p.Name), probe.Sends[1].Params.Select(static p => p.Name));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("gbase8s")]
    [InlineData("tdengine")]
    [InlineData("xugu")]
    [InlineData("sqlite")]
    [InlineData("clickhouse")]
    public async Task Exec_cache_is_bypassed_when_CudBuilding_is_subscribed(string name)
    {
        var probe = new RecordingProbe();
        var fired = 0;
        var orm = BuildOrm(name, probe, new QuickSqlAop { CudBuilding = _ => fired++ });

        for (var batch = 0; batch < 2; batch++)
            Assert.Equal(5, await orm.InsertRange(Rows(batch)).WithBulk(BulkMethod.MultiValue).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        Assert.True(fired >= 2, "CudBuilding 订阅时执行路必须走全量发射（钩子要看得见真句子）。");
        Assert.Equal(2, probe.Sends.Count);
        Assert.Equal(probe.Sends[0].Text, probe.Sends[1].Text);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("gbase8s")]
    [InlineData("tdengine")]
    [InlineData("xugu")]
    [InlineData("sqlite")]
    public async Task ParameterBinding_rewrite_reaches_the_driver_on_the_cache_path(string name)
    {
        var probe = new RecordingProbe();
        var orm = BuildOrm(name, probe, new QuickSqlAop
        {
            ParameterBinding = e =>
            {
                if (e.Column?.MemberName == nameof(Order.Amount))
                    e.SetValue(999m);
            },
        });

        Assert.Equal(5, await orm.InsertRange(Rows(0)).WithBulk(BulkMethod.MultiValue).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, await orm.InsertRange(Rows(1)).WithBulk(BulkMethod.MultiValue).ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 改写 reaches 驱动：两批的 Amount 颗都是 999（第二批走缓存路，物化+回贴不能丢）。
        foreach (var send in probe.Sends)
            Assert.Equal(5, send.Params.Count(static p => Equals(p.Value, 999m)));
    }

    static QuickSqlOrm BuildOrm(string name, RecordingProbe probe, QuickSqlAop? aop = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = new ProbeProvider(ProviderOf(name), probe),
            ConnectionString = SqlServer.OfflineConnectionString,
            Aop = aop ?? new QuickSqlAop(),
        });

    static IDbProvider ProviderOf(string name) => name switch
    {
        "sqlserver" => SqlServer.Provider(SqlServer.NoDriverFactory.Instance),
        "postgresql" => PostgreSql.Provider(SqlServer.NoDriverFactory.Instance),
        "kingbase" => Kingbase.Provider(SqlServer.NoDriverFactory.Instance),
        "opengauss" => OpenGauss.Provider(SqlServer.NoDriverFactory.Instance),
        "gaussdb" => GaussDb.Provider(SqlServer.NoDriverFactory.Instance),
        "highgo" => Highgo.Provider(SqlServer.NoDriverFactory.Instance),
        "gbase8c" => Gbase8c.Provider(SqlServer.NoDriverFactory.Instance),
        "duckdb" => DuckDb.Provider(SqlServer.NoDriverFactory.Instance),
        "mysql" => MySql.Provider(SqlServer.NoDriverFactory.Instance),
        "tidb" => TiDb.Provider(SqlServer.NoDriverFactory.Instance),
        "oceanbase" => OceanBase.Provider(SqlServer.NoDriverFactory.Instance),
        "oracle" => Oracle.Provider(SqlServer.NoDriverFactory.Instance),
        "dameng" => Dameng.Provider(SqlServer.NoDriverFactory.Instance),
        "shentong" => ShenTong.Provider(SqlServer.NoDriverFactory.Instance),
        "gbase8s" => Gbase8s.Provider(SqlServer.NoDriverFactory.Instance),
        "tdengine" => TDengine.Provider(SqlServer.NoDriverFactory.Instance),
        "xugu" => Xugu.Provider(SqlServer.NoDriverFactory.Instance),
        "sqlite" => Sqlite.Provider(SqlServer.NoDriverFactory.Instance),
        "clickhouse" => ClickHouse.Provider(SqlServer.NoDriverFactory.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "没有这一家的离线组装根"),
    };

    sealed class RecordingProbe
    {
        public List<(string Text, List<(string Name, object? Value, DbType DbType, int Size)> Params)> Sends { get; } = [];
    }

    sealed class ProbeProvider(IDbProvider inner, RecordingProbe probe) : IDbProvider
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
        public bool CanCreateDatabase => inner.CanCreateDatabase;
    }

    sealed class ProbeConnectors(RecordingProbe probe) : IConnectorFactory
    {
        public DbConnection CreateConnection(string connectionString) => new ProbeConnection(probe);
        public DbCommand CreateCommand(DbConnection connection) => new ProbeCommand(probe, connection);
        public bool TryEnsureLoaded(out string? failure)
        {
            failure = null;
            return true;
        }
    }

    sealed class ProbeConnection(RecordingProbe probe) : DbConnection
    {
        ConnectionState _state = ConnectionState.Closed;

        [AllowNull] public override string ConnectionString { get; set; } = SqlServer.OfflineConnectionString;
        public override string Database => "exec-cache-probe";
        public override string DataSource => "probe";
        public override string ServerVersion => "0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new ProbeTransaction();
        protected override DbCommand CreateDbCommand() => new ProbeCommand(probe, this);
    }

    sealed class ProbeTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }

    sealed class ProbeCommand(RecordingProbe probe, DbConnection? connection) : DbCommand
    {
        ProbeParameters? _parameters;

        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get => connection; set => _ = value; }
        protected override DbParameterCollection DbParameterCollection => _parameters ??= new ProbeParameters();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new ProbeParameter();

        public override int ExecuteNonQuery() => Record();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(Record());

        int Record()
        {
            var list = (_parameters ??= new ProbeParameters()).Items
                .Select(static p => (p.ParameterName, p.Value, p.DbType, p.Size))
                .ToList();

            probe.Sends.Add((CommandText, list));

            // 本册固定单块 5 行形状（各家块宽最小 12 行），桩交回块行数 5；多块/回填形状归集成册与门禁。
            return 5;
        }
    }

    sealed class ProbeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    sealed class ProbeParameters : DbParameterCollection
    {
        public List<ProbeParameter> Items { get; } = [];

        public override int Count => Items.Count;
        public override object SyncRoot => Items;

        public override int Add(object value)
        {
            Items.Add((ProbeParameter)value);
            return Items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var v in values) Add(v!);
        }

        public override void Clear() => Items.Clear();
        public override bool Contains(object value) => Items.Contains((ProbeParameter)value);
        public override bool Contains(string value) => Items.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((IList)Items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => Items.GetEnumerator();
        public override int IndexOf(object value) => Items.IndexOf((ProbeParameter)value);
        public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => Items.Insert(index, (ProbeParameter)value);
        public override void Remove(object value) => Items.Remove((ProbeParameter)value);
        public override void RemoveAt(int index) => Items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => Items[index];
        protected override DbParameter GetParameter(string parameterName) => Items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => Items[index] = (ProbeParameter)value;
        protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = (ProbeParameter)value;
    }
}