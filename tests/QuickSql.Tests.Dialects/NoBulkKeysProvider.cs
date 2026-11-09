using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Tests.Dialects;

/// <summary>把 <see cref="IKeyStrategy.SupportsBulkKeyRetrieval"/> 掰成 false 的<b>转发</b>包装（§12.4 第 1 项）。
/// 存在的理由只有一个：L3→L5 那一跳在 P0 主靶（SQL Server）上永远走不到，
/// 而"走不到的分支"等于没写 —— 达梦/Oracle 系要到 P2 才进场，不能等到那时才第一次跑这段代码。
/// 转发（而不是抄一份 provider）是关键：内层还是真的 <c>SqlServerProvider</c>，
/// 于是这个替身产出的文本与真库完全一致，被掰的只有那一个能力位。</summary>
sealed class NoBulkKeysProvider(IDbProvider inner, IKeyStrategy keys) : IDbProvider
{
    public static NoBulkKeysProvider Around(IDbProvider inner)
        => new(inner, new BulkKeysOff(inner.Keys));

    public DbKind Kind => inner.Kind;

    public DbCapabilities Capabilities => inner.Capabilities;

    public IConnectorFactory Connectors => inner.Connectors;

    public ISqlDialect Dialect => inner.Dialect;

    public ITypeMap Types => inner.Types;

    public IValueBinder Values => inner.Values;

    public IExpressionRules Expressions => inner.Expressions;

    public ICodeFirstDialect CodeFirst => inner.CodeFirst;

    public ISchemaReader Schema => inner.Schema;

    public IKeyStrategy Keys { get; } = keys;

    public IConcurrencyStrategy Concurrency => inner.Concurrency;

    public IChangeGuard ChangeGuard => inner.ChangeGuard;

    public IBulkStrategy? Bulk => inner.Bulk;

    /// <summary>只把"能不能一次拿全键"改成 false，其余照抄 —— 达梦的 <c>OUTPUT</c> 缺失就是缺在这一位上。</summary>
    sealed class BulkKeysOff(IKeyStrategy inner) : IKeyStrategy
    {
        public bool SupportsBulkKeyRetrieval => false;

        public bool SupportsExplicitIdentityInsert => inner.SupportsExplicitIdentityInsert;

        public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => inner.EmitReturning(writer, returning);

        public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => inner.IdentityInsertToggle(writer, table, on);

        // 两颗新位（v0.34）照"只掰批量位"的既有纪律处理：批量位单独掰成 false，其余原样转发。
        // 内层是 SqlServer 时回读位跟着 false —— 于是这一族替身跑的是"逐条而无处接键"的拒绝路径，
        // 与达梦今天的行为同形；要量"逐条 + 回读"那一支的是 MySql/虚谷自己的方言用例，不从这里借道。
        public bool SupportsPostStatementKeyRead => inner.SupportsPostStatementKeyRead;

        public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key) => inner.EmitPostStatementKeyRead(writer, key);
    }
}
