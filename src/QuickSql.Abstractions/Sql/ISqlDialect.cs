using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Sql;

/// <summary>方言层的出口。Emit 不返回 string：返回 string 的形状会让每一层先分配再拼接。</summary>
public sealed record DialectSql(string Text, IReadOnlyList<AdoParameter> Parameters)
{
    public int ParameterCount => Parameters.Count;
}

/// <summary>语法分岔点：标识符、分页、类型名、UPSERT、RETURNING。
/// 一个方言实例必须能被多个线程同时使用，因此这里不允许有可变状态。</summary>
public interface ISqlDialect
{
    /// <summary>参数前缀（@、:、?）。SqlWriter 靠它生成参数名。</summary>
    char ParameterPrefix { get; }

    /// <summary>这一家的驱动收不收带方言前缀的参数名。
    /// AdoExecutor.Bind 默认把前缀摘掉再交给驱动——那是虚谷（交带前缀的参数名得"未绑定变量"错）逼出来的摘法，
    /// 代价是每绑一颗参数多一次 Substring 分配；答 true 表示本家驱动两种写法都收、且带前缀那一路有过活库见证，
    /// 于是执行器原样递过去、不吃那份价格。今天只有 SQL Server 为 true（它声明的是"这一家不必替第五家付账"）；
    /// 其余四家保持 false 各有所因：虚谷必须摘，另三家"两种都认"没有各自的活库复点，按"没有实测就不改默认"留在摘的那边。
    /// 它不是 IValueBinder 的许可证：provider 若要给驱动另一颗名字，走 AdoParameter.DriverName 那颗自带字段，
    /// 这里只管"前缀摘不摘"这一格。</summary>
    bool DriverAcceptsPrefixedParameterNames { get; }

    /// <summary>是否把字符串当 Unicode 传参。SQL Server 必须为 true，否则 cp1252 排序规则下非 ASCII 会静默变成问号。</summary>
    bool PrefixUnicodeLiterals { get; }

    /// <summary>恒假谓词。空集合 IN 特化到这里，各家写法不同（T-SQL 1 = 0、PG false）。</summary>
    string EmptySetPredicate { get; }

    /// <summary>恒真谓词，IN 全集与空集合 Any() 用。这两处用途今天没有接线：Core 里的消费点是 0 处，
    /// 与上面那颗恒假不同（恒假有 1 处真实消费者：空集合 IN 特化）。所以这颗是"给将来留的口"。</summary>
    string AlwaysTruePredicate { get; }

    /// <summary>这个库的分页语法本身是否要求 ORDER BY（T-SQL 的 OFFSET..FETCH、DM 的 ROWNUM 要求；
    /// PG 的 LIMIT 不要求）。无序列出在上游就被策略拦掉了，走到这里说明策略是 Allow：
    /// 要求 ORDER BY 的方言必须补一个占位序，否则生成的 SQL 直接语法报错。</summary>
    bool PagingRequiresOrdering { get; }

    /// <summary>这一家能不能把别的表带进一条写语句（UPDATE FROM 行集 / DELETE USING）。
    /// 它是合并档的唯一闸门：跟踪器在建 AST 之前问这一颗，答 false 就整桶退回一句一实体（语义等价、代价是 N 次往返）。
    /// 它不长在 DbCapabilities 位表上，是因为那份位掩码今天零读者，判据是"代码走到没有"而不是"库有没有"；
    /// Oracle 血统取 false——那一家没有 UPDATE FROM，等价物是 MERGE INTO。
    /// 不许有人为了让合并档跑起来把它改成 true：那只会让发射层抛，而抛点离写它的人隔了一次构建。</summary>
    bool SupportsJoinedWrites { get; }

    /// <summary>这一家的驱动事务对象收不收 ADO.NET 的保存点三件套（带名字的 Save/Rollback/Release）。
    /// 基类对这三颗的默认实现是抛 NotSupportedException，"驱动没覆写"与"驱动明确不支持"量不出分别是哪一件，
    /// 所以答 false 的方言必须同时给出下面三口文本发射口，执行层据此在外层事务那条连接上发文本保存点；
    /// 语义一格都不动：内层提交并入外层、内层回滚只撤销内层、外层照常提交或回滚。
    /// 今天取 false 的只有虚谷（驱动只覆写了无参 Rollback，带名字的三颗停在基类声明上、调用即 NotSupportedException；
    /// 同一台库把文本那三句收下且语义正确）；取 true 的四家各有活库见证。
    /// 不许有人为了让某一场变绿把它改成 false：下面三口默认抛，那样只会把红点从驱动层挪到发射层，
    /// 离写它的人更远一格。</summary>
    bool SupportsDriverSavepoints { get; }

    /// <summary>发射"创建保存点"那一句文本。只在 SupportsDriverSavepoints 为 false 时被调用，
    /// 且必须与下面两口收同一个 savepointName：名字由 AdoExecutor 按外层事务上的计数器生成
    /// （形状 quicksql_sp_n，字母数字下划线、全部小写，不是用户输入），三口一旦在字形上分岔，
    /// 得到的是"回退到一个不存在也不报错的名字"——那比抛更贵。</summary>
    void EmitSavepoint(SqlWriter writer, string savepointName);

    /// <summary>发射"回退到保存点"那一句（内层只撤销自己）。</summary>
    void EmitRollbackToSavepoint(SqlWriter writer, string savepointName);

    /// <summary>发射"释放保存点"那一句（内层提交：改动并入外层，外层事务继续）。</summary>
    void EmitReleaseSavepoint(SqlWriter writer, string savepointName);

    void Emit(in SelectStatement select, SqlWriter writer);

    void Emit(in InsertStatement insert, SqlWriter writer);

    /// <summary>发一整句 upsert——只给"没有 UpsertClause 句尾槽"的一家
    /// （内核先问那份句尾，交得出就走 Emit(InsertStatement) 再接尾巴，走不到这一颗）。到这里的一家
    /// 发的是两种写法之一：SQLite 的 ON CONFLICT DO UPDATE，或 MERGE INTO 那一族
    /// （SqlServer、Oracle 血统、虚谷、Gbase8s）。AST 形态中立，写法由这一家自己定。
    /// 默认实现抛——没覆写又没有句尾的家，InsertOrUpdate 在内核那一层就当场拒。</summary>
    void Emit(in UpsertStatement upsert, SqlWriter writer);

    void Emit(in UpdateStatement update, SqlWriter writer);

    void Emit(in DeleteStatement delete, SqlWriter writer);

    void EmitIdentifier(SqlWriter writer, string identifier);

    void EmitTableName(SqlWriter writer, TableInfo table);

    /// <summary>写语句（UPDATE/DELETE）的目标表在 SET 与 WHERE 里用什么名字引用自己。
    /// T-SQL 与 PG 系给目标起一个独立别名（t0），而 Oracle 血统两样都不接受——
    /// 目标不能起别名，引用只能用表名本身。这个名字长在 AST 的 TableRef 里、
    /// SET 与 WHERE 的列引用都从它取前缀，而方言实例是跨线程共享的，所以"现在是写语句"
    /// 这件事必须在建 AST 之前就定下来，不能等到发射现场猜。</summary>
    string WriteTargetAlias(TableInfo table);

    /// <summary>CAST / DDL 用的存储类型名。DDL 归 ICodeFirstDialect，
    /// 但类型名只有一份真相，所以 CodeFirst 从这里取。</summary>
    string GetStoreType(ColumnContract column);
}
