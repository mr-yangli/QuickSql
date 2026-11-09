using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.DuckDb;

/// <summary>DuckDB 的驱动槽位：PG 血统的第七颗叶子，也是血统里第一颗真正改了血统产出的叶子——
/// 此前六颗在文本层全部零覆写、产出逐字同形，而 DuckDB 与血统的分岔是厂商文档明写的，本家不是一层空壳。
/// 覆写三格：类型表换 DuckDbTypes（DOUBLE/TIMESTAMP/TIMESTAMPTZ/TIME 四格换名）；DDL 换 DuckDbCodeFirst
///（身份列换形态：句外 CREATE SEQUENCE + DEFAULT nextval）；"建库"整格不存在——内嵌引擎没有服务端，
/// "库"就是连接串里那颗文件，与 SQLite 同档（不是"没读数所以不赌"，是这一问在本家没有对象）。
/// 没覆写的格子是贴着文档对完之后裁"同形"（分页两子句、布尔谓词两形、句尾 RETURNING、ON CONFLICT 档位、
/// COMMENT ON COLUMN/TABLE、ALTER COLUMN … TYPE 双句），代码实际来自血统，库侧收不收全部归未实测。
/// 驱动名 DuckDB.NET.Data 是厂商文档口径、从未解析成功过，不写 factoryType 走兜底扫描；
/// 整层是文档口径 + 未实测台账。</summary>
[QuickSqlProvider(DbKind.DuckDb)]
public sealed class DuckDbProvider : PostgreSqlProviderBase
{
    /// <summary>内建表里的 key（值照 DbKind.DuckDb.ToString().ToLowerInvariant() 落）。</summary>
    public const string DriverKey = "duckdb";

    /// <summary>血统那道缝的第一格：类型表换本家自己的 DuckDbTypes。
    /// 为什么类型表要换而方言不够，缺口在 PostgreSqlProviderBase 的 Types 抬头里写全了（CAST 那条路读的是属性）。</summary>
    public override ITypeMap Types => DuckDbTypes.Instance;

    /// <summary>血统那道缝的又一行，方向与前两格相反：把 PG 那份完整性码表退回"判不出"。
    /// DuckDB 说的是 PG 的线协议，可约束违例在它自己那一侧报的不是 PG 的 SQLSTATE 家数
    ///（23505/23502/23503/23514 那一套是服务端的行为，不是协议规定的），而本家没有一条真库用例
    /// 撞过其中任何一颗。判据是"误分类比不分类坏"——拿别人的码表猜自己家，猜中的结果是用户把
    /// 一句约束错误读成"重复提交"再点一次。翻这一格的条件：集成册那条完整性翻译路径测试在本家跑绿。</summary>
    public override IntegrityViolation? ClassifyIntegrityError(DbException error) => null;

    public DuckDbProvider(DbProviderFactory driverFactory)
        : base(DbKind.DuckDb, driverFactory)
    {
    }

    /// <summary>血统那道缝的第二格：DDL 换本家的 DuckDbCodeFirst（身份列换形态那一份）。
    /// 参数 types 是血统传上来的那份 PostgreSqlTypes，本家不接它——DDL 与类型表是同命的，换 DDL 就换同一份类型表。</summary>
    protected override ICodeFirstDialect CreateCodeFirst(PostgreSqlTypes types) => new DuckDbCodeFirst(DuckDbTypes.Instance);

    /// <summary>血统那道缝的第三格：方言是 DuckDbDialect——文本层与血统的差额只有 GetStoreType
    /// 那一颗读的表；血统的方言类本来 public 可继承（不是 sealed），本家的子类只覆写真正分岔的那一档。</summary>
    protected override PostgreSqlDialect CreateDialect(
        DbKind kind, PostgreSqlTypes types, PostgreSqlKeys keys, PostgreSqlValues values, PostgreSqlExpressions expressions)
        => new DuckDbDialect(kind, keys, values, expressions);

    /// <summary>"建库"这一问在本家没有对象：内嵌引擎没有服务端，"库"就是连接串里那颗文件，打开即有
    ///（与 SQLite 同档）。表的门在 SchemaSyncer 那一句话里（按这颗位绕开 EnsureDatabaseExistsAsync），
    /// 所以这一格落 false 是可执行的那一半。</summary>
    public override bool CanCreateDatabase => false;

    /// <summary>血统那一份的整格直达不可达：进门第一道闸（CanCreateDatabase）已经是 false，
    /// 而"谁绕过闸来问"的答案是抛——调用方拿一句 NotSupportedException，消息里写明"本家的库是那颗文件"，
    /// 而不是静默走到血统那句连 postgres 锚点库的查询。</summary>
    public override Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
        => throw new NotSupportedException(
            "DuckDB 是内嵌单文件引擎：\"库\"就是连接串里那颗文件，没有\"建库\"这一操作（血统 PG 的 EnsureDatabaseExistsAsync 在本家不可达）。");
}

/// <summary>本家的文本层：血统 PostgreSqlDialect 的子类，只覆写 GetStoreType 一格——换读 DuckDbTypes 那份表。
/// 开成覆写而不是"方言另起一份"的理由：血统方言的其余各格（分页两子句、布尔谓词两形、恒假字面量、
/// UPDATE … FROM 的折并、句尾 RETURNING）在 DuckDB 的文档口径里同形，另起一份就会把那一批"同形"
/// 变成一堆再也不随血统走的复件；覆写则它们跟着血统同一份改动走。
/// 基类构造参数里那颗 PostgreSqlTypes 本类不收（传 null 是刻意的）：血统方言只有 GetStoreType 读它，
/// 而这一格已由本类覆写补位——null 传进去的每一条路径都在覆写层的后面。</summary>
sealed class DuckDbDialect(DbKind kind, PostgreSqlKeys keys, PostgreSqlValues values, PostgreSqlExpressions expressions)
    : PostgreSqlDialect(kind, null!, keys, values, expressions)
{
    public override string GetStoreType(ColumnContract column) => DuckDbTypes.Instance.GetStoreType(column);
}
