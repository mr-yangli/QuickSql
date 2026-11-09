using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>应用给值并发列（AppSetsValue，ABP ConcurrencyStamp 同款）在<b>真库</b>上的那一半。
/// 这一档是 rowversion 之外所有家（含 MySQL/PG/SQLite）做乐观并发的正路：应用保存前给新值，
/// 库拿快照旧值进 WHERE、实体现值进 SET，0 行命中抛 <see cref="DbUpdateConcurrencyException"/>。
/// rowversion 族的用例（ConcurrencyPathTests）在非 SQL Server 家按引擎能力跳——这一册补的就是
/// "那 4 颗跳过之后，MySQL 上的乐观并发有没有真库见证"这一格。
/// 靶表只归本文件写，行带批次标签，跨场重跑安全。</summary>
public sealed class AppSetsValuePathTests
{
    /// <summary>特性声明的靶子。</summary>
    [Table("QuickSqlAppTokenRow")]
    public class TokenRow
    {
        public int Id { get; set; }

        [QuickColumn(Length = 64)]
        public string? Sku { get; set; }

        [QuickColumn(Length = 64)]
        [ConcurrencyToken(true)]   // 应用给值族：构造参数那一颗（这一档没有命名参数形）
        public string? Stamp { get; set; }
    }

    /// <summary>约定档的靶子：成员名叫 ConcurrencyStamp，不打任何注解也自动进应用给值族。</summary>
    [Table("QuickSqlAppTokenConvention")]
    public class ConventionRow
    {
        public int Id { get; set; }

        [QuickColumn(Length = 64)]
        [ConcurrencyToken(true)]   // 应用给值族：构造参数那一颗（这一档没有命名参数形）
        public string? Stamp { get; set; }

        [QuickColumn(Length = 64)]
        public string? ConcurrencyStamp { get; set; }
    }

    static QuickSqlOrm NewOrm() => new QuickSqlOrm(new QuickSqlOrmOptions
    {
        Provider = Database.Provider,
        ConnectionString = Database.ConnectionString,
        SchemaSync = SchemaSyncPolicy.Execute,
        ForceSchemaSync = true,
    });

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static string Quote(string name) => Database.Provider.CodeFirst.Identifiers.Quote(name);

    static async Task<QuickSqlOrm> SeedAsync<T>() where T : class
    {
        Database.Ready();

        var table = Database.Provider.CodeFirst.Identifiers.Quote(TableNameOf<T>());

        Database.Execute($"DROP TABLE IF EXISTS {table}");

        var orm = NewOrm();
        orm.Schemas.Contract<T>();
        await orm.SyncStructureAsync(ct);

        return orm;
    }

    static string TableNameOf<T>() where T : class
    {
        var attribute = typeof(T).GetCustomAttributes(typeof(TableAttribute), inherit: true).FirstOrDefault() as TableAttribute;

        return attribute?.Name ?? typeof(T).Name;
    }

    static string Batch => "qsql-appstv-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task New_stamp_goes_into_set_and_old_into_where()
    {
        var orm = await SeedAsync<TokenRow>();

        var sku = Batch + "/a";
        var row = new TokenRow { Sku = sku, Stamp = "v1" };

        await orm.Insert(row).ExecuteAffrowsAsync(ct);

        // 会话改列并换新 stamp：成功 = 旧值确实进了 WHERE（假乐观锁在这里会静默成功也看不出，
        // 冲突那一格由下一颗用例用裸 ADO 造现场来证）。
        await using var db = new AppTokenContext(orm);
        var tracked = await db.Set<TokenRow>().Where(r => r.Sku == sku).FirstOrDefaultAsync(ct);

        Assert.NotNull(tracked);
        Assert.Equal("v1", tracked!.Stamp);

        // 只换 stamp：AppSetsValue 档的脏判定自然把新值带进 SET、快照旧值带进 WHERE。
        tracked.Stamp = "v2";

        await db.SaveChangesAsync(ct);

        var judge = Database.Rows(
            $"SELECT {Quote("Stamp")} AS S FROM {Quote("QuickSqlAppTokenRow")} WHERE {Quote("Sku")} = @p",
            ("@p", sku));

        Assert.Equal("v2", (string) judge.Single()["S"]!);
    }

    /// <summary>冲突现场：别人先改了 stamp，本会话按快照保存 → 抛，双方值都带，批内未结算。</summary>
    [Fact]
    public async Task A_stamp_changed_by_someone_else_raises_with_both_sides()
    {
        var orm = await SeedAsync<TokenRow>();

        var sku = Batch + "/b";

        await orm.Insert(new TokenRow { Sku = sku, Stamp = "mine" }).ExecuteAffrowsAsync(ct);

        await using var db = new AppTokenContext(orm);
        var tracked = await db.Set<TokenRow>().Where(r => r.Sku == sku).FirstOrDefaultAsync(ct);

        Assert.NotNull(tracked);

        // 别人先改：裸 ADO 直接把库里的 stamp 换掉。
        Database.Execute(
            $"UPDATE {Quote("QuickSqlAppTokenRow")} SET {Quote("Stamp")} = @v WHERE {Quote("Sku")} = @p",
            ("@v", "theirs"), ("@p", sku));

        tracked!.Sku = sku + "-x";   // 批内另一处改动，证明"整批未结算"

        var raised = Assert.Throws<DbUpdateConcurrencyException>(
            () => db.SaveChangesAsync(ct).GetAwaiter().GetResult());

        Assert.Equal("mine", (string) raised.CurrentValues["Stamp"]!);
        Assert.Equal("theirs", (string) raised.DatabaseValues!["Stamp"]!);
        Assert.False(raised.RowIsGone);

        // 整批未结算：库里两处都没落。
        var judge = Database.Rows(
            $"SELECT {Quote("Stamp")} AS S, {Quote("Sku")} AS K FROM {Quote("QuickSqlAppTokenRow")} WHERE {Quote("Sku")} LIKE @p",
            ("@p", sku + "%"));

        Assert.Single(judge);
        Assert.Equal("theirs", (string) judge.Single()["S"]!);
    }

    /// <summary>消失行：快照还在库里已不在 → RowIsGone，而不是"被别人改了"。</summary>
    [Fact]
    public async Task A_row_that_vanished_is_reported_as_gone()
    {
        var orm = await SeedAsync<TokenRow>();

        var sku = Batch + "/c";

        await orm.Insert(new TokenRow { Sku = sku, Stamp = "v1" }).ExecuteAffrowsAsync(ct);

        await using var db = new AppTokenContext(orm);
        var tracked = await db.Set<TokenRow>().Where(r => r.Sku == sku).FirstOrDefaultAsync(ct);

        Assert.NotNull(tracked);

        Database.Execute($"DELETE FROM {Quote("QuickSqlAppTokenRow")} WHERE {Quote("Sku")} = @p", ("@p", sku));

        tracked!.Sku = sku + "-x";

        var raised = Assert.Throws<DbUpdateConcurrencyException>(
            () => db.SaveChangesAsync(ct).GetAwaiter().GetResult());

        Assert.True(raised.RowIsGone);
    }

    /// <summary>约定档：成员名叫 ConcurrencyStamp 的属性不打注解也自动进应用给值族——
    /// 表里有那一列、改它触发并发谓词（别人抢先改 → 冲突抛）。</summary>
    [Fact]
    public async Task The_convention_named_member_is_a_token_without_attributes()
    {
        var orm = await SeedAsync<ConventionRow>();

        var stampKey = Batch + "/d";

        await orm.Insert(new ConventionRow { Stamp = stampKey, ConcurrencyStamp = "c1" }).ExecuteAffrowsAsync(ct);

        await using var db = new AppTokenContext(orm);
        var tracked = await db.Set<ConventionRow>().Where(r => r.Stamp == stampKey).FirstOrDefaultAsync(ct);

        Assert.NotNull(tracked);

        Database.Execute(
            $"UPDATE {Quote("QuickSqlAppTokenConvention")} SET {Quote("ConcurrencyStamp")} = @v WHERE {Quote("Stamp")} = @p",
            ("@v", "c2"), ("@p", stampKey));

        // 触发一次真写：另一颗并发列（特性档）的新值进 SET，而约定档的快照在库里已被换——冲突。
        tracked!.Stamp = stampKey + "-x";

        Assert.Throws<DbUpdateConcurrencyException>(
            () => db.SaveChangesAsync(ct).GetAwaiter().GetResult());
    }
}

public class AppTokenContext(QuickSqlOrm orm) : DbContext(orm);
