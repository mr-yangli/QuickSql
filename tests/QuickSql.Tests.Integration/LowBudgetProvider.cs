using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Tests.Integration;

/// <summary>把 <see cref="IBulkStrategy.ParameterBudget"/> 掰小的<b>转发</b>包装（§12.3 的分块式在合并档上的见证）。
///
/// <para>为什么要有这一份：合并语句的每行参数只有<em>键 + 改动列</em>两颗，按 SQL Server 的真预算
/// （2100）算出来是 945 行一块 —— 要在真库上跨到第二块就得灌 946 行实体、再把它们全部读进会话。
/// 那一格的<em>判据</em>与行数无关（要判的是"块边界处列没串、每行落回自己那一格"），
/// 所以把预算掰小到 6（= 2 行一块）、灌 3 行，跑的是同一段循环。</para>
///
/// <para>转发而不是重写是关键：内层还是<em>真的</em>那一家的 provider（v0.32 第二格起那颗由
/// <c>Database.Provider</c> 给，缺省那一家仍是 <c>SqlServerProvider</c>），于是这里发出的语句与真库上
/// 任何其他用例发出的逐字相同，被掰的只有那一个数字。与 <c>NoBulkKeysProvider</c> 同一个手法。</para></summary>
sealed class LowBudgetProvider(IDbProvider inner, int parameterBudget, bool synthesize) : IDbProvider
{
    public static LowBudgetProvider Around(IDbProvider inner, int parameterBudget) => new(inner, parameterBudget, synthesize: false);

    /// <summary>与 <see cref="Around"/> 同一份包装，区别只在<em>内层根本没有策略</em>的那几家
    /// （Oracle 血统、虚谷、SQLite：它们不报预算，引擎走共用层 <c>ConservativeParameterBudget</c> 的 100 兜底，
    /// 于是 <see cref="Around"/> 在那里<em>掰不动任何东西</em>——<c>null is null</c>，包装整个跳过）。
    /// 这里造一份只带那一颗预算的策略，"把预算掰小"因此在每家都判得到。
    /// <b>三颗 DIM 走接口的默认值，而那不是新增声称</b>：没有通道、没在并档表过态、没交 upsert 的句尾，与内层交回 <see langword="null"/>
    /// 时引擎读到的同一件事；本厂只报一个数字。</summary>
    public static LowBudgetProvider BudgetOnly(IDbProvider inner, int parameterBudget) => new(inner, parameterBudget, synthesize: true);

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

    public IBulkStrategy? Bulk => inner.Bulk is { } strategy
        ? new TightBudget(strategy, parameterBudget)
        : synthesize ? new OnlyBudget(parameterBudget) : null;

    /// <summary>内层根本没有策略时的那一份替身：<b>只有一颗预算</b>，其余两格走接口默认值
    /// （那与 <c>Bulk == null</c> 时引擎读到的同一件事：没有装载通道、没在多语句批上表过态）。</summary>
    sealed class OnlyBudget(int budget) : IBulkStrategy
    {
        public int ParameterBudget => budget;

        public string Describe => $"内层不报预算，本用例把兜底掰成 {budget}（装载通道与多语句批两格仍是不接）";
    }

    sealed class TightBudget(IBulkStrategy inner, int budget) : IBulkStrategy
    {
        public int ParameterBudget => budget;

        public string Describe => inner.Describe + $"（本用例把预算掰成 {budget}）";

        // 两颗 DIM 必须显式转发，否则包壳会把"这一家有没有通道""这一家收不收多语句批"一起掰没：
        // IBulkStrategy 上新增槽位一律带默认值（§12.1 那条纪律），默认值在包壳身上读起来就是"这一家表过态说不"。
        // 本册的判据是"只有那一颗预算被掰了"，所以两格照抄内层。与 LowBudgetWriteProvider 同一份写法。
        public bool SupportsMergedStatements => inner.SupportsMergedStatements;

        // 第三颗 DIM 同一条纪律，而它在<em>本册</em>比在离线册更要紧：掰预算的那一条 upsert 用例就在这一家身上跑，
        // 漏转发的话判档读到的是接口的默认 null ⇒ 直接降级成 Merged/SingleRow，"一句 upsert 切成三块"
        // 那一格根本没被走到，而用例只是绿着少测一段（它断言的是落库与 UsedTransaction，两档都能绿）。
        public string? UpsertClause(UpsertColumns columns) => inner.UpsertClause(columns);

        [RequiresUnreferencedCode("接口那一句的镜像（IL2046 要求两边一致）。")]
        [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条。")]
        public IBulkCopyChannel? ResolveCopyChannel() => inner.ResolveCopyChannel();
    }
}
