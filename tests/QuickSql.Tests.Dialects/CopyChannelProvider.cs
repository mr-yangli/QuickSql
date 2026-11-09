using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Tests.Dialects;

/// <summary>把 <see cref="IBulkStrategy.ResolveCopyChannel()"/> 换成替身的<b>转发</b>包装（§12.2 的阶梯判据）。
///
/// <para>为什么要有这一份：真的那一条通道是 <c>SqlBulkCopy</c>，只能按类型名在<em>驱动程序集</em>里找，
/// 而方言册配的是 <see cref="QuickSql.TestInfra.SqlServer.NoDriverFactory"/> —— 那一个 assembly 里没有装载类，
/// 于是"通道在场"这一支在离线上永远拿不到。它今天拿不到不等于不用测：「<c>Auto</c> 落在 L1」「一列不合格
/// 整批退回」「<c>FallbackPolicy.Never</c> 在有通道时不该咬人」这三格若只让集成册量，就变成
/// "只有连得上库才测得动的分支"，而 §13.4 反对的正是这个。</para>
///
/// <para>转发而不是重写是关键：内层还是真的 <c>SqlServerProvider</c>（方言、binder、参数预算全部照抄），
/// 被掰的只有"通道那一个产物"这一格。与 <see cref="NoBulkKeysProvider"/>、<c>LowBudgetProvider</c> 同一个手法。</para></summary>
sealed class CopyChannelProvider(IDbProvider inner, IBulkCopyChannel? channel) : IDbProvider
{
    /// <summary>通道在场，而且每一列都收 —— 阶梯最高那一档的现场。</summary>
    public static CopyChannelProvider OfferingChannel(IDbProvider inner) => new(inner, new FakeChannel());

    /// <summary>通道在场，但 <paramref name="rejectedColumn"/> 那一列它不收（§12.1 那句"整批退回，不省列"的现场）。</summary>
    public static CopyChannelProvider Rejecting(IDbProvider inner, string rejectedColumn) => new(inner, new FakeChannel(rejectedColumn));

    /// <summary>这一家明说没有通道。与真库上"驱动被裁过"交回的是同一个产物，但这里不必裁一遍。</summary>
    public static CopyChannelProvider WithoutChannel(IDbProvider inner) => new(inner, null);

    public DbKind Kind => inner.Kind;

    public DbCapabilities Capabilities => inner.Capabilities;

    public IConnectorFactory Connectors => inner.Connectors;

    public ISqlDialect Dialect => inner.Dialect;

    public ITypeMap Types => inner.Types;

    public IValueBinder Values => inner.Values;

    public IExpressionRules Expressions => inner.Expressions;

    public ICodeFirstDialect CodeFirst => inner.CodeFirst;

    public ISchemaReader Schema => inner.Schema;

    public IKeyStrategy Keys => inner.Keys;

    public IConcurrencyStrategy Concurrency => inner.Concurrency;

    public IChangeGuard ChangeGuard => inner.ChangeGuard;

    // 预算与寻址都照内层：这一颗替身只负责"有没有通道"，改了预算就等于同时改了两件要分别证的事。
    public IBulkStrategy? Bulk => inner.Bulk is { } bulk ? new FakedBulk(bulk, channel) : null;

    sealed class FakedBulk(IBulkStrategy inner, IBulkCopyChannel? channel) : IBulkStrategy
    {
        public int ParameterBudget => inner.ParameterBudget;

        public string Describe => inner.Describe + "（本用例把装载通道换成替身）";

        public IBulkCopyChannel? ResolveCopyChannel() => channel;
    }

    /// <summary>只会拒绝某一列的通道。<b>它不写任何东西</b>：写入那一段的证据在集成册，
    /// 离线上碰着 <see cref="WriteAsync"/> 就说明有一条用例把库连上了，那是要立刻变红的形状。</summary>
    sealed class FakeChannel(string? rejectedColumn = null) : IBulkCopyChannel
    {
        public bool SupportsColumn(ColumnContract column) => column.ColumnName != rejectedColumn;

        [RequiresUnreferencedCode("接口那一句的镜像（IL2046 要求两边一致）。")]
        [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条。")]
        public Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct)
            => throw new NotSupportedException("装载通道的写入不由离线册作证：走到这里说明这条用例连了库。");
    }
}
