using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Schema;
using QuickSql.TestInfra;
using QuickSql.Transactions;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>物化的无参构造器<b>不问可见性</b>（真库那一半）。
///
/// <para><b>为什么这一族要单独钉</b>：ABP/DDD 的实体标配是「无参构造只有自己能看见」——
/// <c>private X()</c> 或 <c>protected X()</c>，创建只能走有参构造或工厂，为的是不让外人 new 一只空实体。
/// 旧那一版把可见性当门槛（<c>Type.GetConstructor(Type.EmptyTypes)</c> 只交回公共的那一颗），
/// 后果不是"读不出来"而是<b>读写两侧一起拒</b>：实测过一颗 <c>internal X()</c> 的实体，
/// <c>Insert</c> 与 <c>Select</c> 响的是同一句"需要无参构造器才能被物化"（G2 的实例工厂与行读取器共用那份查找）。
/// 放宽之后这条界只剩"有没有"，所以本册钉四件事：① 非公共无参构造的实体读写都通；
/// ② 字段初始化器照跑（换成 <c>GetUninitializedObject</c> 就通不过这一条，而它的症状是集合永远空）；
/// ③ <c>IncludeMany</c> 往私有写口的集合里缝子行；④ 原生 SQL 的行出口同一条判据。</para>
///
/// <para><b>G1 生成侧不在这一格</b>：生成的是字面 <code>new T()</code>，非公共的编不出来，
/// 于是那种实体退到 G2 反射解析（QuickSql003 那条诊断说的是"少一份编译期事实册"），
/// 本册走的正是 G2 那一条。</para></summary>
public sealed class NonPublicConstructorPathTests
{
    /// <summary>聚合根：无参构造私有、属性只有私有写口、集合导航靠初始化器——ABP 实体的整形。</summary>
    [Table("QuickSqlPrivateCtorHeader")]
    public sealed class SealedHeader
    {
        public Guid Id { get; private set; }

        [QuickColumn(Length = 32)]
        public string? Code { get; private set; }

        [QuickColumn(Length = 32)]
        public string? Note { get; private set; }

        // 空集合就是证据本身：换成 GetUninitializedObject 装配，这一格会是 null 而不是"空但可追加"
        [ForeignKey(nameof(SealedLine.HeaderId))]
        public List<SealedLine> Lines { get; private set; } = [];

        private SealedHeader()
        {
        }

        public static SealedHeader Create(Guid id, string code, string? note = null)
        {
            return new SealedHeader { Id = id, Code = code, Note = note };
        }

        public void AddLine(Guid id, string label) => Lines.Add(new SealedLine(id, Id, label));
    }

    /// <summary>子行：无参构造是 <c>internal</c>（另一种非公共形状，与上面那枚分开钉）。</summary>
    [Table("QuickSqlPrivateCtorLine")]
    public sealed class SealedLine
    {
        public Guid Id { get; set; }

        public Guid HeaderId { get; set; }

        [QuickColumn(Length = 32)]
        public string? Label { get; set; }

        internal SealedLine()
        {
        }

        internal SealedLine(Guid id, Guid headerId, string label)
        {
            Id = id;
            HeaderId = headerId;
            Label = label;
        }
    }

    /// <summary>原生 SQL 的行出口形状：内部无参构造 + 公共可写属性。</summary>
    internal sealed class RawSeedRow
    {
        public Guid Id { get; set; }

        public string? Code { get; set; }

        internal RawSeedRow()
        {
        }
    }

    /// <summary>没有任何无参构造的形状：放宽的是可见性，不是"要不要存在"。</summary>
    [Table("QuickSqlNoCtorAtAll")]
    public sealed class HeaderWithoutParameterless(string code)
    {
        public Guid Id { get; private set; }

        [QuickColumn(Length = 32)]
        public string Code { get; private set; } = code;

        public void SetId(Guid id) => Id = id;
    }

    const string HeaderTable = "QuickSqlPrivateCtorHeader";
    const string LineTable = "QuickSqlPrivateCtorLine";
    const string EmptyTable = "QuickSqlNoCtorAtAll";

    static string Quote(string identifier) => Database.Provider.CodeFirst.Identifiers.Quote(identifier);

    static CancellationToken ct => TestContext.Current.CancellationToken;

    static async Task<QuickSqlOrm> NewStoreAsync()
    {
        Database.Ready();
        Database.Execute($"DROP TABLE IF EXISTS {Quote(HeaderTable)}");
        Database.Execute($"DROP TABLE IF EXISTS {Quote(LineTable)}");
        Database.Execute($"DROP TABLE IF EXISTS {Quote(EmptyTable)}");

        var orm = new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = Database.Provider,
            ConnectionString = Database.ConnectionString,
            SchemaSync = SchemaSyncPolicy.Execute,
            ForceSchemaSync = true,
        });

        orm.Schemas.Contract<SealedHeader>();
        orm.Schemas.Contract<SealedLine>();
        orm.Schemas.Contract<HeaderWithoutParameterless>();

        await orm.SyncStructureAsync(ct);

        return orm;
    }

    [Fact]
    public async Task A_private_parameterless_constructor_round_trips_both_ways()
    {
        var orm = await NewStoreAsync();

        var id = Guid.NewGuid();
        SealedHeader header = SealedHeader.Create(id, "AB-1", "私有构造那一族");
        header.AddLine(Guid.NewGuid(), "甲");
        header.AddLine(Guid.NewGuid(), "乙");

        // 写：实体是自己工厂造出来的，物化不参与——旧那一版在这里就响了
        await orm.Insert(header).ExecuteAffrowsAsync(ct);
        await orm.InsertRange(header.Lines).ExecuteAffrowsAsync(ct);

        // 读：G2 的行读取器装配同一形状
        List<SealedHeader> rows = await orm
            .Select<SealedHeader>()
            .Where(x => x.Id == id)
            .ToListAsync(ct);

        Assert.Single(rows);
        Assert.Equal("AB-1", rows[0].Code);
        Assert.Equal("私有构造那一族", rows[0].Note);
        Assert.Empty(rows[0].Lines);

        var lines = await orm
            .Select<SealedLine>()
            .Where(x => x.HeaderId == id)
            .ToListAsync(ct);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["甲", "乙"], lines.Select(x => x.Label).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Field_initializers_run_so_the_collection_slot_is_never_null()
    {
        var orm = await NewStoreAsync();

        // 库里一颗子行都没有：物化出来的那一只实体，集合位必须是初始化器给的那一只，
        // 而不是 null —— GetUninitializedObject 那一手能"装配成功"却把这一格留空
        var absent = Guid.NewGuid();

        SealedHeader? materialized = await orm
            .Select<SealedHeader>()
            .Where(x => x.Id == absent)
            .FirstOrDefaultAsync(ct);

        Assert.Null(materialized);

        var id = Guid.NewGuid();
        await orm.Insert(SealedHeader.Create(id, "AB-2")).ExecuteAffrowsAsync(ct);

        SealedHeader? row = await orm
            .Select<SealedHeader>()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);

        Assert.NotNull(row);

        // 这一格就是"装配跑过构造器"的证据：GetUninitializedObject 交回的是 null 集合
        Assert.NotNull(row.Lines);
        Assert.Empty(row.Lines);

        // 缝上去的那两颗用的是追加口，所以那一只集合必须一直可用
        await orm.Insert(new SealedLine(Guid.NewGuid(), id, "丙")).ExecuteAffrowsAsync(ct);

        // 缝合只在批量出口那一条路上做（流式枚举不缝集合），所以这一发用链上的 FirstOrDefaultAsync
        SealedHeader? stitched = await orm
            .Select<SealedHeader>()
            .IncludeMany(x => x.Lines)
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);

        Assert.NotNull(stitched);

        Assert.Equal(["丙"], stitched!.Lines.Select(x => x.Label).ToArray());
    }

    [Fact]
    public async Task Raw_sql_row_reader_accepts_an_internal_parameterless_constructor()
    {
        var orm = await NewStoreAsync();

        var id = Guid.NewGuid();
        await orm.Insert(SealedHeader.Create(id, "AB-3", "原生口")).ExecuteAffrowsAsync(ct);

        string sql =
            $"SELECT {Quote(nameof(SealedHeader.Id))} AS {Quote(nameof(RawSeedRow.Id))}, "
            + $"{Quote(nameof(SealedHeader.Code))} AS {Quote(nameof(RawSeedRow.Code))} "
            + $"FROM {Quote(HeaderTable)}";

        List<RawSeedRow> rows = await orm.Ado.QueryAsync<RawSeedRow>(sql, null, ct);

        Assert.Single(rows);
        Assert.Equal(id, rows[0].Id);
        Assert.Equal("AB-3", rows[0].Code);
    }

    [Fact]
    public async Task A_type_without_any_parameterless_constructor_is_still_refused_with_an_accurate_message()
    {
        var orm = await NewStoreAsync();

        var exception = await Record.ExceptionAsync(() =>
            orm.Select<HeaderWithoutParameterless>().ToListAsync(ct)
        );

        Assert.NotNull(exception);

        var innermost = exception!;
        while (innermost.InnerException is not null)
            innermost = innermost.InnerException;

        Assert.Contains("没有无参构造器", innermost.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HeaderWithoutParameterless), innermost.Message, StringComparison.Ordinal);
    }
}
