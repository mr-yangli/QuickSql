using System.Data;
using QuickSql.Benchmarks.Offline;
using QuickSql.Generated;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Providers.SqlServer;
using QuickSql.Schema;

namespace QuickSql.Benchmarks;

/// <summary>基准与门禁共用的组装根。这里唯一"生产环境没有"的东西是 <see cref="OfflineFactory"/> 那个假驱动。</summary>
static class Harness
{
    /// <summary>占位串：与方言套件同一条纪律（§13.1 第 4 项），<b>故意不是真配置</b>。
    /// 假驱动不读它，所以它一旦被换成活库就说明有人把基准搬错了地方。</summary>
    public const string OfflineConnectionString = "Server=offline;Database=never-benchmarked;Integrated Security=True";

    public static IDbProvider Provider(Func<string, DataTable?> result) => new SqlServerProvider(new OfflineFactory(result));

    /// <summary><c>schemas = null</c> 走 G2（反射兜底，构造器自带 <c>[RequiresUnreferencedCode]</c>）；
    /// 传 <see cref="Generated"/> 那份走 G1。两侧都要量，因为 §12.8 的门禁钉的是 G1 路径。</summary>
    public static QuickSqlOrm Orm(Func<string, DataTable?> result, IEntitySchemaRegistry? schemas = null)
    {
        var options = new QuickSqlOrmOptions
        {
            Provider = Provider(result),
            ConnectionString = OfflineConnectionString,
        };

        return schemas is null ? new QuickSqlOrm(options) : new QuickSqlOrm(options, schemas);
    }

    /// <summary>与 <see cref="Orm"/> 同一条组装，只多一件事：把 <see cref="OfflineFactory.Counters"/> 那颗槽位
    /// 换成一颗新的计数器。§12.8 第 11 行要从<b>数据源那侧</b>数语句条数与事务条数，而不是读 QuickSql 自报的
    /// 那两个数（理由见 <see cref="WriteCounters"/>）；而计数器只能挂槽位、不能挂构造参数 —— 连接与命令每查询都
    /// 新建，给它们加字段就是给第 1 行与第 6 行各添一笔量具自己的钱，四笔回归的实测与算法记在
    /// <c>OfflineAdo</c> 类注释④。组装仍在测量窗外，所以这一句不改任何一格的字节。
    /// <para><b>槽位是全局可变的，用完要摘</b>：调用方在 <c>finally</c> 里调 <see cref="UnbindCounters"/>，
    /// 于是"其余各行跑的时候槽位是 null"这件事由代码结构保证，不靠"记得摘"。</para></summary>
    public static (QuickSqlOrm Orm, WriteCounters Counters) OrmCounted(Func<string, DataTable?> result, IEntitySchemaRegistry? schemas = null)
    {
        var counters = new WriteCounters();
        OfflineFactory.Counters = counters;

        return (Orm(result, schemas), counters);
    }

    public static void UnbindCounters() => OfflineFactory.Counters = null;

    public static IEntitySchemaRegistry Generated(IDbProvider provider)
        => new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet);

    public static IEntitySchemaRegistry Reflected(IDbProvider provider)
        => new EntitySchemaRegistry(new ContractResolver(provider));

    /// <summary>结果集<b>由契约生成</b>，不手写列清单：列名、列序、列型都取自
    /// <see cref="EntityContract.Columns"/>，所以契约一旦变了，假结果集跟着变，
    /// 不会出现"SQL 改了但样本数据还合着旧契约"这种看起来正常的红数字。
    /// 枚举列按<b>存储类型</b>（底层整数）灌：真驱动交回来的是整数而不是枚举实例，
    /// 直接放枚举会让 <c>ValueConverter</c> 走 <c>value is T</c> 那条快捷分支，把真实的装箱路径绕过去。</summary>
    public static DataTable Table(IEntitySchemaRegistry schemas, Type clrType, int rows)
    {
        var contract = schemas.Contract(clrType);
        var table = new DataTable();

        foreach (var column in contract.Columns)
            table.Columns.Add(column.ColumnName, StoreType(column.ClrType));

        for (var row = 0; row < rows; row++)
        {
            var values = new object?[contract.Columns.Length];

            for (var i = 0; i < values.Length; i++)
                values[i] = Sample(contract.Columns[i].ClrType, row, contract.Columns[i].ColumnName);

            table.Rows.Add(values);
        }

        return table;
    }

    static Type StoreType(Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return underlying.IsEnum ? Enum.GetUnderlyingType(underlying) : underlying;
    }

    static object Sample(Type clrType, int row, string column)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlying.IsEnum)
            return (int) (row % 4);

        if (underlying == typeof(string))
            return column + row;

        if (underlying == typeof(int))
            return row;

        if (underlying == typeof(long))
            return (long) row;

        if (underlying == typeof(decimal))
            return row + 0.5m;

        if (underlying == typeof(DateTime))
            return new DateTime(2026, 1, 1).AddMinutes(row);

        if (underlying == typeof(bool))
            return row % 2 == 0;

        if (underlying == typeof(Guid))
            return new Guid(row, 0, 0, new byte[8]);

        throw new NotSupportedException($"基准样本没覆盖 {underlying.Name}：加一条分支，别让它落成字符串列。");
    }
}
