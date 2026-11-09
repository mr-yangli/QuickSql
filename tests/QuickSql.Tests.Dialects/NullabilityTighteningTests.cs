using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Core.Schema;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Providers.PostgreSql;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Values;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>§9.5 里"把一列收紧成 <c>NOT NULL</c>"那一句 <c>ALTER</c> 在执行层的三臂：库里<em>有</em>空值就整句不发、
/// <em>没有</em>就照发、这一家<em>答不出</em>就照发并欠一行"判不了"。
///
/// <para><b>这一格也离线</b>，手法与 <see cref="SchemaSyncFailureTests"/> 同一对：内层是 <see cref="SqlServer"/>
/// 那份真 provider（方言、类型映射、DDL 发射照抄；两臂孪生时换成 <c>PostgreSql</c> 那一族——23502 的
/// 事故现场就在这一家，<c>SET NOT NULL</c> 的字面要在这里钉），换掉的只有两处——连接是一根只记文本的桩，
/// 目录读数由用例手摆。于是"库里有没有空值"这一问的答案是<em>用例给的</em>，而被问的那一句是不是真的一句都没发出去，
/// 量的是内核自己的清单。</para>
///
/// <para><b>为什么不连真库也算证据</b>：§13.1 挡住的是"只有连得上真库才测得动的分支"，而这里要证的不是
/// 库为什么数得出空值（那是各家 <c>SELECT</c> 的事，归各家 reader 与集成册），是内核拿到三态之后
/// <em>发还是不发</em>。那一判只有三个出口，加上两臂"根本不该问"，全部在这册里各有读数。</para></summary>
public sealed class NullabilityTighteningTests
{
    /// <summary>契约里 <c>Title</c> 非空、<c>Note</c> 可空：前者配库里那句"可空"是收紧，后者配库里那句
    /// "非空"是放宽。两列各钉一个方向。</summary>
    [Table("TIGHT_PROBE")]
    sealed class Probe
    {
        public int Id { get; set; }

        [QuickColumn(Length = 32)]
        public string Title { get; set; } = null!;

        [QuickColumn(Length = 32)]
        public string? Note { get; set; }
    }

    /// <summary>第一臂：库说"这一列有空值"。<b>那一句 <c>ALTER</c> 一个字都不许发</b>，而被挡的那一列要进
    /// 结果清单并给出路（回填完，下一趟自己收紧）。这一格红一次就是宿主起不来那一场的复发。</summary>
    [Fact]
    public async Task Dirty_store_keeps_the_tightening_statement_back_and_names_it()
    {
        var probe = new SyncProbe(nulls: true);

        var result = await RunAsync(probe);

        Assert.Empty(probe.Sent);
        Assert.False(result.Executed);
        Assert.Empty(result.AlteredColumns);
        Assert.Single(result.SkippedNullabilityTightening);
        Assert.Contains("TIGHT_PROBE.Title", result.SkippedNullabilityTightening[0], StringComparison.Ordinal);
        Assert.Contains("回填", result.SkippedNullabilityTightening[0], StringComparison.Ordinal);
    }

    /// <summary>第二臂：库说"没有空值"。收紧照发，且<em>不</em>进跳过清单——那一臂不许退化成"为了保险都不发"，
    /// 否则净库上的可空性永远追不平契约，而报告里一句"跳过了"看着像成功。</summary>
    [Fact]
    public async Task Clean_store_tightens_as_before()
    {
        var probe = new SyncProbe(nulls: false);

        var result = await RunAsync(probe);

        Assert.Single(probe.Sent);
        Assert.StartsWith("ALTER TABLE [dbo].[TIGHT_PROBE] ALTER COLUMN [Title]", probe.Sent[0], StringComparison.Ordinal);
        Assert.EndsWith(" NOT NULL", probe.Sent[0], StringComparison.Ordinal);
        Assert.True(result.Executed);
        Assert.Equal(["TIGHT_PROBE.Title"], result.AlteredColumns);
        Assert.Empty(result.SkippedNullabilityTightening);
    }

    /// <summary>第三臂：这一家没接那颗探针（<c>null</c>＝答不出，不是"没有"）。发，与今天逐字同形，
    /// 但欠一行"判不了"。<b>默认实现必须是答不出而不是"没有"</b>：把没接的家读成净库，就是替六家宣布
    /// 它们没有存量 NULL，而那句话只有库答得起。</summary>
    [Fact]
    public async Task A_family_that_does_not_answer_the_probe_still_emits_and_says_so()
    {
        var probe = new SyncProbe(nulls: null);

        var result = await RunAsync(probe);

        Assert.Single(probe.Sent);
        Assert.Contains("ALTER COLUMN [Title]", probe.Sent[0], StringComparison.Ordinal);
        Assert.Equal(["TIGHT_PROBE.Title"], result.AlteredColumns);
        Assert.Empty(result.SkippedNullabilityTightening);
        Assert.Contains(result.NotJudged, n => n.Contains("TIGHT_PROBE.Title", StringComparison.Ordinal));
    }

    /// <summary>放宽那一句<em>不该</em>去问库：方向判据在计划层就有了，问一句是白一趟往返。
    /// <see cref="SyncProbe.Asked"/> 记的是被点名的列，所以这一臂钉的是"没问过 Title"，而放宽的那一句照发。</summary>
    [Fact]
    public async Task A_loosening_alter_never_asks_the_store()
    {
        var probe = new SyncProbe(nulls: true, titleTight: false, noteLoose: true);

        var result = await RunAsync(probe);

        Assert.Empty(probe.Asked);
        Assert.Single(probe.Sent);
        Assert.Contains("ALTER COLUMN [Note]", probe.Sent[0], StringComparison.Ordinal);
        Assert.EndsWith(" NULL", probe.Sent[0], StringComparison.Ordinal);
        Assert.Equal(["TIGHT_PROBE.Note"], result.AlteredColumns);
        Assert.Empty(result.SkippedNullabilityTightening);
    }

    // ── PG 家孪生：事故现场在谁家，字面就在谁家钉 ─────────────────────
    //
    // 23502 那一场的证人是一颗 PostgreSQL 的列（<c>ATRewriteTable</c>），所以"脏表不发 <c>SET NOT NULL</c>"
    // 这一判不能只落在 T-SQL 的 <c>ALTER COLUMN … NOT NULL</c> 字形上：本家的收紧那一半是独立的一句
    // （<c>ALTER COLUMN … SET NOT NULL</c>），孪生两臂量的就是这一句发与不发，而计划的其余部分逐字相同。

    /// <summary>PG 家第一臂的孪生：库答"有空值" ⇒ <c>SET NOT NULL</c> 那一句整个不发——
    /// <see cref="ProbeCommand.ExecuteNonQueryAsync"/> 的台账是空的，跳过清单点着 <c>TIGHT_PROBE.Title</c>。
    /// 这一格红一次就是宿主在启动那一趟撞 23502 那场的复发。</summary>
    [Fact]
    public async Task The_pg_dirty_twin_sends_no_set_not_null()
    {
        var probe = new SyncProbe(nulls: true, pg: true);

        var result = await RunAsync(probe);

        Assert.Empty(probe.Sent);
        Assert.Empty(result.DdlStatements);
        Assert.Empty(result.AlteredColumns);
        Assert.False(result.Executed);
        Assert.Single(result.SkippedNullabilityTightening,
            s => s.Contains("TIGHT_PROBE.Title", StringComparison.Ordinal));
    }

    /// <summary>PG 家第二臂的孪生：库答"没有空值" ⇒ 那一句照发，且发的正是带 <c>SET NOT NULL</c> 的那一形。
    /// 与上面那一臂逐句对照：同一份计划、同一句文本，<em>唯一的差是它发没发出去</em>——
    /// "其余的 plan 不变"这一判在读数上就是这两臂相减只剩一个布尔。</summary>
    [Fact]
    public async Task The_pg_clean_twin_emits_the_set_not_null_as_before()
    {
        var probe = new SyncProbe(nulls: false, pg: true);

        var result = await RunAsync(probe);

        Assert.Single(probe.Sent);
        Assert.Contains("ALTER COLUMN \"Title\" TYPE VARCHAR(32)", probe.Sent[0], StringComparison.Ordinal);
        Assert.Contains("SET NOT NULL", probe.Sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("DROP NOT NULL", probe.Sent[0], StringComparison.Ordinal);
        Assert.Equal(["TIGHT_PROBE.Title"], result.AlteredColumns);
        Assert.Empty(result.SkippedNullabilityTightening);
    }

    /// <summary>库里那一列本来就跟契约一样紧：没有可空性差，于是既不发 <c>ALTER</c> 也不问库。
    /// 这一臂挡住"每次同步都给每个非空列加一句 <c>SELECT</c>"那种退化。</summary>
    [Fact]
    public async Task An_already_tight_column_is_neither_altered_nor_probed()
    {
        var probe = new SyncProbe(nulls: true, titleTight: false);

        var result = await RunAsync(probe);

        Assert.Empty(probe.Asked);
        Assert.Empty(probe.Sent);
        Assert.True(result.IsEmpty);
    }

    /// <summary>跑一趟 Execute。<see cref="SyncProbe.Pg"/> 为 <c>true</c> 时内层换 PG 那一家真 provider
    /// （事故现场那一家），目录读数跟着换成本家会报的字形；其余两格（连接桩、探针答复）一格不动。
    /// <b>两家共用同一只 <see cref="SyncProbe"/>，孪生两臂之间就没有第二种会漂移的东西。</b></summary>
    static async Task<SchemaSyncResult> RunAsync(SyncProbe probe)
    {
        IDbProvider inner = probe.Pg
            ? PostgreSql.Provider(SqlServer.NoDriverFactory.Instance)
            : SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = new ProbeProvider(inner, probe),
            ConnectionString = "Server=probe;Database=tight-probe;",
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        _ = orm.Schemas.Contract(typeof(Probe));

        return await orm.SyncStructureAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>桩 provider：<c>CodeFirst</c>/<c>Types</c>/<c>Dialect</c> 全部内层照抄，只换两处——
    /// 连接是记文本的桩，目录读数与探针答复由 <see cref="SyncProbe"/> 手摆。</summary>
    sealed class ProbeProvider(IDbProvider inner, SyncProbe probe) : IDbProvider
    {
        public DbKind Kind => inner.Kind;
        public DbCapabilities Capabilities => inner.Capabilities;
        public IConnectorFactory Connectors { get; } = new ProbeConnectors(probe);
        public ISqlDialect Dialect => inner.Dialect;
        public ITypeMap Types => inner.Types;
        public IValueBinder Values => inner.Values;
        public IExpressionRules Expressions => inner.Expressions;
        public ICodeFirstDialect CodeFirst => inner.CodeFirst;
        public ISchemaReader Schema { get; } = new HandRead(probe);
        public IKeyStrategy Keys => inner.Keys;
        public IConcurrencyStrategy Concurrency => inner.Concurrency;
        public IChangeGuard ChangeGuard => inner.ChangeGuard;
        public IBulkStrategy? Bulk => inner.Bulk;

        // 与失败那一册同一格理由：建库与这一格要证的三臂无关，开了它桩就得再装一份能被建库逻辑读的连接。
        public bool CanCreateDatabase => false;
    }

    /// <summary>手摆的目录读数：两列字符列的形状都摆成<em>本家目录会报的字形</em>（T-SQL 一家是
    /// <c>nvarchar(32)</c>，PG 一家是 <c>varchar(32)</c>——与本家 <c>ColumnShapeMatches</c> 的折叠自洽），
    /// 只有可空性按开关给。<b>除了可空性一个字节都不拧歪</b>，清单里才只留可空性那一句。</summary>
    sealed class HandRead(SyncProbe probe) : ISchemaReader
    {
        public Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
            => Task.FromResult<SchemaTable?>(new SchemaTable(table, [
                new SchemaColumn("Id", probe.Pg ? "integer" : "int", null, null, false, 1) { Comment = string.Empty, IsIdentity = true, IsPrimaryKey = true },
                new SchemaColumn("Title", probe.Pg ? "varchar(32)" : "nvarchar(32)", 32, null, probe.TitleNullableInStore, 2) { Comment = string.Empty, IsPrimaryKey = false },
                new SchemaColumn("Note", probe.Pg ? "varchar(32)" : "nvarchar(32)", 32, null, !probe.NoteLoose, 3) { Comment = string.Empty, IsPrimaryKey = false },
            ])
            { Comment = string.Empty, Indexes = [] });

        public Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(
            DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
            => Task.FromResult<LengthSemanticsReading?>(null);

        /// <summary>这一颗是桩自己答的：记下列名（<see cref="SyncProbe.Asked"/>）再交回用例给的那一态。
        /// 三臂里 <c>null</c> 那一支走的就是"用例没接"这条路，与真 provider 的默认实现同一形状。</summary>
        public Task<bool?> ColumnHasNullsAsync(
            DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
            => Task.FromResult(probe.Ask(columnName));
    }

    /// <param name="nulls">探针那一问的答案：true＝有存量空值、false＝干净、null＝这一家答不出。</param>
    /// <param name="titleTight">true＝库里 <c>Title</c> 可空而契约要它非空（收紧那一格在场）。</param>
    /// <param name="noteLoose">true＝库里 <c>Note</c> 非空而契约可空（放宽那一句在场）。</param>
    /// <param name="pg">true＝内层 provider 与目录读数都换成 PG 那一族（事故现场那一家，字面钉 <c>SET NOT NULL</c>）。</param>
    sealed class SyncProbe(bool? nulls, bool titleTight = true, bool noteLoose = false, bool pg = false)
    {
        /// <summary>发出去的 DDL 原文，顺序 = 发送顺序。</summary>
        public List<string> Sent { get; } = [];

        /// <summary>被问过有没有空值的那些列。<b>两臂"不该问"的裁判就是这一颗为零</b>。</summary>
        public List<string> Asked { get; } = [];

        public bool TitleNullableInStore => titleTight;

        /// <summary>库里 <c>Note</c> 非空而契约可空 = 放宽那一格在场。</summary>
        public bool NoteLoose { get; } = noteLoose;

        /// <summary>这一趟的内层 provider 与目录字形是 PG 那一族（<see cref="RunAsync"/> 与
        /// <see cref="HandRead"/> 共读这一颗）。</summary>
        public bool Pg { get; } = pg;

        public bool? Ask(string columnName)
        {
            Asked.Add(columnName);

            return nulls;
        }
    }

    sealed class ProbeConnectors(SyncProbe probe) : IConnectorFactory
    {
        public DbConnection CreateConnection(string connectionString) => new ProbeConnection(probe);

        public DbCommand CreateCommand(DbConnection connection) => new ProbeCommand(probe, connection);

        public bool TryEnsureLoaded(out string? failure)
        {
            failure = null;
            return true;
        }
    }

    /// <summary>台账跟着连接走：<b>两条取命令的路（<c>Connectors.CreateCommand</c> 与
    /// <c>connection.CreateCommand()</c>）交回的是同一种带台账的命令</b>，否则内核换了取法就静默少记一句，
    /// 那一臂的"发了/没发"读的是桩的记性而不是内核的。</summary>
    sealed class ProbeConnection(SyncProbe probe) : DbConnection
    {
        ConnectionState _state = ConnectionState.Closed;

        [AllowNull] public override string ConnectionString { get; set; } = "Server=probe;Database=tight-probe;";
        public override string Database => "tight-probe-db";
        public override string DataSource => "probe";
        public override string ServerVersion => "0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new ProbeCommand(probe, this);
    }

    /// <summary>只有 <c>ExecuteNonQueryAsync</c> 这一条出口有实现：结构同步发 DDL 走的就是它。
    /// 探针那一问由 reader 直接答，不经命令，所以这里交不出 <c>ExecuteReader</c> ——撞上它就是调用形状变了。</summary>
    sealed class ProbeCommand(SyncProbe probe, DbConnection? connection) : DbCommand
    {
        DbConnection? _connection = connection;

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
            probe.Sent.Add(CommandText);

            return Task.FromResult(1);
        }

        static NotSupportedException NotInThisBook(string member)
            => new($"{member} 不由这一册作证：结构同步只走异步那一条出口，撞上它就说明调用形状变了。");
    }
}
