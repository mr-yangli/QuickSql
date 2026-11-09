using System.Data;
using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Providers.MySql;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Values;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>「事务可选」那道口的判定册（§14 第 22 颗库种那一行的重开条件②，【本笔（ClickHouse 重开·笔一）】）。
///
/// <para>形状学的是本册里已有的 guardrail 类：借 <see cref="SchemaSyncFailureTests"/> 那份
/// "拿一个真 provider 包住、只改一格"的整形壳写法，判据不在壳上是在位上。判的是刀口的两侧：<b>没有
/// <see cref="DbCapabilities.Transactions"/> 位时，公开的 <c>BeginTransaction</c> 当场抛
/// <see cref="NotSupportedException"/>（照 <c>CanCreateDatabase</c>=false 先例的形状，消息指名，
/// 严禁静默假事务）；位声明回去时，走的是与本笔之前逐字同一条路 —— 落在驱动那一格的旧异常上，
/// 而不落在这道新闸门上。两格一起钉住的是这个改判的最硬那一半：内核写路径与公开口都认这一颗位，
/// 谁也不得绕过它。</para>
///
/// <para>壳上那个记数器同时钉了顺序：闸门先于驱动 —— 没有位的那一只，驱动加载那一步
///（<c>IConnectorFactory.TryEnsureLoaded</c>）一次都不该被问过，否则"没事务"与"没驱动"两个红
/// 就有一个被盖掉的机会。</para></summary>
public sealed class TransactionCapabilityTests
{
    static readonly MySqlProvider Inner = MySql.Provider(SqlServer.NoDriverFactory.Instance);

    /// <summary>真 provider 的整形壳（与那个 ProbeSchemaProvider 同一形状）：能力位是唯一被改写的一格，
    /// 其余各格原样转发真家的那一份 —— 判据因此只落在位上，不会被其他格的噪声冲淡。
    /// <paramref name="transactions"/> 为 false 时按位摘除（<c>&amp; ~</c>），true 时按位补上（<c>|</c>）：
    /// 两只壳盖住的正是"这一家声明不声明"两条 possible 的世界。</summary>
    sealed class NarrowingProvider(bool transactions) : IDbProvider
    {
        readonly MySqlProvider _inner = Inner;
        readonly RecordingConnectorFactory _connectors = new();

        public DbKind Kind => _inner.Kind;

        public DbCapabilities Capabilities
            => transactions ? _inner.Capabilities | DbCapabilities.Transactions : _inner.Capabilities & ~DbCapabilities.Transactions;

        public IConnectorFactory Connectors => _connectors;
        public ISqlDialect Dialect => _inner.Dialect;
        public ITypeMap Types => _inner.Types;
        public IValueBinder Values => _inner.Values;
        public IExpressionRules Expressions => _inner.Expressions;
        public ICodeFirstDialect CodeFirst => _inner.CodeFirst;
        public ISchemaReader Schema => _inner.Schema;
        public IKeyStrategy Keys => _inner.Keys;
        public IConcurrencyStrategy Concurrency => _inner.Concurrency;
        public IChangeGuard ChangeGuard => _inner.ChangeGuard;
        public IBulkStrategy? Bulk => _inner.Bulk;
    }

    sealed class RecordingConnectorFactory : IConnectorFactory
    {
        public int LoadAsked;

        public DbConnection CreateConnection(string connectionString) => throw new NotSupportedException();

        public DbCommand CreateCommand(DbConnection connection) => throw new NotSupportedException();

        public bool TryEnsureLoaded(out string? failure)
        {
            LoadAsked++;
            failure = "判据的另一半要这一步红：带位的那一只要走到这里，旧异常照旧。";
            return false;
        }
    }

    static QuickSqlOrm Orm(bool transactions) => new(new QuickSqlOrmOptions
    {
        Provider = new NarrowingProvider(transactions),
        ConnectionString = SqlServer.OfflineConnectionString,
    });

    [Fact]
    public void A_begin_transaction_without_the_bit_is_refused_with_a_named_message_and_never_touches_the_driver()
    {
        var orm = Orm(transactions: false);

        // 闸门先于驱动：NotSupportedException 是能力位的答案，不是驱动缺失的答案 ——
        // 记数器为 0 钉住"这一家连 TryEnsureLoaded 都没问过"，没有位能把它当成"驱动装上就好了"的那格活路。
        var ex = Assert.Throws<NotSupportedException>(() => orm.BeginTransaction());

        Assert.Contains("不支持事务", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DbCapabilities.Transactions), ex.Message, StringComparison.Ordinal);

        var connector = (RecordingConnectorFactory)orm.Options.Provider.Connectors;
        Assert.Equal(0, connector.LoadAsked);
    }

    [Fact]
    public void The_same_refusal_arrives_from_the_session_door_so_both_public_faces_are_the_same_gate()
    {
        // BeginSessionTransaction 走的是同一条 BeginTransaction 管线（§6 形态 (4)），所以两颗公开口
        // 在没有位的那一家上必然一致 —— 环境事务那半边不许提供第二条绕过本位的活路。
        // 集群名那颗参数不会被校验到：闸门在整条管线的第一格。
        var orm = Orm(transactions: false);

        var ex = Assert.Throws<NotSupportedException>(() => orm.BeginSessionTransaction("main"));

        Assert.Contains(nameof(DbCapabilities.Transactions), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_with_the_bit_walks_the_same_road_as_before_this_change()
    {
        var orm = Orm(transactions: true);

        // 照旧的意思要说准：走到驱动那一格才红（NoDriverFactory），而那格的异常是本笔之前的形状
        // （DriverNotAvailableException），绝不落在这颗新闸门上 —— 壳把位声明回去之后，闸门应当放行。
        // 记数器为 1 钉住"判位在先、驱动在后"的次序：位没拦它，是驱动不在。
        var ex = Assert.Throws<DriverNotAvailableException>(() => orm.BeginTransaction());

        Assert.DoesNotContain(nameof(DbCapabilities.Transactions), ex.Message, StringComparison.Ordinal);

        var connector = (RecordingConnectorFactory)orm.Options.Provider.Connectors;
        Assert.Equal(1, connector.LoadAsked);
    }

    [Fact]
    public void The_real_family_providers_declare_the_bit_now()
    {
        // 在册的每一本要声明回去：本笔给各血统族档（PG ∶ Oracle ∶ MySQL）与单层四家挂位之后，
        // 这一格拿真 provider 出场复点一遍 —— 也钉住"没有位的那一家今天只有测试侧的整形壳这一种"。
        var provider = MySql.Provider(SqlServer.NoDriverFactory.Instance);

        Assert.True(provider.Capabilities.HasFlag(DbCapabilities.Transactions));
        Assert.True(provider.Capabilities.HasFlag(DbCapabilities.Schemas));
    }
}
