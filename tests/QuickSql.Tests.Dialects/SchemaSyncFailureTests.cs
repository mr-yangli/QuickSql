using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Core.Schema;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Values;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>Execute 档里那一句 DDL 被库拒绝时要报出什么（§9.5 的失败形状）。
///
/// <para><b>为什么这一册可以碰"连接"而仍然离线</b>：§13.1 挡住的是"只有连得上真库才测得动的分支"，
/// 而这里要证的不是库为什么拒（方言与权限的事，归集成册），是<em>被拒之后内核报回来的那一颗说不说得出
/// "哪一句、前面已经落库了哪几句"</em>。这一格不连库也能证：把 <see cref="IConnectorFactory"/>
/// 换成一根只认文本标记的桩，拒绝是假的，包装拒绝的那一段是真的。</para>
///
/// <para>桩不是驱动：内层仍是 <see cref="SqlServer"/> 那份真 provider（方言、类型映射、建表发射照抄），
/// 只是它那条 <c>NoDriverFactory</c> 被换掉了 —— 换掉它不是"替身没配好"，而是这一格唯一能把失败
/// 送进 <c>ExecuteDdlAsync</c> 的入口。两张表的先后 = 注册的先后，而同步那一趟本就按这个次序发。</para></summary>
public sealed class SchemaSyncFailureTests
{
    const string Boom = "SYNC_BOOM";

    [Table("SYNC_OK")]
    sealed class OkEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Table(Boom)]
    sealed class BoomEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Table("SYNC_CANCEL")]
    sealed class CancelEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    // 截断那一格要 6 句已生效（上限 5），一句失败的 —— 七张一样的表，只有一颗列就够。
    [Table("CAP_1")] sealed class Cap1 { public int Id { get; set; } }
    [Table("CAP_2")] sealed class Cap2 { public int Id { get; set; } }
    [Table("CAP_3")] sealed class Cap3 { public int Id { get; set; } }
    [Table("CAP_4")] sealed class Cap4 { public int Id { get; set; } }
    [Table("CAP_5")] sealed class Cap5 { public int Id { get; set; } }
    [Table("CAP_6")] sealed class Cap6 { public int Id { get; set; } }
    [Table("CAP_7")] sealed class Cap7 { public int Id { get; set; } }

    static readonly Type[] Capped = [typeof(Cap1), typeof(Cap2), typeof(Cap3), typeof(Cap4), typeof(Cap5), typeof(Cap6), typeof(Cap7)];

    [Fact]
    public async Task Failing_Statement_Is_Named_With_What_Already_Applied()
    {
        var probe = new DdlProbe(failMarker: Boom);
        var orm = BuildOrm(probe);

        _ = orm.Schemas.Contract(typeof(OkEntity));
        _ = orm.Schemas.Contract(typeof(BoomEntity));

        var ex = await Assert.ThrowsAsync<SchemaSyncFailedException>(() => orm.SyncStructureAsync(TestContext.Current.CancellationToken));

        Assert.Equal(Boom, ex.Target);
        Assert.Equal("CreateTable", ex.ChangeKind);
        Assert.Contains(Boom, ex.Sql, StringComparison.Ordinal);

        // 上一句已经落库了：这一趟不包事务，而 MySQL 那一族本来也回滚不了 DDL，
        // 所以"失败在哪"与"库里现在是什么形状"是同一件事的两半，缺一半都修不了。
        Assert.Single(ex.AppliedStatements);
        Assert.Contains("SYNC_OK", ex.AppliedStatements[0], StringComparison.Ordinal);
        Assert.DoesNotContain(ex.Sql, ex.AppliedStatements, StringComparer.Ordinal);
        Assert.Equal("sync-probe-db", ex.Database);

        // 消息要能独立读：日志里往往只有这一串，拿不到属性的人也要看得见是哪一句。
        Assert.Contains("结构同步失败", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SYNC_OK", ex.Message, StringComparison.Ordinal);
        Assert.Contains("已经生效的 1 句", ex.Message, StringComparison.Ordinal);
        Assert.IsType<DdlRejectedException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancelled_Send_Keeps_Its_Own_Exception()
    {
        var probe = new DdlProbe(cancelMarker: "SYNC_CANCEL");
        var orm = BuildOrm(probe);

        _ = orm.Schemas.Contract(typeof(CancelEntity));

        // 取消不是库的拒绝：包一层只会让人以为"这一句被拒了"，而真问题是这一趟被叫停。
        await Assert.ThrowsAsync<OperationCanceledException>(() => orm.SyncStructureAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Applied_List_Is_Capped_While_The_Count_Stays_Truthful()
    {
        const int sentBeforeFailure = 6;

        // 按"第几句"判而不是按表名判：七张表交出的都是同一种句子，谁排第七不重要，
        // 而这一格要钉的是"清单截断、首行报数不截断"。
        var probe = new DdlProbe(failOnNth: sentBeforeFailure + 1);
        var orm = BuildOrm(probe);

        foreach (var type in Capped)
            _ = orm.Schemas.Contract(type);

        var ex = await Assert.ThrowsAsync<SchemaSyncFailedException>(() => orm.SyncStructureAsync(TestContext.Current.CancellationToken));

        Assert.Equal(sentBeforeFailure, ex.AppliedStatements.Count);
        Assert.StartsWith("CREATE TABLE", ex.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"已经生效的 {sentBeforeFailure} 句", ex.Message, StringComparison.Ordinal);
        Assert.Contains("另有 1 句", ex.Message, StringComparison.Ordinal);

        for (var i = 0; i < SchemaSyncFailedException.ListedAppliedLimit; i++)
            Assert.Contains($"  {i + 1}. ", ex.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("  6. ", ex.Message, StringComparison.Ordinal);
    }

    static QuickSqlOrm BuildOrm(DdlProbe probe)
        => new(new QuickSqlOrmOptions
        {
            Provider = new ProbeSchemaProvider(SqlServer.Provider(SqlServer.NoDriverFactory.Instance), probe),
            ConnectionString = "Server=probe;Database=sync-fail;",
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

    /// <summary>只作证失败那一格的桩：句子文本进台账，探针说拒就拒、说收就收。</summary>
    sealed class DdlProbe(string? failMarker = null, string? cancelMarker = null, int? failOnNth = null)
    {
        public List<string> Sent { get; } = [];

        public Exception? Reject(string sql)
        {
            if (cancelMarker is not null && sql.Contains(cancelMarker, StringComparison.Ordinal))
                return new OperationCanceledException(sql);

            if (failMarker is not null && sql.Contains(failMarker, StringComparison.Ordinal))
                return new DdlRejectedException(sql);

            if (failOnNth is { } nth && Sent.Count == nth)
                return new DdlRejectedException(sql);

            return null;
        }
    }

    /// <summary>替 <c>MySqlException</c> 站位：驱动那一句原话在真库上由方言给，这里给不了。</summary>
    sealed class DdlRejectedException(string sql) : Exception($"库拒绝了这一句：{sql}");

    sealed class ProbeSchemaProvider(IDbProvider inner, DdlProbe probe) : IDbProvider
    {
        public DbKind Kind => inner.Kind;
        public DbCapabilities Capabilities => inner.Capabilities;
        public IConnectorFactory Connectors { get; } = new ProbeConnectors(probe);
        public ISqlDialect Dialect => inner.Dialect;
        public ITypeMap Types => inner.Types;
        public IValueBinder Values => inner.Values;
        public IExpressionRules Expressions => inner.Expressions;
        public ICodeFirstDialect CodeFirst => inner.CodeFirst;
        public ISchemaReader Schema { get; } = new EverythingMissing();
        public IKeyStrategy Keys => inner.Keys;
        public IConcurrencyStrategy Concurrency => inner.Concurrency;
        public IChangeGuard ChangeGuard => inner.ChangeGuard;
        public IBulkStrategy? Bulk => inner.Bulk;

        // 建库那一步与这一格要证的失败无关，而它读的是连接串里的库名：不开这一格，
        // 桩就得再装一份"能被建库逻辑读"的连接。
        public bool CanCreateDatabase => false;
    }

    /// <summary>库里一张表都没有：让每张都走建表那条路，失败才有机会落在 <c>CREATE TABLE</c> 上。</summary>
    sealed class EverythingMissing : ISchemaReader
    {
        public Task<SchemaTable?> ReadTableAsync(DbConnection connection, QuickSql.Metadata.TableInfo table, CancellationToken ct = default)
            => Task.FromResult<SchemaTable?>(null);

        public Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(
            DbConnection connection, QuickSql.Metadata.TableInfo table, string columnName, CancellationToken ct = default)
            => Task.FromResult<LengthSemanticsReading?>(null);
    }

    sealed class ProbeConnectors(DdlProbe probe) : IConnectorFactory
    {
        public DbConnection CreateConnection(string connectionString) => new ProbeConnection(probe);

        public DbCommand CreateCommand(DbConnection connection) => new ProbeCommand(probe, connection);

        public bool TryEnsureLoaded(out string? failure)
        {
            failure = null;
            return true;
        }
    }

    sealed class ProbeConnection(DdlProbe probe) : DbConnection
    {
        ConnectionState _state = ConnectionState.Closed;

        [AllowNull] public override string ConnectionString { get; set; } = "Server=probe;Database=sync-fail;";
        public override string Database => "sync-probe-db";
        public override string DataSource => "probe";
        public override string ServerVersion => "0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new ProbeCommand(probe, this);
    }

    sealed class ProbeCommand : DbCommand
    {
        readonly DdlProbe _probe;
        DbConnection? _connection;

        public ProbeCommand(DdlProbe probe, DbConnection? connection)
        {
            _probe = probe;
            _connection = connection;
        }

        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get => _connection; set => _connection = value; }
        protected override DbParameterCollection DbParameterCollection => null!;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        public override int ExecuteNonQuery() => throw NotInThisBook(nameof(ExecuteNonQuery));
        public override object? ExecuteScalar() => throw NotInThisBook(nameof(ExecuteScalar));

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw NotInThisBook(nameof(ExecuteDbDataReader));

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _probe.Sent.Add(CommandText);

            return _probe.Reject(CommandText) is { } rejection
                ? Task.FromException<int>(rejection)
                : Task.FromResult(1);
        }

        static NotSupportedException NotInThisBook(string member)
            => new($"{member} 不由这一册作证：结构同步只走异步那一条出口，撞上它就说明调用形状变了。");
    }
}
