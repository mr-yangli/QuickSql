using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>默认值那一格（2026-11-01 落地）在<b>真库</b>上的两场：建表与补列。
///
/// <para>① <b>建表带默认</b>：插一行不碰默认列，读回来是契约里那颗值 —— "默认"的语义由库兑现，
/// 不是本库在物化时补的（那会盖住库里真实存的别的值）。</para>
///
/// <para>② <b>补列带默认</b>：先建 v1 的表、种一行存量数据，再按 v2（多一颗 NOT NULL 新列 + 默认值）
/// 同步 —— 那一格在离线册里从"永远拒"翻成"放行"，这里证的是放行之后那句
/// <c>ADD COLUMN … DEFAULT … NOT NULL</c> 真把存量行填上了、新插入也不欠值。</para>
///
/// <para>靶表只归本文件写，两场各用自己的批次标签，跨场重跑安全。</para></summary>
public sealed class DefaultValuePathTests
{
    [Table("QuickSqlDefaultsV1")]
    public class SeedRow
    {
        public int Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Sku { get; set; }
    }

    /// <summary>v2：同一张表多了两颗带默认值的列 —— NOT NULL 整型（补列那格翻案的正主）
    /// 与可空字符串（默认值不是 NOT NULL 的专利那一半）。</summary>
    [Table("QuickSqlDefaultsV1")]
    public class GrownRow
    {
        public int Id { get; set; }

        [QuickColumn(Length = 32)]
        public string? Sku { get; set; }

        [DefaultValue(7)]
        public int Reserved { get; set; }

        [DefaultValue("待定")]
        [QuickColumn(Length = 32)]
        public string? Note { get; set; }
    }

    static QuickSqlOrm Orm() => new QuickSqlOrm(new QuickSqlOrmOptions
    {
        Provider = Database.Provider,
        ConnectionString = Database.ConnectionString,
        SchemaSync = SchemaSyncPolicy.Execute,
        ForceSchemaSync = true,
    });

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static string Batch => "qsql-dv-" + Guid.NewGuid().ToString("N")[..8];

    static void Drop()
    {
        Database.Ready();
        Database.Execute("DROP TABLE IF EXISTS " + Database.Provider.CodeFirst.Identifiers.Quote("QuickSqlDefaultsV1"));
    }

    /// <summary>建表带默认 + <b>语句真的省掉了默认列</b>：ORM 的插入按列清单全量写（实体没赋值的就是
    /// CLR 默认值），所以"库给值"那一格要由<em>不带那几列的语句</em>证 —— 原生 SQL 逃生舱走同一条执行链（14 篇），
    /// 插进去再由 ORM 读回来，库填的那两格是事实而不是物化层补的。</summary>
    [Fact]
    public async Task Create_with_defaults_and_insert_without_those_columns()
    {
        Drop();

        var orm = Orm();
        orm.Schemas.Contract<GrownRow>();
        await orm.SyncStructureAsync(ct);

        var sku = Batch + "/create";

        // 列清单只写 Sku：Reserved 与 Note 根本不在语句里，值由库按 DEFAULT 给。
        await orm.Ado.ExecuteNonQueryAsync(
            "INSERT INTO " + Database.Provider.CodeFirst.Identifiers.Quote("QuickSqlDefaultsV1")
            + " (" + Database.Provider.CodeFirst.Identifiers.Quote("Sku") + ") VALUES (@sku)",
            new { sku }, ct);

        var back = await orm.Select<GrownRow>().Where(r => r.Sku == sku).FirstOrDefaultAsync(ct);

        Assert.NotNull(back);
        Assert.Equal(7, back!.Reserved);
        Assert.Equal("待定", back.Note);
    }

    /// <summary>补列那格翻案的真库半边：存量行由库按 DEFAULT 填值（裁判读的是库里的事实，
    /// 不是被测读口自证），新插入不欠值。</summary>
    [Fact]
    public async Task A_not_null_column_with_a_default_is_added_and_backfilled()
    {
        Drop();

        var orm = Orm();
        orm.Schemas.Contract<SeedRow>();
        await orm.SyncStructureAsync(ct);

        var sku = Batch + "/grown";

        await orm.Insert(new SeedRow { Sku = sku }).ExecuteAffrowsAsync(ct);

        // 同一张表长出两颗带默认值的列：这一趟在离线册已判"放行"，真库上要证的是句子真的执行得动。
        orm.Schemas.Contract<GrownRow>();
        await orm.SyncStructureAsync(ct);

        var stored = await orm.Select<GrownRow>().Where(r => r.Sku == sku).FirstOrDefaultAsync(ct);

        Assert.NotNull(stored);
        // NOT NULL + DEFAULT 那一列：存量行由库回填（SQL Server 实测如此；可空列 + DEFAULT 那一列
        // 各家的存量行口径不同——SQL Server 不回填——所以可空那半只对新插入断，见下面 fresh）。
        Assert.Equal(7, stored!.Reserved);

        // 新插入不欠值：默认列一颗都不写（原生 SQL，同一场 ① 的口径）。
        var sku2 = Batch + "/after";

        await orm.Ado.ExecuteNonQueryAsync(
            "INSERT INTO " + Database.Provider.CodeFirst.Identifiers.Quote("QuickSqlDefaultsV1")
            + " (" + Database.Provider.CodeFirst.Identifiers.Quote("Sku") + ") VALUES (@sku)",
            new { sku = sku2 }, ct);

        var fresh = await orm.Select<GrownRow>().Where(r => r.Sku == sku2).FirstOrDefaultAsync(ct);

        Assert.NotNull(fresh);
        Assert.Equal(7, fresh!.Reserved);
        Assert.Equal("待定", fresh.Note);
    }
}
