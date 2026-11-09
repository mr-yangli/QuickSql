using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>复合外键的读侧补载（IncludeMany）在真库上把子行缝给<b>正确的</b>父 —— 不是叉积里的每一只。
///
/// <para><b>这一格为什么必须真库钉</b>：复合键的补载 WHERE 是执行期现造的
/// （<c>QueryEngine.QueryInBatchesAsync</c>）。它曾经发的是"每列各一个 IN"
/// （<c>LedgerBook IN (1) AND LedgerLine IN (1,9)</c>）——那是值的<b>叉积</b>：<c>(1,1)</c> 的父会把
/// <c>(1,9)</c> 的子行也缝过来，主查询文本与行数都看不出来，只有键值的配对关系能判。修后发的是
/// 逐行等值的 OR 链（<c>(Book=1 AND Line=1) OR (Book=1 AND Line=9)</c>），每行 N 颗参数与
/// <c>RowsPerStatement</c> 的预算分母同一口径。子查询文本由执行层造，离线那一步压根不发它，
/// 所以这一格只有真库那一半。</para>
///
/// <para>外键列走约定档：子端列名 = <c>{父型名}{父键列名}</c>（DiscoverFkColumns 的复合约定支），
/// 不需要引用导航点名。</para></summary>
public sealed class CompositeForeignKeyIncludePathTests
{
    /// <summary>复合主键（两列）且都由客户端给号：整型主键按约定会被判成自增，两列自增在 DDL 上根本立不住，
    /// 所以这里逐列显式声明 IsIdentity = No（与分表实体同一句话术）。</summary>
    [Table("QuickSqlCompositeLedger")]
    public sealed class CompositeLedger
    {
        [Key]
        [QuickColumn(IsIdentity = TriState.No)]
        public int Book { get; set; }

        [Key]
        [QuickColumn(IsIdentity = TriState.No)]
        public int Line { get; set; }

        [QuickColumn(Length = 32)]
        public string? Tag { get; set; }

        public List<CompositeLedgerNote>? Notes { get; set; }
    }

    /// <summary>外键两列走约定名 <c>CompositeLedgerBook</c> / <c>CompositeLedgerLine</c>。</summary>
    [Table("QuickSqlCompositeLedgerNote")]
    public sealed class CompositeLedgerNote
    {
        public int Id { get; set; }

        public int CompositeLedgerBook { get; set; }

        public int CompositeLedgerLine { get; set; }

        [QuickColumn(Length = 32)]
        public string? Memo { get; set; }
    }

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static async Task<QuickSqlOrm> RebuildStore()
    {
        Database.Ready();

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        // 两张表一起问一次契约，同步那一趟才看得见（与 IntegrityErrorTranslationPathTests 同一颗顺序）。
        orm.Schemas.Contract<CompositeLedger>();
        orm.Schemas.Contract<CompositeLedgerNote>();

        Database.Execute($"DROP TABLE IF EXISTS {Database.Provider.CodeFirst.Identifiers.Quote("QuickSqlCompositeLedgerNote")}");
        Database.Execute($"DROP TABLE IF EXISTS {Database.Provider.CodeFirst.Identifiers.Quote("QuickSqlCompositeLedger")}");

        await orm.SyncStructureAsync(ct);

        return orm;
    }

    [Fact]
    public async Task A_composite_key_include_stitches_each_child_to_its_own_parent()
    {
        var orm = await RebuildStore();

        // 两只父共享 Book=1：叉积语义下 (1,1) 会把 (1,9) 的子行也收走，只有逐行配对才分得开。
        await orm.Insert(new CompositeLedger { Book = 1, Line = 1, Tag = "quicksql-comp" }).ExecuteAffrowsAsync(ct);
        await orm.Insert(new CompositeLedger { Book = 1, Line = 9, Tag = "quicksql-comp" }).ExecuteAffrowsAsync(ct);

        await orm.Insert(new CompositeLedgerNote { CompositeLedgerBook = 1, CompositeLedgerLine = 1, Memo = "note-1-1" }).ExecuteAffrowsAsync(ct);
        await orm.Insert(new CompositeLedgerNote { CompositeLedgerBook = 1, CompositeLedgerLine = 9, Memo = "note-1-9" }).ExecuteAffrowsAsync(ct);

        var db = new DbContext(orm);

        // 会话的跟踪档由 Set<T>() 建，IncludeMany 登记子型要它先在位（CascadePathTests 同一颗现场手续）。
        db.Set<CompositeLedgerNote>();

        var ledgers = await db.Set<CompositeLedger>()
            .IncludeMany(l => l.Notes!)
            .Where(l => l.Book == 1)
            .ToListAsync(ct);

        Assert.Equal(2, ledgers.Count);

        var first = ledgers.Single(l => l.Line == 1);
        var ninth = ledgers.Single(l => l.Line == 9);

        // 各自只缝自己的那一行：叉积下两只父各拿两行，这一句当场红。
        Assert.Equal("note-1-1", Assert.Single(first.Notes!).Memo);
        Assert.Equal("note-1-9", Assert.Single(ninth.Notes!).Memo);
    }
}
