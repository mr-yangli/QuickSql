using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>§10.6 必填尺子那颗区分的活库半（离线四臂在 <c>NullabilityDeclarationTests</c>）。
///
/// <para><b>事故的形状</b>（本轮的证人，真库 PostgreSQL）：一次登录要往 <c>OpenIddictToken</c> 写一行，
/// 而 <c>Payload</c>∶<c>Properties</c>∶<c>ReferenceId</c> 那三列在那一行上本来就是 <c>null</c>
/// （引用令牌才用 <c>Payload</c>，没有袋子就没有 <c>Properties</c>）。实体住在可空上下文之外，
/// 旧那一版把"没表过态"读成"说了非空"，于是整批插入被必填校验拒在库门外 —— 症状是登录 500，
/// 而库里一行都没有。</para>
///
/// <para><b>为什么这一半只能在库里看</b>：离线那册钉的是"尺子落不落"，而本案要的是
/// <b>那行 null 真的进了库、真的读回是 null</b>。只有库能答"这一列今天收不收 null"——
/// 裁判一律裸 ADO（与 <see cref="WritePathTests"/> 同一条纪律），不能让被审的那条写链自己作证。</para></summary>
public sealed class NullabilityDeclarationPathTests
{
    /// <summary>三格与离线那册逐字同形：没表过态的、<c>[Required]</c> 说非空的、显式可空的。</summary>
    [Table("QuickSqlSpokenPath")]
    public sealed class SpokenPath
    {
        public long Id { get; set; }

#nullable disable
        public string Unspoken { get; set; }
#nullable enable

        [System.ComponentModel.DataAnnotations.Required]
        public string SpokenRequired { get; set; } = "REQ";

        public string? SpokenNullable { get; set; }
    }

    const string TableName = "QuickSqlSpokenPath";

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static string Table => Quote(TableName);

    static CancellationToken ct => TestContext.Current.CancellationToken;

    /// <summary>装配口说"这一族的键归客户端给号"那一台（与 <see cref="ApplicationKeyPathTests"/> 同一颗），
    /// 顺带把本案的行键做成客户端给的雪花号。</summary>
    static QuickSqlOrm Orm()
        => new(
            new QuickSqlOrmOptions { Provider = Database.Provider, ConnectionString = Database.ConnectionString },
            new QuickSql.Schema.EntitySchemaRegistry(
                new QuickSql.Metadata.ContractResolver(Database.Provider, null, integralPrimaryKeyAutoIncrement: false)));

    [Fact]
    public async Task A_null_in_an_unspoken_column_reaches_the_store_while_a_declared_one_stays_refused()
    {
        Database.Ready();

        var orm = Orm();

        // 靶表按<em>现库那一族的形状</em>手摆：键是普通 bigint（库里没有序列在发号），而"没表过态"的那一列
        // 库里容空 —— 那正是 SqlSugar 时代的 CodeFirst 留下的形状，也是本案那行 null 能进去的原因。
        // 不用 SyncStructureAsync 建这一张：契约那句发的是 NOT NULL，用它建就等于替本案换了一道库门。
        Database.Execute($"DROP TABLE IF EXISTS {Table}");
        Database.Execute(
            $"CREATE TABLE {Table} ({Quote(nameof(SpokenPath.Id))} BIGINT NOT NULL, " +
            $"{Quote(nameof(SpokenPath.Unspoken))} VARCHAR(32) NULL, " +
            $"{Quote(nameof(SpokenPath.SpokenRequired))} VARCHAR(32) NOT NULL, " +
            $"{Quote(nameof(SpokenPath.SpokenNullable))} VARCHAR(32) NULL)");

        var tag = "quicksql-spoken-" + Guid.NewGuid().ToString("N")[..8];

        // 那一行本来就该进库：Unspoken 交 null，键由客户端给。
        var id = 7_642_523_574_499L;

        await orm.Insert(new SpokenPath { Id = id, Unspoken = null!, SpokenRequired = tag, SpokenNullable = null })
            .ExecuteAffrowsAsync(ct);

        // 裁判是裸 ADO：行在场，那一格在库里就是 NULL。
        var rows = Database.Rows(
            $"SELECT {Quote(nameof(SpokenPath.Unspoken))}, {Quote(nameof(SpokenPath.SpokenNullable))} FROM {Table} WHERE {Quote(nameof(SpokenPath.SpokenRequired))} = @tag",
            ("@tag", tag));

        var stored = Assert.Single(rows);

        Assert.Equal("NULL", stored[nameof(SpokenPath.Unspoken)]);
        Assert.Equal("NULL", stored[nameof(SpokenPath.SpokenNullable)]);

        // 反向那一半：说过话的那一列不许跟着松。三格一起 null 时，被拒的只有 [Required] 那一格。
        var failure = Assert.Throws<ValidationFailedException>(() => orm.Insert(new SpokenPath
        {
            Id = id + 1,
            Unspoken = null!,
            SpokenRequired = null!,
            SpokenNullable = null,
        }).ToSql());

        var bad = Assert.Single(failure.Failures);

        Assert.Equal(nameof(SpokenPath.SpokenRequired), bad.MemberName);
        Assert.Equal("required", bad.Rule);

        // 而库里仍然只有那一行 —— 被拒的批次一行都没写进去（"整批先校验后执行"那句在这一格是实的）。
        Assert.Single(Database.Rows($"SELECT {Quote(nameof(SpokenPath.Id))} AS kid FROM {Table} WHERE {Quote(nameof(SpokenPath.SpokenRequired))} = @tag", ("@tag", tag)));
    }
}
