using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Tests.Dialects;

/// <summary>把 <see cref="IBulkStrategy.ParameterBudget"/> 掰小、<b>其余四颗槽位逐颗照抄</b>的转发包装
/// （§12.1 写侧扩展那条链的分块见证）。
///
/// <para>为什么要有这一份：真预算下 <c>UpdateRange</c> 的合并档块宽是 236 行（2100×9/10÷8 颗），
/// 要跨到第二条命令就得灌 237 行实体。而这一格要判的是<em>切块边界上的形状</em>——每条命令各自一张参数表、
/// 编号各从 0 重新起、<c>ToSql()</c> 在多条时拒交单一文本——那些判据与行数无关，把预算掰小到 40
/// （= 4 行一块）灌 5 行，跑的是同一段循环。</para>
///
/// <para><b>为什么这颗替身必须把四颗槽位都转发，而集成层那一颗同名替身只转发了预算</b>：
/// <see cref="IBulkStrategy.ResolveCopyChannel()"/>、<see cref="IBulkStrategy.SupportsMergedStatements"/> 与
/// <see cref="IBulkStrategy.UpsertClause"/> 都是接口上的<em>默认实现</em>（§12.1 那句"新增 SPI 槽位一律带默认值"），
/// 一个只覆写 <c>ParameterBudget</c> 的实现类在那三颗上读到的是默认值 <c>null</c> / <c>false</c> / <c>null</c>，
/// 而不是内层那一家<em>报出来的</em>那一份。
/// 对本册而言后果是具体的：漏转发的替身会让 <c>UpdateRange</c> 的 Auto 直接落逐条，
/// 于是"合并档切成两块"那一条分支根本没被走到，而用例只是绿着少测一段；漏转发第三颗的后果同形而更隐蔽
/// ——点名 Upsert 会<em>降级</em>成内层那一家的改档，绿着的用例量的就不再是 upsert 的切块了。
/// 集成层那一颗走的是 <c>SaveChanges</c> 的合并档（§12.5，判据不读这几颗位），所以它漏不漏都判得动——
/// 两条链各用各的替身，名字相同而槽位覆盖不同，这一格要说死免得互抄。</para>
///
/// <para>转发而不是重写是关键：内层还是真的那一家的 provider（方言、binder、标识符样式全部照抄），
/// 被掰的只有那一个数字。与 <see cref="CopyChannelProvider"/>、<see cref="NoBulkKeysProvider"/> 同一个手法。</para></summary>
sealed class LowBudgetWriteProvider(IDbProvider inner, int parameterBudget) : IDbProvider
{
    public static LowBudgetWriteProvider Around(IDbProvider inner, int parameterBudget) => new(inner, parameterBudget);

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

    public IBulkStrategy? Bulk => inner.Bulk is null ? null : new TightBudget(inner.Bulk, parameterBudget);

    sealed class TightBudget(IBulkStrategy inner, int budget) : IBulkStrategy
    {
        public int ParameterBudget => budget;

        public string Describe => inner.Describe + $"（本用例把预算掰成 {budget}）";

        public bool SupportsMergedStatements => inner.SupportsMergedStatements;

        // 这一颗不转发的话，掰小预算的那一家就同时丢掉了 upsert 档——切块的形状就再从没人走过的那一支读。
        public string? UpsertClause(UpsertColumns columns) => inner.UpsertClause(columns);

        [RequiresUnreferencedCode("接口那一句的镜像（IL2046 要求两边一致）。")]
        [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条。")]
        public IBulkCopyChannel? ResolveCopyChannel() => inner.ResolveCopyChannel();
    }
}
