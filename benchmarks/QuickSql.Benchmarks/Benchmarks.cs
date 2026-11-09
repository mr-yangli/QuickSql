using System.Data;
using BenchmarkDotNet.Attributes;
using Dapper;
using QuickSql.Benchmarks.Offline;
using QuickSql.Metadata;
using QuickSql.Schema;

namespace QuickSql.Benchmarks;

/// <summary>§12.8 指定的七个基准名<b>全部能跑</b>：最后一格 <c>ChangeTrackerDetect10k</c> 由 v0.17 补上
/// （见 <see cref="ChangeTrackerBench"/>），倒数第二格 <c>BulkInsert10k</c> 是 v0.12 随写路径落地的
/// （见 <see cref="BulkInsertBench"/>，那里写着它的行数为什么取 1000）。
/// 不在这里放空转的基准方法：它会在报告里占一行，而那一行的含义是"已经量过了"。
/// <para>这里与 <see cref="Gates"/> 的分工：门禁给红绿（几秒、退出码），本文件给人看的对比数字
/// （BDN 的显著性与交叉验证是判据用不起的，也是 PR 附件该带的）。两边跑的是同一批调用形状。</para>
/// <para><b>派生类不能 <c>sealed</c></b>：BDN 靠生成子类来跑基准，验证阶段会把 sealed 的类整批判成无效
/// 并且只打一行提示，看起来像"跑完了但没数字"。</para></summary>
[MemoryDiagnoser]
public abstract class BenchBase
{
    protected const int Rows = 1_001;

    protected IEntitySchemaRegistry Generated { get; private set; } = null!;

    protected IEntitySchemaRegistry Reflected { get; private set; } = null!;

    protected QuickSqlOrm Orders { get; private set; } = null!;

    protected QuickSqlOrm Wide { get; private set; } = null!;

    protected DataTable WideTable { get; private set; } = null!;

    protected DataTable OrderTable { get; private set; } = null!;

    /// <summary>BDN 一个类只允许一个 <c>[GlobalSetup]</c>，所以派生类扩展这里是 override 而不是再加一个。
    /// <para><b>假结果集一律在这里建好</b>：resolver 是每次执行命令时调一次，写成 <c>_ => Harness.Table(...)</c>
    /// 就把"造 1001 行 DataTable"计进了被测量 —— 那会让 <c>SelectSimple</c> 报出一个仍然"看着正常"的数字。</para></summary>
    [GlobalSetup]
    public virtual void Setup()
    {
        var provider = Harness.Provider(_ => null);

        Generated = Harness.Generated(provider);
        Reflected = Harness.Reflected(provider);

        WideTable = Harness.Table(Generated, typeof(WideRow), Rows);
        OrderTable = Harness.Table(Generated, typeof(BenchOrder), Rows);

        Wide = Harness.Orm(_ => WideTable, Generated);
        Orders = Harness.Orm(_ => OrderTable, Generated);
    }
}

/// <summary>基准名 <c>MetadataLookup</c>（§12.8 第 5、7 行）。G1 与 G2 各测一次同样的三件事：
/// 取契约、取 schema、取读取委托 —— 也就是"缓存命中之后每次查询要付的元数据税"。</summary>
[MemoryDiagnoser]
public class MetadataLookupBench : BenchBase
{
    [Benchmark(Baseline = true)]
    public EntityContract G1_Contract() => Generated.Contract<BenchOrder>();

    [Benchmark]
    public IEntityTypeSchema<BenchOrder> G1_Schema() => Generated.Schema<BenchOrder>();

    [Benchmark]
    public Func<System.Data.Common.DbDataReader, ReadPlan, object> G1_ReadDelegate() => Generated.ReadDelegate(typeof(BenchOrder));

    [Benchmark]
    public EntityContract G2_Contract() => Reflected.Contract<BenchOrder>();

    [Benchmark]
    public IEntityTypeSchema<BenchOrder> G2_Schema() => Reflected.Schema<BenchOrder>();

    [Benchmark]
    public Func<System.Data.Common.DbDataReader, ReadPlan, object> G2_ReadDelegate() => Reflected.ReadDelegate(typeof(BenchOrder));
}

/// <summary>基准名 <c>SqlBuild100</c>（§12.8 第 4 行）。链在 setup 里就搭好，
/// 量的只是"从模型到 SQL 文本"这一步 —— 把建链也计进来就会变成量闭包分配。</summary>
[MemoryDiagnoser]
public class SqlBuildBench : BenchBase
{
    Func<string> _hundred = null!;

    Func<string> _one = null!;

    public override void Setup()
    {
        base.Setup();

        static Func<string> Chain(int predicates)
        {
            var orm = Harness.Orm(_ => null);

            return () =>
            {
                var query = orm.Select<BenchOrder>();

                for (var i = 0; i < predicates; i++)
                    query = query.Where(o => o.Id > i);

                return query.ToSql();
            };
        }

        _hundred = Chain(100);
        _one = Chain(1);
    }

    [Benchmark(Description = "SqlBuild100（100 步 Where → ToSql）")]
    public string Hundred() => _hundred();

    [Benchmark(Description = "SqlBuild1（同一条链的截距）")]
    public string One() => _one();
}

/// <summary>基准名 <c>SelectSimple</c> / <c>SelectJoin3</c> / <c>MaterializeWide</c>（§12.8 第 1、2、3 行）。
/// Dapper 那一条与 <c>MaterializeWide</c> 同表同驱动同进程，所以报告里的 Ratio 列直接就是第 2 行要的那个百分比。</summary>
[MemoryDiagnoser]
public class MaterializeBench : BenchBase
{
    QuickSqlOrm _join = null!;

    QuickSqlOrm _dto = null!;

    public override void Setup()
    {
        base.Setup();

        var table = JoinTable();
        var triple = TripleTable();

        _join = Harness.Orm(_ => table, Generated);
        _dto = Harness.Orm(_ => triple, Generated);
    }

    [Benchmark(Description = "SelectSimple（BenchOrder 1001 行整行）")]
    public Task<List<BenchOrder>> SelectSimple() => Orders.Select<BenchOrder>().ToListAsync();

    [Benchmark(Description = "SelectJoin3（三表 + 三列投影 1001 行）")]
    public Task<List<Line>> SelectJoin3() => Join();

    [Benchmark(Baseline = true, Description = "MaterializeWide（30 列 1001 行，G1）")]
    public Task<List<WideRow>> MaterializeWide() => Wide.Select<WideRow>().ToListAsync();

    /// <summary><b>这一条是 <see cref="SelectJoin3"/> 的对照，为的是把"贵"归给谁问清楚</b>：它与
    /// <see cref="SelectSimple"/> 同表同链，只差"投影成 DTO"这一步；与 <see cref="SelectJoin3"/> 只差那两张联进来的表。
    /// 于是三行的数一读就能分开"每行成本"与"每次执行的开销"：DTO 侧的行数、列数都比实体侧少，
    /// 若它仍然慢一个数量级，多出来的那笔就不可能来自行，只能来自"每次执行都要装配一次投影委托"
    /// （<c>QueryEngine.cs:403</c> 的 <c>Reader&lt;T&gt;(model)</c> 没有指纹缓存，而 §12.7 C 第一条要的就是它）。
    /// 换掉原先那个"同一条 DTO 查询连跑十次"的探针是必要的：<b>它不判别任何东西</b> —— BDN 的预热已经把每次执行的
    /// 编译与每行的成本一起摊平了，跑十次当然还是十份，那个数无法把"编译没缓存"与"委托本身慢"分开。</summary>
    [Benchmark(Description = "SelectSimpleDto（同一条单表链，只多一步 DTO 投影）")]
    public Task<List<Triple>> SelectSimpleDto() =>
        _dto.Select<BenchOrder>().Select(o => new Triple(o.Id, o.CustomerId, o.Amount)).ToListAsync();

    Task<List<Line>> Join() => _join.Select<BenchOrder>()
        .InnerJoin<BenchCustomer>((o, c) => o.CustomerId == c.Id)
        .InnerJoin<BenchItem>((o, c, i) => i.OrderId == o.Id)
        .OrderBy((o, c, i) => o.Id)
        .ToListAsync((o, c, i) => new Line(o.Id, c.City, i.Quantity));

    [Benchmark(Description = "MaterializeWide 对照组：Dapper QueryAsync<WideRow>")]
    public async Task<List<WideRow>> MaterializeWideDapper()
        => (await new OfflineConnection(_ => WideTable).QueryAsync<WideRow>("select * from [BenchWideRow]")).ToList();

    /// <summary>假结果集与 SQL 是各写各的，所以"投影列数变了但样本没跟上"必须响（<see cref="Selected"/>），
    /// 不能安静地量出一个看起来正常的数字。
    /// <para>两处口径与 <see cref="SelectJoin3"/> 不完全重合，都是被签名逼的：① 拿<b>带投影</b>的那条 SQL 来数，
    /// 联表链不带投影的 <c>ToSql()</c> 发射的是整行（八列），列裁剪发生在 <c>Select(projector)</c> 之后 ——
    /// 这句是断言第一次跑就炸出来的，它当场拦下了我按整行写死的 3 列假设；② 这里<b>不带 <c>OrderBy</c></b>，
    /// 因为 <c>IOrderedJoinQuery&lt;T1,T2,T3&gt;</c> 只有 <c>ToListAsync(projector)</c> 而没有 <c>Select</c>
    /// （§16.14 那一族的第四个实例：<b>有序联表投影没有离线出口</b>，与 §13.9 的聚合剥序是同一个形状）。
    /// 于是这条断言量的是"投影段的列数"，而序段按 §16.15 的实测不进投影，两者互不影响。</para></summary>
    static DataTable JoinTable()
    {
        var orm = Harness.Orm(_ => null);

        var sql = orm.Select<BenchOrder>()
            .InnerJoin<BenchCustomer>((o, c) => o.CustomerId == c.Id)
            .InnerJoin<BenchItem>((o, c, i) => i.OrderId == o.Id)
            .Select((o, c, i) => new Line(o.Id, c.City, i.Quantity))
            .ToSql();

        Selected(sql, 3);

        var table = new DataTable();

        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("City", typeof(string));
        table.Columns.Add("Quantity", typeof(int));

        for (var row = 0; row < Rows; row++)
            table.Rows.Add(row, "city" + (row & 63), row % 7);

        return table;
    }

    /// <summary><see cref="SelectSimpleDto"/> 的三列假结果集，按<b>投影之后</b>的列序建：
    /// DTO 的槽位读的是结果集序号 0..n-1（<c>Materializer.Assemble</c>），所以拿实体那张八列表喂它会串位 ——
    /// 第 1 个槽读走 `CustomerName`，于是"类型对不对"这种本该由投影层负责的事会伪装成一次转换异常。</summary>
    static DataTable TripleTable()
    {
        var orm = Harness.Orm(_ => null);

        var sql = orm.Select<BenchOrder>().Select(o => new Triple(o.Id, o.CustomerId, o.Amount)).ToSql();

        Selected(sql, 3);

        var table = new DataTable();

        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("CustomerId", typeof(int));
        table.Columns.Add("Amount", typeof(decimal));

        for (var row = 0; row < Rows; row++)
            table.Rows.Add(row, row % 97, row % 13 * 1.5m);

        return table;
    }

    /// <summary>数 SELECT 段到底发了几列。<b>列名不校验</b>：QuickSql 侧按序号取，名字进不了这条路径。</summary>
    static void Selected(string sql, int expected)
    {
        var at = sql.IndexOf(" FROM ", StringComparison.Ordinal);

        if (at < 0)
            throw new InvalidOperationException($"这条 SQL 里没有 ` FROM `，数不出投影段：{Environment.NewLine}{sql}");

        var columns = sql[..at].Split(',').Length;

        if (columns != expected)
            throw new InvalidOperationException($"假结果集按 {expected} 列投影写死，而这条 SQL 选了 {columns} 列：{Environment.NewLine}{sql}");
    }
}

/// <summary>三列投影的落地形状。<b>不用匿名类型</b>：BDN 的返回值校验与字段计数都要一个具名类型，
/// 而匿名类型跨方法签名会把它自己摊平成 <c>object</c>。</summary>
public sealed record Line(int Id, string? City, int Quantity);

/// <summary>基准名 <c>BulkInsert10k</c>（§12.8 第 6 行，v0.12 写路径落地后才有可跑的东西）。
/// <b>名字里的 10k 与门禁行的 1000 行不一致，这一条按门禁行走</b>：那两半（<c>1000 行 × 30 列 批量插入</c>
/// 与 <c>BulkInsert10k</c>）在 §12.8 里本来就各写一处，取行数一致的那一份，是为了让 BDN 报告的 Alloc Ratio
/// 与 <c>--gate</c> 的红绿比的是同一次插入 —— 名字保留原样，是为了对得上那张表里的那一格（§12.8 的
/// 基准名对照里记着这次取舍）。
/// <para>实体数组建在 <see cref="Setup"/> 里：写侧的分配几乎全在被测量之内（每格两颗对象 + 一份文本），
/// 把 1000 个 <see cref="WideRow"/> 的构造也计进来只会让每一格都多一个与判据无关的常数。
/// 与门禁同一档出口（<c>ExecuteAffrowsAsync</c>，不回填键），回填那一档多出的分配属于第 3 行的口径。</para></summary>
[MemoryDiagnoser]
public class BulkInsertBench : BenchBase
{
    const int BulkRows = 1_000;

    WideRow[] _rows = null!;

    QuickSqlOrm _write = null!;

    public override void Setup()
    {
        base.Setup();

        // 值各不相同：全默认会让每一格的装箱命中那点小整数缓存，报出来的每行字节就成了乐观下界。
        _rows = new WideRow[BulkRows];

        for (var row = 0; row < BulkRows; row++)
            _rows[row] = new WideRow { Id = row, Code1 = "c" + row, Qty1 = row, Money1 = row + 0.5m, Key1 = Guid.NewGuid(), State = BenchState.Placed };

        _write = Harness.Orm(_ => null, Generated);
    }

    /// <summary>每次都是<b>新引擎</b>（<c>InsertRange</c> 在方法体里）：引擎会缓存计划，
    /// 把 <c>var insert = _write.InsertRange(_rows)</c> 提到 <see cref="Setup"/> 里量出来的就是"第二次读缓存"，
    /// 与 §12.8 那句"稳态 0 分配"比的不是同一件事。</summary>
    [Benchmark(Description = "BulkInsert10k（1000 行 × 29 列写侧，L3 多值 + 16 块，G1）")]
    public Task<int> BulkInsert10k() => _write.InsertRange(_rows).ExecuteAffrowsAsync();
}

/// <summary>基准名 <c>ChangeTrackerDetect10k</c>（§12.8"基准名对照"里最后一格，v0.17 补上）。
/// <b>名字写 10k 而形状是 1000 只，与 <see cref="BulkInsertBench"/> 同一个取舍</b>：那两半在 §12.8 里各写了一个数，
/// 取行数一致的那一份是为了让 BDN 报告与门禁比的是同一次会话规模（名字保留原样，理由同那一格）。
/// <para><b>被测窗口里没有 ADO</b>：把 1000 行读进会话发生在 <see cref="Setup"/>，而 <c>DetectChanges</c>
/// 读的是实体属性与内存快照 —— 所以 §12.8"数字从哪来"那五条假驱动口径失真一条都沾不到这一格。
/// 反过来说它也<em>不在 §12.8 那张门禁表里</em>：那几行是 §12.8 原稿列的 CI 阻断项，变更跟踪不在其中，
/// 给它加一行是改规格而不是补实现。于是这一格的数只进 BDN 报告、<b>不进退出码</b> ——
/// "一次把 <c>DetectChanges</c> 做贵了的 PR 不会被 CI 拦住"这条缺口记在 §14 的 P1 余项。</para>
/// <para>v0.28 的第 11 行把这一条<em>收窄了一点而没有关掉</em>：那一格的地板腿（同一批实体一次都不弄脏）
/// 量的就是这一句里的"全员比一轮"，于是侦测变贵会在那一格的实测里显形 —— 但它只是<em>印出来</em>而没有钉，
/// 而且规模是 13 只而不是这里的 1 000 只。<b>两份读数不能直接换算</b>：地板腿 6 272 B ÷ 13 = 482 B/只，
/// 这里 507.84 KB ÷ 1 000 = 520 B/只，差的那 38 B/只最像的来源是<b>实体形状不同</b> —— 地板腿那 13 只由
/// <c>new WideRow { Id = i + 1 }</c> 造，两颗可空 <see cref="DateTime"/> 是 null 因而压根不装箱，
/// 而这里的数据由 <see cref="Harness.Table"/> 逐列灌值、可空列也有值。<b>这一句是给"为什么两份数不一样"
/// 一个方向，不是精确归因</b>（没逐列拆过；要拆就在 <see cref="Setup"/> 里把可空列留空重跑一场）。
/// 所以"没人拦"那句照旧成立，读法照旧是这一格进 BDN、不进退出码。</para>
/// <para><b>v0.39 那一格进表了，上面那两句从今天起读作历史</b>：§12.8 添了第 13 行
/// （<c>Gates.SessionScanFloor</c>，三枚钉 = 全员未变的一轮侦测、每只都脏的一轮侦测、1 000 只里一句写回的保存全程），
/// 于是"把 <c>DetectChanges</c> 做贵了的 PR 不会被 CI 拦住"那一句作废 —— 现在它会红，而红的是钉行那一位
/// （<c>RegressionFlag</c>），不是颜色。本方法继续存在的理由随之换掉一半：字节那一半由门禁接管，
/// 这里留着的只有 <b>µs</b> 那一列（时间类不钉，见 §12.8 那句"分配是确定量、时间是机器状态"）
/// 与"给 PR 看的前后对比"这一用途。两处的规模口径也不同源：这里 1 000 只由 <c>Harness.Table</c> 逐列灌值
/// （520 B/只），门禁那 1 000 只由 <c>Attach</c> 造（472 B/只，可空列全 null 不装箱），别互相换算。</para>
/// <para><b>两条方法比的是"有没有短路"</b>：<see cref="DetectClean"/> 是全员未变的一轮，
/// <see cref="DetectEveryEntityDirty"/> 是每只实体都有一列被改过的一轮。§8.2 那句"hash 短路，
/// 未变的实体不进列比较"没接线，所以两轮的字节数<em>相同</em>就是"没短路"的见证，
/// 而 <see cref="DetectClean"/> 那个数同时是"把短路接上最多能省多少"的上界。
/// 2026-09-22 实测：两轮各 <b>507.84 KB</b>（520 B/只，全部是每只 20 颗值类型列的装箱，逐列算得与实测对上）、
/// <c>Alloc Ratio 1.00</c>，243.3 / 252.4 µs（第二场 249.5 / 257.3 µs，分配数逐字相同）。
/// 改值写在被测量之内（一列 <c>int</c> 赋值不分配）：<c>DetectChanges</c> 不动快照，
/// 若把"脏"做成 setup 里一次性的动作，第一次迭代之后每一轮量的就都是同一个已判过脏的状态，
/// 与真实用法里"每次保存前有人改过"不是同一件事。</para></summary>
[MemoryDiagnoser]
public class ChangeTrackerBench : BenchBase
{
    /// <summary>与 <see cref="BulkInsertBench" /> 同档（1000），理由见类注释。</summary>
    const int TrackedRows = 1_000;

    DbContext _session = null!;

    WideRow[] _tracked = null!;

    public override void Setup()
    {
        base.Setup();

        var table = Harness.Table(Generated, typeof(WideRow), TrackedRows);

        _session = new DbContext(Harness.Orm(_ => table, Generated));
        _tracked = [.. _session.Set<WideRow>().ToList()];

        // 会话没收进 1000 只的话，两轮 DetectChanges 量的都是空转，而空转报出来的 0 B 读起来正像达标。
        if (_session.ChangeTracker.Count != TrackedRows)
            throw new InvalidOperationException(
                $"只跟踪到 {_session.ChangeTracker.Count} 只，预期 {TrackedRows} 只：这一格量的东西不存在，别把它的数字拿去算账。");
    }

    [Benchmark(Baseline = true, Description = "ChangeTrackerDetect10k（1000 只已跟踪实体的一轮 DetectChanges，全员未变）")]
    public int DetectClean() => _session.ChangeTracker.DetectChanges();

    [Benchmark(Description = "同一档，每只实体都有一列被改过（对照：没有 hash 短路 ⇒ 两轮同价）")]
    public int DetectEveryEntityDirty()
    {
        // 取负数：快照里的 Qty1 是行号（≥ 0），所以这一句在第一次之后每轮都仍然"脏"。
        for (var i = 0; i < _tracked.Length; i++) _tracked[i].Qty1 = -i - 1;

        return _session.ChangeTracker.DetectChanges();
    }
}
/// <summary><see cref="MaterializeBench.SelectSimpleDto"/> 的形状：三个槽位、两种类型（含一个会走
/// <c>ValueConverter</c> 的 <c>decimal</c>）。与 <see cref="Line"/> 分开写而不是复用，是因为这两条基准
/// 的表不同、槽位类型分布也不同，合成一个类型就让"只差投影这一步"这句对照失效了。</summary>
public sealed record Triple(int Id, int CustomerId, decimal Amount);
