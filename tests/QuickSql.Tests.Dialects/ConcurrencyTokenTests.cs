using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.TestInfra;
using QuickSql.Tracking;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>乐观并发列（§8.2，v0.18）在<b>离线</b>能判的那一半。分界线跟 <see cref="TrackingTests"/> 同一条：
/// <b>这一份里没有任何一句 SQL 上线</b>，全程 <see cref="SqlServer.NoDriverFactory"/>。
/// 于是「元数据 → 列清单 → 脏判定」这三层各自的事实是可执行断言，而不是一份说法。
///
/// <para><b>这里判不到的三件事都归集成层</b>（<c>TrackingPathTests</c>）：<c>WHERE</c> 里那句并发谓词的文本
/// （含 NULL 折成 <c>IS NULL</c> 那一档）、<c>OUTPUT inserted.[并发列]</c> 真的把新值读回实体、
/// 以及 0 行时补查回来的现值与 <c>RowIsGone</c> 的分类。三者都要活库。</para>
///
/// <para>反过来，<b>只有离线能判的是"两侧一致"</b>：并发标记只落在 G1 或只落在 G2 时，列清单、<c>WHERE</c>、
/// 回填集三处一起分岔，而那正是要到真库上才现形的一类错 —— 所以 <c>conc=</c> 已经进了
/// <see cref="MetadataEquivalenceTests"/> 的逐字段对拍。</para></summary>
public sealed class ConcurrencyTokenTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static QuickSqlOrm orm() => SqlServer.Orm();

    /// <summary>带 Fluent 配置的那一台：§10.2 的层序（Fluent 压在注解之上）只有在这里问得出答案。</summary>
    static QuickSqlOrm ormWith(ModelConfiguration model)
        => new(new QuickSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));

    static InventoryItem row(int quantity = 2) => new()
    {
        Id = 5,
        Sku = "A-1",
        Quantity = quantity,
        RowVersion = [1, 2],
    };

    static DbContext session() => new(orm());

    // ── 元数据：两格来源与 origin ────────────────────────────────────

    [Fact]
    public void The_annotation_marks_the_column_and_says_so_in_the_origin()
    {
        var columns = orm().Schemas.Contract<InventoryItem>().Columns;

        // 两格都是契约列序里的 token：RowVersion 是"库里有人写"的那一档，CheckedAt 是"没人写、恒 NULL"的那一档。
        // 离线判不出谓词差别（那是集成层的 IS NULL），但"每一格都被认成 token"必须在这一句就成立 ——
        // 少认一格的那一行就会带着并发列进 SET，而那是到真库上才炸的错。
        Assert.Equal(
            [nameof(InventoryItem.RowVersion), nameof(InventoryItem.CheckedAt)],
            columns.Where(c => c.IsConcurrencyToken).Select(c => c.MemberName).ToArray());

        var token = columns.Single(c => c.MemberName == nameof(InventoryItem.RowVersion));

        Assert.True(token.IsConcurrencyToken);
        Assert.Equal("rowversion", token.StoreTypeName);
        Checks.Has("; 并发 ← [ConcurrencyToken]", token.Origin.Note);

        // 没标这一格的列，origin 那串文本一个字都不该动 —— 它是黄金样本与既有 Explain 断言的护栏：
        // 拼上去的是"这一列归库管"，而不是给每列都补一句"不归库管"。
        foreach (var column in columns)
            if (!column.IsConcurrencyToken)
                Checks.Absent("并发", column.Origin.Note);

        Assert.All(orm().Schemas.Contract<Order>().Columns, c => Assert.False(c.IsConcurrencyToken));
    }

    [Fact]
    public void Fluent_is_the_higher_layer_and_it_works_in_both_directions()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.PaidAt).IsConcurrencyToken();
        model.Entity<InventoryItem>().Property(i => i.RowVersion).IsConcurrencyToken(false);

        var schemas = ormWith(model).Schemas;
        var added = schemas.Contract<Order>().Columns.Single(c => c.MemberName == nameof(Order.PaidAt));
        var taken = schemas.Contract<InventoryItem>().Columns.Single(c => c.MemberName == nameof(InventoryItem.RowVersion));

        Assert.True(added.IsConcurrencyToken);
        Checks.Has("; 并发 ← Fluent IsConcurrencyToken", added.Origin.Note);

        // 反向那一半才是"层序"的证据：只测"Fluent 能加上"的话，注解压住 Fluent 的实现也会全绿。
        Assert.False(taken.IsConcurrencyToken);
        Checks.Absent("并发", taken.Origin.Note);
    }

    // ── 「不书写」在两条写链上的形状 ─────────────────────────────────

    [Fact]
    public void A_token_column_stays_out_of_the_insert_column_list()
    {
        var sql = orm().Insert(row()).ToSql(out var parameters);

        // 列清单里少了两列，各有各的理由：自增列的值归库决定，并发列的值归库给。
        Checks.Has("INSERT INTO [QuickSqlInventoryItem] ([Sku], [Quantity]) VALUES (@p0, @p1)", sql);
        Checks.Absent("[Id]", sql);
        Checks.Absent("[RowVersion]", sql);
        Checks.Absent("[CheckedAt]", sql);

        Assert.Equal(2, parameters.Count);
        Assert.Equal(["Sku", "Quantity"], parameters.Select(p => p.Hint?.MemberName).ToArray());
    }

    [Fact]
    public void The_key_only_backfill_slot_still_returns_only_the_key()
    {
        var insert = orm().Insert(row());

        // ExplainKeys() 对应 ExecuteReturnKeysAsync：那一档的语义是"把自增主键给我"。
        // 并发列归跟踪器走的第三档（要真库才看得到文本，见集成层），两档分错就是多回一列或漏回一列。
        Checks.Has(") OUTPUT inserted.[Id] VALUES (", insert.ExplainKeys().ToSql());
        Checks.Absent("[RowVersion]", insert.ExplainKeys().ToSql());
        Checks.Absent("OUTPUT", insert.Explain().ToSql());
    }

    [Fact]
    public void The_create_table_text_refuses_to_advertise_a_null_for_the_column_the_store_fills()
    {
        var sql = Ddl<InventoryItem>();

        Golden.Verify("ddl-inventory-item", sql);

        // 这一句以前钉的是一个<em>缺口</em>：CodeFirst 的可空性取自 CLR，而并发列在实体侧只能声明成可空
        // （§10.6 的必填规则量的是整只实体，非空的 byte[] 会让"新增一行"在客户端就过不去），
        // 两者一撞就产出 "rowversion NULL" —— 那句 NULL 声明的是"这一列可以由人写空值"，而库永不完全给值。
        // v0.24 补的是缺的那个概念：契约长出「值归库给」那一档（<see cref="ColumnContract.ValueFromStore"/>，
        // 由本家 <c>ITypeMap.ApplyStorageDefaults</c> 落输入、DDL 读 <c>NullableInStore</c>）。
        // 用例因此改名（原名 …says_what_the_database_cannot_be，§8.2 与集成层夹具各引用过一次，两处同步）。
        Checks.Has("[RowVersion] rowversion NOT NULL", sql);

        // 同一张表上另一格并发列照旧 NULL：它不给写，可库里也没有任何东西给它值。
        // 两格并排才让这一档判得动 —— 只有一格时"token ⇒ NOT NULL"那条错规则没有任何一句能推翻它。
        Checks.Has("[CheckedAt] DATETIME2(3) NULL", sql);

        // 另外三列对位：自增列与 rowversion 同属那一档（一个由约定层判、一个由 provider 判），普通列跟着 CLR 走。
        Checks.Has("[Id] INT IDENTITY(1,1) NOT NULL", sql);
        Checks.Has("[Sku] NVARCHAR(32) NULL", sql);
        Checks.Has("[Quantity] INT NOT NULL", sql);
    }

    [Fact]
    public void A_version_column_the_database_does_not_fill_keeps_the_nullability_the_clr_declared()
    {
        // 「值归库给」按<em>存储类型</em>认，不按 ClrType 认、也不按"是不是并发列"认。
        // 少了这一格，那两条一刀切（byte[] 就算库给、token 就算库给）都不会红：
        // 上一格用例的 RowVersion 与 CheckedAt 恰好同时满足两条错判据。
        var sql = Ddl<AppVersioned>();

        // §8.2 明写过这一型："由调用方自己递增的版本号…不属于这一格"。它在库里可空是<em>允许</em>
        // （新增那一趟没人给它值），而 NOT NULL 是一句"库里那一格永远有值"的承诺，没有东西兜它。
        Checks.Has("[Stamp] VARBINARY(MAX) NULL", sql);

        // 而它确实也不进列清单，两个判据在这一列上各答各的：不写 ∩ 可空。
        var insert = orm().Insert(new AppVersioned { Name = "x" }).ToSql();

        Checks.Has("([Name]) VALUES", insert);
        Checks.Absent("[Stamp]", insert);
    }

    [Fact]
    public void Being_left_out_of_the_column_list_and_being_never_null_are_two_different_questions()
    {
        var columns = orm().Schemas.Contract<InventoryItem>().Columns;
        var sql = Ddl<InventoryItem>();
        var insert = orm().Insert(row()).ToSql();

        // 写侧"不进列清单"的是三列，存储侧"值归库给"的是两列，差的那一格就是 CheckedAt。
        // 次序照契约列序，所以期望值是死的 —— 换序就是有人动了契约，那是要看得见的事。
        Assert.Equal(
            [nameof(InventoryItem.Id), nameof(InventoryItem.RowVersion), nameof(InventoryItem.CheckedAt)],
            columns.Where(static c => !Writable(c)).Select(static c => c.MemberName).ToArray());
        Assert.Equal(
            [nameof(InventoryItem.Id), nameof(InventoryItem.RowVersion)],
            columns.Where(static c => c.ValueFromStore).Select(static c => c.MemberName).ToArray());

        // 两个判据各自只有一个读者，而这里比的是<em>产物</em>：列清单只认 Writable，DDL 的可空性只认 NullableInStore。
        // 复述判据的用例在实现改口径时会跟着一起改错，所以那句判据只出现在上面两个期望值旁边。
        Assert.All(columns, c => Assert.Equal(Writable(c), insert.Contains($"[{c.MemberName}]", StringComparison.Ordinal)));
        Assert.All(columns, c => Assert.Equal(c.NullableInStore, Nullability(Cell(sql, c.MemberName)) is "NULL"));
    }

    /// <summary>读那一列定义段的可空性尾巴。<b>先判 <c>NOT NULL</c> 再判 <c>NULL</c></b>，反过来的话
    /// <c>"NOT NULL".EndsWith(" NULL")</c> 为真 —— 这个坑是本用例第一次跑出来的：三列判反、两列恰好判对，
    /// 于是它长得像"契约与文本不一致"，实际是尾巴量法错了。</summary>
    static string Nullability(string cell)
    {
        if (cell.EndsWith(" NOT NULL", StringComparison.Ordinal))
            return "NOT NULL";

        if (cell.EndsWith(" NULL", StringComparison.Ordinal))
            return "NULL";

        throw new InvalidOperationException($"那一列的定义段既没有 NULL 也没有 NOT NULL：{cell}");
    }

    [Fact]
    public void The_storage_defaults_are_a_fixpoint_because_the_emission_layer_applies_them_again()
    {
        var once = orm().Schemas.Contract<InventoryItem>().Columns;
        var twice = once.Select(Provider.Types.ApplyStorageDefaults).ToArray();

        // 契约在收尾时已经跑过一遍 ApplyStorageDefaults（EntityContractFactory.Complete），
        // 而 EmitColumnDefinition 拿到列之后会<em>再</em>跑一遍。这一格如果做成"只有第一次才设"，
        // 同一份契约就会在两条 DDL 路径上自相矛盾（建表 NOT NULL、加列 NULL），
        // 而那两种文本出自同一次 CodeFirst 调用 —— 谁都不会想到要去比一比。
        Assert.Equal(once, twice);
    }

    /// <summary>写侧那句话的判据（<c>EntityTracker</c> 的 writable 位图）。这里写它是为了给出<em>期望值</em>，
    /// 不是拿它当断言：真正的断言是上面那句"列清单里有/没有这一列"。</summary>
    static bool Writable(ColumnContract column)
        => !column.IsPrimary && !column.IsIdentity && !column.IsConcurrencyToken;

    /// <summary>取建表文本里那一列的定义段。<b>只按 <c>", ["</c> 收口</b>，不按裸逗号：
    /// <c>DECIMAL(18,4)</c> 那种类型名里就有逗号，按逗号切会得到 <c>[Amount] DECIMAL(18</c>。</summary>
    static string Cell(string ddl, string member)
    {
        var start = ddl.IndexOf($"[{member}] ", StringComparison.Ordinal);

        Assert.True(start >= 0, $"建表文本里没有 [{member}]：{Environment.NewLine}{ddl}");

        var rest = ddl[(start + member.Length + 3)..];
        var tail = rest.IndexOf(", [", StringComparison.Ordinal);
        var constraint = rest.IndexOf(", CONSTRAINT", StringComparison.Ordinal);
        var end = new[] { tail, constraint, rest.Length }.Where(static i => i >= 0).Min();

        return rest[..end];
    }

    static string Ddl<TEntity>() where TEntity : class
    {
        var writer = new SqlWriter(new StringBuilder(), [], Provider.Dialect.ParameterPrefix);

        Provider.CodeFirst.EmitCreateTable(writer, orm().Schemas.Contract<TEntity>());

        return writer.ToSqlString();
    }

    [Fact]
    public void The_chained_update_refuses_the_column_that_only_compares()
    {
        // 与自增列同一道门：那一列在库里也不可赋值（T-SQL 的 rowversion 不接受写），
        // 放它过去只会得到一次往返之后的一句库错，而报错点离写它的人就远了。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Update<InventoryItem>().Set(i => i.RowVersion, new byte[] { 9, 9 }).Where(i => i.Id == 5).ToSql());

        Checks.Has("只比较、不书写", failure.Message);

        // 同一条链上的可写列照旧能写：拦的是那一列，不是这一格实体。
        Checks.Has("SET [Quantity] = @p0", orm().Update<InventoryItem>().Set(i => i.Quantity, 7).Where(i => i.Id == 5).ToSql());
    }

    // ── 脏判定：这一列从不进 SET，但它从不在比较之外 ───────────────────

    [Fact]
    public void Marking_the_whole_row_dirty_still_skips_the_column_that_only_compares()
    {
        var db = session();
        var item = row();

        db.Update(item);

        // State = Modified 是"整行都写"的说法，而"整行"从来不含主键、自增列与并发列 ——
        // 否则最常见的那一句 db.Update(e); SaveChanges() 会变成一句必抛。
        Assert.Equal(["Sku", "Quantity"], db.Entry(item).ModifiedMembers);
        Assert.False(db.Entry(item).Property(i => i.RowVersion).IsModified);
        Assert.False(db.Entry(item).Property(i => i.CheckedAt).IsModified);
    }

    [Fact]
    public void A_written_token_column_is_visible_even_though_it_never_enters_SET()
    {
        var db = session();
        var item = row();

        db.Attach(item);

        item.RowVersion = [3, 4];

        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        var token = db.Entry(item).Property(i => i.RowVersion);

        Assert.True(token.IsModified);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(token.OriginalValue));
        Assert.Equal(EntityState.Modified, db.Entry(item).State);

        // 但它进不了 SET，所以 ModifiedMembers 是空的 —— 而这一行仍然"欠着一次处理"。
        // 那两句话不矛盾，而 WillWrite 必须按后者答：SaveChanges 先看这一句决定要不要开事务，
        // 若按"有没有列进得了 SET"答，这一行会被当成干净的、整次保存提前返回空报告，
        // 于是「往并发列里写值」变成静默丢弃，而 §8.2 那条"只比较不书写"的拒绝成了走不到的死路。
        Assert.Empty(db.Entry(item).ModifiedMembers);
        Assert.True(db.Entry(item).IsModified);
        Assert.True(db.ChangeTracker.HasChanges);
    }

    // ── 第三族：应用给值（AppSetsValue，ABP 的 string ConcurrencyStamp 那一族）────

    [Fact]
    public void A_ConcurrencyStamp_member_is_a_token_the_app_fills_by_convention()
    {
        var column = orm().Schemas.Contract<StampedDoc>().Columns.Single(c => c.MemberName == nameof(StampedDoc.ConcurrencyStamp));

        // 名字约定只把第三族认出来（进 SET、比快照），不把一列凭空升成并发列：
        // 普通列、以及打标但没开 AppSetsValue 的并发列，那一格都是 false。
        Assert.True(column.IsConcurrencyToken);
        Assert.True(column.AppSetsValue);

        var plain = orm().Schemas.Contract<StampedDoc>().Columns.Single(c => c.MemberName == nameof(StampedDoc.Note));
        Assert.False(plain.IsConcurrencyToken);
        Assert.False(plain.AppSetsValue);

        var storeFilled = orm().Schemas.Contract<InventoryItem>().Columns.Single(c => c.MemberName == nameof(InventoryItem.RowVersion));
        Assert.True(storeFilled.IsConcurrencyToken);
        Assert.False(storeFilled.AppSetsValue);
    }

    [Fact]
    public void The_attribute_and_the_fluent_surface_declare_the_third_family_explicitly()
    {
        var attributed = orm().Schemas.Contract<ExplicitStamped>().Columns.Single(c => c.MemberName == nameof(ExplicitStamped.Stamp));

        Assert.True(attributed.IsConcurrencyToken);
        Assert.True(attributed.AppSetsValue);

        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.PaidAt).IsConcurrencyToken().AppSetsValue();

        var fluent = ormWith(model).Schemas.Contract<Order>().Columns.Single(c => c.MemberName == nameof(Order.PaidAt));

        Assert.True(fluent.IsConcurrencyToken);
        Assert.True(fluent.AppSetsValue);
    }

    [Fact]
    public void An_app_set_token_enters_the_insert_column_list_and_the_SET()
    {
        // 与「不进列清单」那两族相对：这一族的新值在实体上、由应用生成，必须随行写出去。
        var sql = orm().Insert(new StampedDoc { Id = 3, Title = "t", ConcurrencyStamp = "s1" }).ToSql();

        Checks.Has("([Title], [ConcurrencyStamp], [Note]) VALUES", sql);

        var db = session2();
        var doc = new StampedDoc { Id = 3, Title = "t", ConcurrencyStamp = "s1" };

        db.Attach(doc);
        doc.ConcurrencyStamp = "s2";

        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        // 与 int Version 那一族也不同：那一族是"库替我 +1"（实体不用先改），
        // 这一族是"我自己已换好值"—— 脏判定认得它，SET 里带的就是实体手里的新值。
        Assert.Equal(["ConcurrencyStamp"], db.Entry(doc).ModifiedMembers);

        // State = Modified 的整行写也含这一族（可写位图放行了它），同样不进 +1 改写 ——
        // 整行的口径是"可写列全脏"，所以普通列 Note 也在；对照 InventoryItem 那两颗用例，
        // 差的就是并发列这一格从"跳过"变"包含"。
        var whole = new StampedDoc { Id = 4, Title = "t", ConcurrencyStamp = "s1" };

        db.Update(whole);
        Assert.Equal(["Title", "ConcurrencyStamp", "Note"], db.Entry(whole).ModifiedMembers);
    }

    [Fact]
    public void An_app_set_token_does_not_break_the_other_two_families()
    {
        // 三族并排对账：库维护（RowVersion）照旧不进 SET/列清单；应用自维护整型（Count 那一族用显式声明验证分类）与
        // 应用给值（ConcurrencyStamp）进 SET。这里只判离线判得出的那一半：可写位图与列清单。
        var stamped = orm().Schemas.Contract<StampedDoc>().Columns;

        Assert.Equal(
            ["Id"],
            stamped.Where(c => !WritableWithThirdFamily(c)).Select(c => c.MemberName).ToArray());

        // 整型 + AppSetsValue 显式声明的列：分类上它归第三族（不回读、不 +1），可写照旧。
        var explicitStamped = orm().Schemas.Contract<ExplicitStamped>().Columns.Single(c => c.MemberName == nameof(ExplicitStamped.Stamp));

        Assert.True(explicitStamped.AppSetsValue);
    }

    /// <summary>第三族之后写侧那句话的判据（<c>EntityTracker</c> 的 writable 位图）。</summary>
    static bool WritableWithThirdFamily(ColumnContract column)
        => !column.IsPrimary && !column.IsIdentity && (!column.IsConcurrencyToken || column.AppSetsValue);

    static DbContext session2() => new(orm());
}

/// <summary>第三族的靶子（应用给值）：成员名 ConcurrencyStamp 命中约定，一个注解都不用加。
/// 形状就是 ABP <c>IHasConcurrencyStamp</c> 那一族实体的现成拼写。</summary>
[Table("QuickSqlStampedDoc")]
public class StampedDoc
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Title { get; set; }

    /// <summary>应用给值的并发戳：新值由应用生成（Guid 那一派），库拿快照旧值比 WHERE、把现值写进 SET。</summary>
    public string? ConcurrencyStamp { get; set; }

    /// <summary>普通可写列，与第三族并排才能判出"放行的只有那一族"。</summary>
    public string? Note { get; set; }
}

/// <summary>显式声明第三族的靶子：成员名不叫 ConcurrencyStamp，靠特性的 AppSetsValue 参数升档。</summary>
[Table("QuickSqlExplicitStamped")]
public class ExplicitStamped
{
    public int Id { get; set; }

    /// <summary>整型也能进第三族：显式声明优先于"整型 ⇒ +1"的缺省分类。</summary>
    [ConcurrencyToken(true)]
    public int Stamp { get; set; }

    [QuickColumn(Length = 16)]
    public string? Name { get; set; }
}

/// <summary>并发列而库里<em>没有</em>那一型的靶子（v0.24 那一档的负样本）。§8.2 明写过一句
/// "由调用方自己递增的版本号…不属于这一格" —— 那句话说的是语义，这里要的是它的存储形状：
/// CLR 是 <c>byte[]?</c>、类型名落本家默认的 <c>VARBINARY</c>、库里没有任何东西给它值，所以 DDL 必须写 <c>NULL</c>。
///
/// <para>它存在的理由是判据的形状：「值归库给」按<em>存储类型</em>认（<c>rowversion</c>），
/// 既不按 ClrType（<c>byte[]</c>）认、也不按"是不是并发列"认。后两条一刀切在这一列上都会红，
/// 而 <see cref="InventoryItem"/> 的 <c>RowVersion</c> 恰好三条判据全满足，拿它判不出该用哪一条。</para>
///
/// <para>放本文件而不是 Common：Common 里的类型都挂源生成器，而这一格要判的是"provider 落下的那一格输入"，
/// 与 G1/G2 那条分岔无关（<c>RelationshipTests</c> 那批靶子同一理由）。</para></summary>
[Table("QuickSqlAppVersioned")]
public class AppVersioned
{
    public int Id { get; set; }

    /// <summary>应用自己递增的版本号：写侧照旧不写（它是并发列），可空性照旧归 CLR。</summary>
    [ConcurrencyToken]
    public byte[]? Stamp { get; set; }

    /// <summary>一格普通可写列。没有它，这一行的 INSERT 会一个列都不剩 —— 那与本题无关，
    /// 却会让上面那句"列清单里没有 [Stamp]"红于一个错原因。</summary>
    [QuickColumn(Length = 16)]
    public string? Name { get; set; }
}
