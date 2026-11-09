namespace QuickSql.Write;

/// <summary>orm.InsertOrUpdate().SetSource() 的链：按主键一句决定"没有就插、有就改"——每一行把除主键、
/// 库里给值的列与并发列以外的全部列写进去，主键既是插入清单的一部分、又是冲突判定的键。
/// 它是 IUpdateBulk 的 Upsert 档那份能力的一等公民入口（同一句文本、同一把列尺子），拆成独立一条链
/// 是为了让"我要 upsert"这件事在方法名上就写出来。两种发射形态都走这一条链，调用方不选：
/// 句尾族（ON DUPLICATE KEY UPDATE、ON CONFLICT DO UPDATE）或整语句族（MERGE INTO）由 provider 与方言
/// 自己定，两家都拿不到时这一条链在这一家当场拒；主键由库给值（自增、序列）而这一行没带键时也当场拒——
/// 那种键的冲突判定出不来。入口零反射税：列清单来自契约、值来自编译好的读写委托，一个表达式树都不建，
/// 连全局过滤器都不叠（在场即拒）。</summary>
public interface IInsertOrUpdate<T> where T : class
{
    /// <summary>交一只实体。链是可复用的（SetSource 换一批再执行一次），但一条链只算一次取值段的
    /// AuditValue：与"一个插入对象只算一次列值"同一条纪律，所以 ToSql 与紧随的 ExecuteAffrowsAsync 是同一条语句。</summary>
    IInsertOrUpdate<T> SetSource(T entity);

    /// <summary>交一批实体（立刻物化：看到的那份数据与发出去的那份必须是同一份）。
    /// 多行按参数预算分块，跨块不是原子的，见 WithBatchTransaction。</summary>
    IInsertOrUpdate<T> SetSource(IEnumerable<T> entities);

    /// <summary>这一批要不要一次提交（默认 true）。判据是语句数而不是块数：一句 upsert 本身就原子，
    /// 包它只多付两次往返，只有这批被切成两句以上时才自开一条事务。
    /// 传 false 回到"每句自己提交、半批可以在库里落地"。</summary>
    IInsertOrUpdate<T> WithBatchTransaction(bool atomic = true);

    /// <summary>不连库看这批会发出的命令文本（发射形态已按这一家的句尾或 MERGE 落定）。
    /// 这一颗不交全局过滤器那笔 RUC/RDC 税：这条链的句子里没有 WHERE 可叠，过滤器在场是当场拒而不是叠加，
    /// 它一次表达式翻译都不做。</summary>
    BulkWritePlan Explain();

    /// <summary>不连库；只有单条命令时可取，口径与 IInsert.ToSql 相同。</summary>
    string ToSql();

    /// <summary>逐块内联字面量，多块时带 -- 命令 n 分节：交付给人审阅的文本必须能看出有几条语句。</summary>
    string ToLogSql();

    /// <summary>受影响行数（多块时各块之和，按驱动口径）。这一颗不回填键：一句 upsert 的回填那一档
    /// 各家形态不一（MERGE OUTPUT、ON CONFLICT RETURNING），今天都不在这一条链上，
    /// 要键走 IInsert.ExecuteReturnKeysAsync 或先插后查。</summary>
    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);
}
