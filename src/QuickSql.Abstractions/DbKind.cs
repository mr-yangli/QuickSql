namespace QuickSql;

/// <summary>数据库种类。分派表由 G2 按这个枚举生成，因此成员顺序与名字都是契约。</summary>
public enum DbKind
{
    SqlServer = 0,
    MySql = 1,
    Oracle = 2,
    PostgreSql = 3,
    Kingbase = 4,
    OpenGauss = 5,
    GaussDb = 6,
    Highgo = 7,
    Dameng = 8,
    ShenTong = 9,
    Xugu = 10,
    Gbase8s = 11,
    Gbase8c = 12,
    TiDb = 13,
    OceanBase = 14,
    Sqlite = 15,
    // 追加成员只在末位。分派走字符串 DriverKey、provider 与测试按名字取常量，仓库里没有一处读这颗整数。
    DuckDb = 16,

    // 文档口径实现、未经活库实测：UPDATE/DELETE 整格拒、建表带索引整格拒。
    ClickHouse = 17,

    // 文档口径实现、未经活库实测：无事务、无 UPDATE、无 identity、无二级索引；
    // 每表第一列必须是 TIMESTAMP（时间戳主键校验面在 provider 的 CodeFirst 里）。
    TDengine = 18,
}
