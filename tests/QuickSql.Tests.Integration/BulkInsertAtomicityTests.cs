using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using QuickSql.Providers;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>多块插入的原子性。2026-11 之前这条链是"跨块不是原子的（§12.4 第 8 项）"——每块各自
/// autocommit，中途失败半批已提交、半批丢失；plan-cross-orm-bench.md §4.4 在活库上量出这笔交付方式的
/// 价之后，InsertEngine 对多块包一条事务整批提交。本颗用例钉的就是这个语义：前两块已经落库、
/// 第三块撞主键 ⇒ 异常向上、库里一行不剩。
///
/// <para><b>失败怎么注入的</b>：显式主键（<c>[QuickColumn(IsIdentity = TriState.No)]</c> 让 INSERT
/// 带上 Id——ApplicationKeyConventionTests 的 GivenKeyRow 同一颗写法），主键基址按批次随机取
/// （<b>键空间必须与本批隔离</b>：靶表里躺着此前各场留下的行，固定基址会让第一块就撞上别场的键——
/// 第一版就是这么"绿"的：异常在第一块、库里 0 行，撤掉事务反验也绿，那颗绿是假绿），第 250 行改成与
/// 第 150 行同键——第三块在库端撞主键，而库端的失败谁也提前拦不住，这正是要测的形状。
/// <c>rowsPerRoundTrip: 100</c> 把块宽钉在 100（12 块），与各家的参数预算无关，六家跑的是同一颗形状。</para>
///
/// <para><b>反验</b>：把 SendAtomicallyAsync 的事务分支撤掉，这一颗当场红（库里躺着前两块的 198 行）——
/// 那正是旧形状。<b>裁判纪律</b>与全层同一条：终审裸 ADO（<see cref="Database.Rows"/>），不用被测的读口
/// 自证清白。未声明事务位的 provider 会走 autocommit 直发（"事务可选"条款），那一形状在这里就是红——
/// 六家今天都声明了。</para></summary>
public sealed class BulkInsertAtomicityTests
{
    [Fact]
    public async Task Multi_chunk_insert_rolls_back_as_a_whole_when_a_chunk_violates_a_key()
    {
        Database.Ready();
        Database.EnsureTable(typeof(AtomicRow));

        var batch = "qsql-atomic-" + Guid.NewGuid().ToString("N")[..8];
        var idBase = Random.Shared.Next(1_000_000, 9_000_000);
        var rows = new List<AtomicRow>(1_200);

        for (var i = 0; i < 1_200; i++)
            rows.Add(new AtomicRow { Id = idBase + i, Batch = batch, No = i });

        // 第 249 号位与第 149 号位同键：第三块（下标 198..296）撞主键，而前两块已经发出去。
        rows[249].Id = rows[149].Id;

        // 判的是回滚不是分类：各家的违键异常形态不同（虚谷交回裸的驱动异常，没进本家的分类链），
        // 所以这里只断言"有异常向上"，类型那一半由 IntegrityErrorTranslationPathTests 守。
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await Database.Orm.InsertRange(rows)
                .WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: 100)
                .ExecuteAffrowsAsync(TestContext.Current.CancellationToken);
        });

        // 终审裸 ADO：整批回滚 ⇒ 本批一行都不许在；半批提交的旧形状在这里就是 198 行。
        var left = Database.Rows("SELECT COUNT(*) AS Cnt FROM [dbo].[QuickSqlAtomicRow] WHERE [Batch] = @p", ("@p", batch));

        Assert.Equal("0", left[0]["Cnt"]);
    }
}

[Table("QuickSqlAtomicRow")]
public class AtomicRow
{
    // 显式主键要的是本家的声明（DataAnnotations 的 DatabaseGeneratedOption.None 不在本家的解析链上，
    // ApplicationKeyConventionTests 的 GivenKeyRow 同一颗写法）：整型 Id 不按自增约定省列，号原样发出去。
    [QuickColumn(IsIdentity = TriState.No)]
    public int Id { get; set; }

    public string? Batch { get; set; }

    public int No { get; set; }
}
