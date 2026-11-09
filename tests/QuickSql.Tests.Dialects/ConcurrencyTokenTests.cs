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

/// <summary>乐观并发列（§8.2）在<b>离线</b>能判的那一半。分界线跟 <see cref="TrackingTests"/> 同一条：
/// <b>这一份里没有任何一句 SQL 上线</b>，全程 <see cref="SqlServer.NoDriverFactory"/>。
/// rowversion 支持已移除：令牌只有应用自维护两族——整型 <c>Version</c>（ORM 在 UPDATE 里 +1）
/// 与 <c>AppSetsValue</c>（应用给新值）。这里判「元数据 → 列清单 → 脏判定」三层各自的事实。
///
/// <para><b>这里判不到的三件事都归集成层</b>（<c>ConcurrencyPathTests</c>）：<c>WHERE</c> 里那句并发谓词的文本
/// （含 NULL 折成 <c>IS NULL</c> 那一档）、0 行时补查回来的现值与 <c>RowIsGone</c> 的分类、
/// 以及一次真实保存里 Version 与库里的同步。三者都要活库。</para></summary>
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
    };

    static DbContext session() => new(orm());

    // ── 元数据：两格来源与 origin ────────────────────────────────────

    [Fact]
    public void The_annotations_mark_the_columns_and_say_so_in_the_origin()
    {
        var columns = orm().Schemas.Contract<InventoryItem>().Columns;

        // 两格都是契约列序里的 token：Version 是整型 +1 那一族，CheckedAt 是应用给值那一族。
        Assert.Equal(
            [nameof(InventoryItem.Version), nameof(InventoryItem.CheckedAt)],
            columns.Where(c => c.IsConcurrencyToken).Select(c => c.MemberName).ToArray());

        var integerToken = columns.Single(c => c.MemberName == nameof(InventoryItem.Version));

        Assert.True(integerToken.IsConcurrencyToken);
        Assert.False(integerToken.AppSetsValue);
        Assert.Equal("INT", integerToken.StoreTypeName);
        Checks.Has("; 并发 ← [ConcurrencyToken]", integerToken.Origin.Note);

        var appSetToken = columns.Single(c => c.MemberName == nameof(InventoryItem.CheckedAt));

        Assert.True(appSetToken.IsConcurrencyToken);
        Assert.True(appSetToken.AppSetsValue);

        // 没标这一格的列，origin 那串文本一个字都不该动。
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
        model.Entity<InventoryItem>().Property(i => i.Version).IsConcurrencyToken(false);

        var schemas = ormWith(model).Schemas;
        var added = schemas.Contract<Order>().Columns.Single(c => c.MemberName == nameof(Order.PaidAt));
        var taken = schemas.Contract<InventoryItem>().Columns.Single(c => c.MemberName == nameof(InventoryItem.Version));

        Assert.True(added.IsConcurrencyToken);
        Checks.Has("; 并发 ← Fluent IsConcurrencyToken", added.Origin.Note);

        // 反向那一半才是"层序"的证据：只测"Fluent 能加上"的话，注解压住 Fluent 的实现也会全绿。
        Assert.False(taken.IsConcurrencyToken);
        Checks.Absent("并发", taken.Origin.Note);
    }

    // ── 「不书写」在写链上的形状 ─────────────────────────────────────

    [Fact]
    public void An_integer_token_stays_out_of_the_insert_column_list()
    {
        var sql = orm().Insert(row()).ToSql(out var parameters);

        // Id 是自增、Version 是整型并发列：两列都不进列清单。CheckedAt 是 AppSetsValue，
        // 新值在实体上（这里是 null），按第三族的规则随行写出去。
        Checks.Has("INSERT INTO [QuickSqlInventoryItem] ([Sku], [Quantity], [CheckedAt], [Exception]) VALUES (@p0, @p1, @p2, @p3)", sql);
        Checks.Absent("[Id]", sql);
        Checks.Absent("[Version]", sql);

        Assert.Equal(4, parameters.Count);
        Assert.Equal(["Sku", "Quantity", "CheckedAt", "Exception"], parameters.Select(p => p.Hint?.MemberName).ToArray());
    }

    [Fact]
    public void The_key_only_backfill_slot_still_returns_only_the_key()
    {
        var insert = orm().Insert(row());

        // ExplainKeys() 对应 ExecuteReturnKeysAsync：那一档的语义是"把自增主键给我"。
        // 并发令牌全是应用自维护的，不进回填集。
        Checks.Has(") OUTPUT inserted.[Id] VALUES (", insert.ExplainKeys().ToSql());
        Checks.Absent("OUTPUT inserted.[Version]", insert.ExplainKeys().ToSql());
        Checks.Absent("OUTPUT inserted.[CheckedAt]", insert.ExplainKeys().ToSql());
        Checks.Absent("OUTPUT", insert.Explain().ToSql());
    }

    [Fact]
    public void The_create_table_text_carries_the_portable_token_shape()
    {
        var sql = Ddl<InventoryItem>();

        // 整型 Version：NOT NULL，初始值由列默认值 0 给（它不进 INSERT 列清单）。
        Checks.Has("[Version] INT NOT NULL DEFAULT 0", sql);

        // AppSetsValue 那一格：CLR 可空 ⇒ 库里可空。
        Checks.Has("[CheckedAt] DATETIME2(3) NULL", sql);

        // 其它列对位。
        Checks.Has("[Id] INT IDENTITY(1,1) NOT NULL", sql);
        Checks.Has("[Sku] NVARCHAR(32) NULL", sql);
        Checks.Has("[Quantity] INT NOT NULL", sql);
    }

    /// <summary>写侧那句话的判据（<c>EntityTracker</c> 的 writable 位图）。</summary>
    static bool Writable(ColumnContract column)
        => !column.IsPrimary && !column.IsIdentity && (!column.IsConcurrencyToken || column.AppSetsValue);

    [Fact]
    public void Being_left_out_of_the_column_list_and_being_never_null_are_two_different_questions()
    {
        var columns = orm().Schemas.Contract<InventoryItem>().Columns;
        var sql = Ddl<InventoryItem>();
        var insert = orm().Insert(row()).ToSql();

        // 写侧"不进列清单"的是 Id 与 Version；存储侧"值归库给"的只有 Id。
        Assert.Equal(
            [nameof(InventoryItem.Id), nameof(InventoryItem.Version)],
            columns.Where(static c => !Writable(c)).Select(static c => c.MemberName).ToArray());
        Assert.Equal(
            [nameof(InventoryItem.Id)],
            columns.Where(static c => c.ValueFromStore).Select(static c => c.MemberName).ToArray());

        // 列清单只认 Writable；DDL 的可空性只认 NullableInStore。
        Assert.All(columns, c => Assert.Equal(Writable(c), insert.Contains($"[{c.MemberName}]", StringComparison.Ordinal)));
        Assert.All(columns, c => Assert.Equal(c.NullableInStore, Nullability(Cell(sql, c.MemberName)) is "NULL"));
    }

    /// <summary>读那一列定义段的可空性。<b>先判 <c>NOT NULL</c> 再判 <c>NULL</c></b>：
    /// 列默认值挂在可空性之后（<c>NOT NULL DEFAULT 0</c>），所以不能只看结尾。</summary>
    static string Nullability(string cell)
    {
        if (cell.Contains(" NOT NULL", StringComparison.Ordinal))
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

        Assert.Equal(once, twice);
    }

    /// <summary>取建表文本里那一列的定义段。<b>只按 <c>", ["</c> 收口</b>，不按裸逗号。</summary>
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
    public void The_chained_update_refuses_the_integer_token()
    {
        // 整型 Version 由 ORM 在 UPDATE 里 +1，调用方不能直接点它写。
        var failure = Checks.Raised<QuickSqlException>(
            () => orm().Update<InventoryItem>().Set(i => i.Version, 9).Where(i => i.Id == 5).ToSql());

        Checks.Has("只比较、不书写", failure.Message);

        // 同一条链上的可写列照旧能写：拦的是那一列，不是这一格实体。
        Checks.Has("SET [Quantity] = @p0", orm().Update<InventoryItem>().Set(i => i.Quantity, 7).Where(i => i.Id == 5).ToSql());
    }

    // ── 脏判定：整型令牌从不进 SET，但它从不在比较之外 ───────────────────

    [Fact]
    public void Marking_the_whole_row_dirty_still_skips_the_integer_token()
    {
        var db = session();
        var item = row();

        db.Update(item);

        // State = Modified 是"整行都写"的说法，而"整行"不含主键、自增列与整型令牌；
        // AppSetsValue 那一族（CheckedAt）可写，所以它在名单里。
        Assert.Equal(["Sku", "Quantity", "CheckedAt", "Exception"], db.Entry(item).ModifiedMembers);
        Assert.False(db.Entry(item).Property(i => i.Version).IsModified);
        Assert.True(db.Entry(item).Property(i => i.CheckedAt).IsModified);
    }

    [Fact]
    public void A_written_integer_token_is_visible_even_though_it_never_enters_SET()
    {
        var db = session();
        var item = row();

        db.Attach(item);

        item.Version = 9;

        Assert.Equal(1, db.ChangeTracker.DetectChanges());

        var token = db.Entry(item).Property(i => i.Version);

        Assert.True(token.IsModified);
        Assert.Equal(0, Assert.IsType<int>(token.OriginalValue));
        Assert.Equal(EntityState.Modified, db.Entry(item).State);

        // 它进不了 SET，所以 ModifiedMembers 是空的 —— 而这一行仍然"欠着一次处理"。
        Assert.Empty(db.Entry(item).ModifiedMembers);
        Assert.True(db.Entry(item).IsModified);
        Assert.True(db.ChangeTracker.HasChanges);
    }

    // ── 第三族：应用给值（AppSetsValue，ABP 的 string ConcurrencyStamp 那一族）────

    [Fact]
    public void A_ConcurrencyStamp_member_is_a_token_the_app_fills_by_convention()
    {
        var column = orm().Schemas.Contract<StampedDoc>().Columns.Single(c => c.MemberName == nameof(StampedDoc.ConcurrencyStamp));

        Assert.True(column.IsConcurrencyToken);
        Assert.True(column.AppSetsValue);

        var plain = orm().Schemas.Contract<StampedDoc>().Columns.Single(c => c.MemberName == nameof(StampedDoc.Note));
        Assert.False(plain.IsConcurrencyToken);
        Assert.False(plain.AppSetsValue);

        var integerToken = orm().Schemas.Contract<InventoryItem>().Columns.Single(c => c.MemberName == nameof(InventoryItem.Version));
        Assert.True(integerToken.IsConcurrencyToken);
        Assert.False(integerToken.AppSetsValue);
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
        var sql = orm().Insert(new StampedDoc { Id = 3, Title = "t", ConcurrencyStamp = "s1" }).ToSql();

        Checks.Has("([Title], [ConcurrencyStamp], [Note]) VALUES", sql);

        var db = session();
        var doc = new StampedDoc { Id = 3, Title = "t", ConcurrencyStamp = "s1" };

        db.Attach(doc);
        doc.ConcurrencyStamp = "s2";

        Assert.Equal(1, db.ChangeTracker.DetectChanges());
        Assert.Equal(["ConcurrencyStamp"], db.Entry(doc).ModifiedMembers);

        var whole = new StampedDoc { Id = 4, Title = "t", ConcurrencyStamp = "s1" };

        db.Update(whole);
        Assert.Equal(["Title", "ConcurrencyStamp", "Note"], db.Entry(whole).ModifiedMembers);
    }

    [Fact]
    public void An_app_set_token_does_not_break_the_integer_family()
    {
        var stamped = orm().Schemas.Contract<StampedDoc>().Columns;

        Assert.Equal(
            ["Id"],
            stamped.Where(c => !WritableWithThirdFamily(c)).Select(c => c.MemberName).ToArray());

        var explicitStamped = orm().Schemas.Contract<ExplicitStamped>().Columns.Single(c => c.MemberName == nameof(ExplicitStamped.Stamp));

        Assert.True(explicitStamped.AppSetsValue);
    }

    /// <summary>第三族之后写侧那句话的判据。</summary>
    static bool WritableWithThirdFamily(ColumnContract column)
        => !column.IsPrimary && !column.IsIdentity && (!column.IsConcurrencyToken || column.AppSetsValue);

    // ── 非整型又没有 AppSetsValue 的令牌：启动期正面拒 ──────────────────

    [Fact]
    public void A_non_integer_token_without_app_set_value_is_refused_at_tracker_construction()
    {
        var db = new DbContext(orm());

        var failure = Checks.Raised<QuickSqlException>(() =>
        {
            _ = db.Set<BadToken>();

            return 0;
        });

        Checks.Has("没有可用的令牌维护方式", failure.Message);
        Checks.Has("ConcurrencyToken(true)", failure.Message);
    }
}

/// <summary>第三族的靶子（应用给值）：成员名 ConcurrencyStamp 命中约定，一个注解都不用加。</summary>
[Table("QuickSqlStampedDoc")]
public class StampedDoc
{
    public int Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? Title { get; set; }

    /// <summary>应用给值的并发戳：新值由应用生成，库拿快照旧值比 WHERE、把现值写进 SET。</summary>
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

/// <summary>非整型、又没标 AppSetsValue 的负样本：rowversion 支持移除后，这一档没有可用的令牌维护方式，
/// 在 <c>ChangeTracker.For&lt;T&gt;()</c> 构造那一格正面拒，而不是等到第一次保存才静默不成。</summary>
[Table("QuickSqlBadToken")]
public class BadToken
{
    public int Id { get; set; }

    [ConcurrencyToken]
    public byte[]? Stamp { get; set; }

    [QuickColumn(Length = 16)]
    public string? Name { get; set; }
}