# QuickSql 注意事项归档

> 2026-11-02 全仓注释精简（简短 `///`）时，从代码注释里摘出来的坑位、边界、已知限制与历史账，按模块归档于此。代码里只留一两句能自立的说明；这一份收"展开才说得清"的部分。

> **2026-11-09 rowversion 支持移除**：产品与测试一起删掉了 `ConcurrencyMode.RowVersion`、`ColumnContract.StoreFillsValue`、`OUTPUT/RETURNING` 回读并发列那条路，以及 SQL Server 的 rowversion 映射。并发令牌只剩两族：整型 `Version`（ORM 在 `SET` 里 +1）与 `AppSetsValue`（应用给新值）。下面历史条目中涉及 `rowversion` / `StoreFillsValue` / “值归库给”的，一律以本条为准，代码里已不再走那条路。

## Metadata／ColumnContract（src/QuickSql.Abstractions/Metadata/ColumnContract.cs）

- `NullabilityDeclared` 只摘写前必填校验（§10.6 的尺子），不改列形状——动形状要的是现库的列而不是一句推断；唯一落 false 的情形是 `#nullable disable` 或未带可空元数据的引用程序集（整份 ABP/DDD 框架代码天天在这一格上）。
- `IsConcurrencyToken` 与 `ValueFromStore` 不是一回事：前者说"这一侧不写它"，后者说"库里它必不为空"。可空并发列而库里没有任何东西给它值的那一型（CheckedAt）不进列清单，可 DDL 写 NOT NULL 会让每一句不带它的 INSERT 撞库。
- `AppSetsValue` 与整型自增分住：整型令牌不进 SET（由 ORM 发 `Version = Version + 1`）；应用给值族必须进——WHERE 是快照、SET 是现值。rowversion / StoreFillsValue 已移除。
- `NullableInStore` 是五族 EmitColumnDefinition 的唯一可空判据——"改一处注解就让两条 DDL 对上"的修法在这一格无处遁形。
- `Comment` 的 null 与空串语义不同：结构同步按这一格决定要不要重发 COMMENT ON COLUMN。
- `MaxLength` 解析链上 StringLength 与 MaxLength 同层互斥，取严格者会藏"建表按 20、校验按 50"三个月的 bug，所以直接抛 MetadataConflictException。
- `ColumnFacts.MaxChars` 的三态（`DefaultStringLength` 落地后）：null = 未表态（吃 orm 默认）、> 0 = 声明宽度、<= 0 = 显式"无宽度"（不吃默认）。两条注册路径各自只负责"写了的原样进事实、没写的给 null"，收口在 `ColumnFacts.ToProvisional`；点名了存储型（`StoreTypeName`）的列额外跳过默认——否则契约宽度会小于列的实宽，`FitsChars` 会拒掉列明明装得下的值。DA 的 `[StringLength(-1)]` 也走这一态，代价是它自己的 `IsValid` 会抛（"maximum length must be a nonnegative integer"），实体要过 DA 校验管线时改用 `[QuickColumn(Length = -1)]`。

---

# Part A：注释精简时摘出的坑位、边界、实测读数与重开条件

> 2026-11-02 QuickSql.Abstractions 全量注释精简（简短 ///）时，从代码注释里删掉的"展开才说得清"的部分，按目录归档于此。锚为 `文件相对路径 + 成员名`。代码里只留一两句能自立的说明。

### 根目录（DbKind / DbCapabilities / Exceptions / AssemblyInfo）

- `src/QuickSql.Abstractions/DbKind.cs + DbKind`：MariaDb 曾挂在 MySql 与 Oracle 之间占 = 2，撤下后留下的空洞于 v0.37 压平（16 颗成员值域 0..15 连续）；压编号只改后备整数、不动成员顺序与名字。整数值本身无人读取——分派走字符串 DriverKey、provider 与测试按名字取常量；"留空洞不重编号"的旧判据据此改判。
- `src/QuickSql.Abstractions/DbKind.cs + DbKind.DuckDb`：整层按【文档·DuckDB】口径实现、未经活库实测。它是 PG 血统的第七颗叶子（挂 PostgreSqlBase），但文本层真的改了血统产出：类型表四格换名（DOUBLE/TIMESTAMP/TIMESTAMPTZ/TIME）、身份列换形态（句外 CREATE SEQUENCE 加 DEFAULT nextval）、"建库"整格不存在（内嵌单文件引擎，与 SQLite 同档）。逐格出处见 src/QuickSql.Providers.DuckDb 各类注释。
- `src/QuickSql.Abstractions/DbKind.cs + DbKind.ClickHouse`：单层 provider（无血统基类、无叶子），能力 6 位、UPDATE/DELETE 整格拒、建表带索引整格拒；整层按 clickhouse.com/docs 口径、未实测。
- `src/QuickSql.Abstractions/DbCapabilities.cs + DbCapabilities.Transactions`：这是位表上第一颗被内核真正当闸门读的位——此前三十一位的读者只有 SchemaSyncer 的 Alter_Column_Precision。底层类型必须为 uint：位表占满 1<<0 到 1<<30 后第三十二颗只能住进有符号 int 的符号位，组合值会变负数，而 Flags 组合名的解析走符号位扩张，带它混排的任何位掩码 ToString() 都会静默错拼；换 uint 后声明形状一字不变，仓库对 (int) 一类数值取用在 src/ 与 tests/ 各 0 命中（2026-10 复核）。
- `src/QuickSql.Abstractions/DbCapabilities.cs + DbCapabilities.Json_Type`：位与类型页原稿（Json<T>）都还留着，但映射层产不出 json 列、零消费者，PG 与 SQL Server 两颗声明已摘——"声明了却没人读"与"没有"按能力位的诚实条款要分清，现在口径是后者；真实现（对象与字符串整体读写）单独一批落地时把这一位与声明一起重开。
- `src/QuickSql.Abstractions/Exceptions.cs + ValidationFailedException.BuildMessage`：实测一次 134 行的种子数据报出 656 项违例——归并清单按"成员 + 规则"分组、项数多的排前面，就是为了这种形状下读的人能一眼看到"契约哪几条太严"。
- `src/QuickSql.Abstractions/Exceptions.cs + DbUpdateException.columns`：列名不在任何驱动的结构化字段里——PG 的 DETAIL 段在默认连接串上被驱动整段涂掉，连"能不能拿到"都不是方言说了算的事；正解是拿 ConstraintName 回契约查唯一索引的列集合，对不上（库自起约束名、系统生成的 PK 名、契约外对象）就是 null，查不到既不抛也不猜。
- `src/QuickSql.Abstractions/Exceptions.cs + ForeignKeyConstraintException`：PG 实测插入侧与删除侧同码（都是 23503），只有文案分岔。
- `src/QuickSql.Abstractions/Exceptions.cs + IntegrityViolation.ConstraintName`：PG 实测只有非空那一档不给约束名（那一档本来就没有命名约束），其余三档都给。
- `src/QuickSql.Abstractions/AssemblyInfo.cs + InternalsVisibleTo`：写路径（QuickSql.Core）是钩子事件的第一个消费者；把构造器开成 public 会让"事件"变成用户可以自己 new 出来喂给钩子的东西。

### Metadata

- `src/QuickSql.Abstractions/Metadata/EntityContractFactory.cs + ColumnFacts.ToProvisional`：.NET 10.0.12 实测——new StringLengthAttribute(-1) 不抛且 MaximumLength == -1，反射取该属性上的实例也原样交回；BCL 只在有人真调用 IsValid 时才响 InvalidOperationException（"The maximum length must be a nonnegative integer"）。即建契约这一路对负宽度一路绿灯，直到发射 DDL 或首次校验才现形；两条路（源码生成与反射）在此同形，收口只能收在这一个点。
- `src/QuickSql.Abstractions/Metadata/EntityContract.cs + TableInfo.Comment`：挂源生成器的路径上还多一颗兜底——项目开着 QuickSqlXmlDocComments 时，注解与 Fluent 两层都没写的那一格取类型上的 /// summary（反射侧没有这份来源）。
- `src/QuickSql.Abstractions/Metadata/EntityContract.cs + RelationshipContract`：FK 约束文本仍未接线（P1 余项）。
- `src/QuickSql.Abstractions/Metadata/EntityContract.cs + EntityContractFactory.ForShardDdl`：随片索引的建档口径被裁（规格 §16.4 记为已裁），SplitTablesResult.IndexesNotCreated 每次点名模板上有几条索引没随片建；将来要接，排在 P2。
- `src/QuickSql.Abstractions/Metadata/QuickSqlAttributes.cs + ConcurrencyTokenAttribute`：由调用方自己递增的版本号（int Version 一派）不属于这一格——那一列要进 SET，而"谁负责 +1"还没定，缺口与判据记在 P1 余项（AppSetsValue 第三族不受此挡：那一档"谁给新值"已定）。
- `src/QuickSql.Abstractions/Metadata/QuickSqlAttributes.cs + SplitTableAttribute`：按运行期上下文改表名（影子表、租户表那一类）走的是 TableRemap 钩子那格，今天仍未落地。
- `src/QuickSql.Abstractions/Metadata/IdentifierPolicy.cs + IdentifierPolicy`：v0.23 起 FoldToUpperCase 只有 Oracle 血统取 true；v0.31 起虚谷成为第二家（来路不同：Oracle 靠厂商文档，虚谷靠已发布产物里 INDEX_NAME=UPPER('{0}') 那句）；v0.37 Gbase8s 添了"库种需要折小写而字段没有那一支"的第一户——六颗预设的取值分布 2 真 : 4 假，而 Gbase8s 一票与 PG 系两家同值不同解。FoldToUpperCase 的真状态要三值枚举才表达得下。
- `src/QuickSql.Abstractions/Metadata/IdentifierPolicy.cs + IdentifierPolicy.MySql`：MySQL 保留字表"最长"这一判据在五家里没有重数过——第五家的完整保留字清单两档出处都没读到【无出处】，这句"最长"没有替它签字（同句式在 MySqlExpressions 的舍入模式一格也用过一次），两处一起留给 doctor。
- `src/QuickSql.Abstractions/Metadata/IdentifierPolicy.cs + IdentifierPolicy.Xugu`：折大写有两档出处且都是条件句——产物侧读目录用 INDEX_NAME=UPPER('...')（不加引号建的名字在目录里已是大写）；文档侧把折叠交给 COMPATIBLE_MODE：取 NONE 或 ORACLE 折大写、MYSQL 不处理、POSTGRESQL 折小写。真状态是"默认档折大写，而它是建库时可改的参数"，代价（换 POSTGRESQL 档时整套折叠判断反号）属"会话/兼容参数改变方言假设"的第四个实例（前三个：金仓兼容模式、MySQL lower_case_table_names、虚谷 CHAR_SET）。双引号对反引号是取向不是唯一合法：文档把反引号也列为合法引号，产物里反引号只出现在建表整段外壳上。
- `src/QuickSql.Abstractions/Metadata/IdentifierPolicy.cs + IdentifierPolicy.Sqlite`：唯一一颗每颗取值都经活库实测（探针 probe10 的 10/11/12/18/19/20/21）：方括号与双引号命中同一列；带引号 "MIXEDcase" 能命中不带引号建的 mixedcase，再建一次同名表报 "mixedcase already exists"；不带引号的未知标识符被解析器静默改判——投影位置落成一列常量字符串、WHERE 里恒假、算术里恒零、ORDER BY 里既不报错也不改序，四格都是"能绿着错"；建表写的字形就是目录读回的字形（22/81/616 那串读数）。
- `src/QuickSql.Abstractions/Metadata/SplitTable.cs + SplitRouting.Resolve`：另一家 ORM（SqlSugar 系）在分片键为空时"当作今天"（SplitTableService.cs:65,75-76 的读码结论）；本家宁抛不让一行数据因空字段静默进错表——那笔账要到月底统计才看得见。时间档若不先 Floor，2026-01-15（周四）的行会进 …20260115，而按周读算出来的是 …20260112 那张。

### Tracking

- `src/QuickSql.Abstractions/Tracking/EntityState.cs + EntityState`：与规格 §8.2 的列举顺序差一格且有意——Detached 挪到 0 位，为的是 default(EntityState)（结构体默认、忘设置的局部、反序列化缺字段）落在"不进任何语句"而不是"插一行"；名字与个数与规格一致，按名字 switch 不受影响。
- `src/QuickSql.Abstractions/Tracking/DbChangeTracker.cs + DbChangeTracker.DetectChanges`：实测每只 520 B / 约 0.24 µs，全部落在装箱上。v0.39 起 Gates.SessionScanFloor 门禁钉着这轮比较的字节数：全员未变与每只都脏各一枚，再加一枚"1000 只里只有一只欠语句"的保存全程——把 DetectChanges 做贵的 PR 从此红在退出码上。
- `src/QuickSql.Abstractions/Tracking/SaveChangesReport.cs + SaveChangesOp`：报告侧按脏列掩码分桶是 v0.16 接的，语句一条都没合——"100 个同结构实体 SaveChanges 只发 1 条 UPDATE"是验收项，今天没过，归属记在 P1。
- `src/QuickSql.Abstractions/Tracking/EntityEntry.cs + EntityEntry.EntityType`：派生实体各占一份跟踪器、各自写自己那张表；继承映射（P2/P3）落地前，按基类纳入的派生实例写的就是基类那张表。
- `src/QuickSql.Abstractions/Tracking/SaveChangesReport.cs + SaveChangesReport.HookReplay`：与写侧三条链 Explain() 上同一口径：只记真改了的，没订阅或"看过但没改"都是空。

### Query

- `src/QuickSql.Abstractions/Query/ITrackedSet.cs + ITrackedSet`：orm.Select() 与 db.Set().AsNoTracking() 模型文本逐字节相等这条判据在 v0.16 之前不成立——那时 NoTracking 是 QueryModel 上的一个位、会被打成 NO TRACKING 一行，挪到引擎上才是"同一份模型、物化期决定跟不跟踪"的字面兑现。
- `src/QuickSql.Abstractions/Query/IQuery.cs + IQuery<T>.WhereIF`：2026-10-30 独立成口——Where 本名只留给无条件那一颗，条件拼接在名字上就该是条件口（IF 大写与 SqlSugar 的 WhereIF 逐字同形；FreeSql 是小写 WhereIf，名字跟 SqlSugar 走）。
- `src/QuickSql.Abstractions/Query/IQuery.cs + IQuery<T>.IgnoreQueryFilters / IgnoreQueryFilter / WithSql / OrderBy(string) / FromPrimary / ToArrayAsync`：这一族只落在单表链（arity 1）这一档，联表链上没有对应口子，是一处已记名的缺口（联表后想跳过过滤器只能回单表链重起）。
- `src/QuickSql.Abstractions/Query/IQuery.cs + IQuery<T>.UnionAll`：SqlSugar 的 Clone() 把 Take/Skip/OrderBy 原样复制进每一腿，是"取前 10 行变每腿各取 10 行"的反面教材；本家在腿上点名这些当场拒。
- `src/QuickSql.Abstractions/Query/IQuery.cs + IQuery<T>.AnyAsync`：v0.38 第十九笔从"COUNT(*) 大于 0、语义对但没到最优"改成"SELECT 1 取第一行"的存在性探针。

### Sql

- `src/QuickSql.Abstractions/Sql/ReadPlan.cs + ReadPlan`：实测装箱口 95.2 ns / 24 B、直读 89.0 ns / 0 B（每行每列）；做成 readonly struct 让票本身不落堆（每场次只剩那一颗 byte[]，字段 12 字节按值传比按引用传堆对象便宜）。序号向量与直读档拆成两个参数会有"向量按 0..n-1 探、序号却带偏移"的错位空间——现在的加法只在一个地方发生。
- `src/QuickSql.Abstractions/Sql/SqlWriter.cs + SqlWriter.AppendIdentifier`：发射层每列都要过一次这条路，一次中间串拼接是实测"每列约 304 B"分配的大头——也是 NeedsQuote 不许顺手跑一趟大写映射的理由。
- `src/QuickSql.Abstractions/Sql/ISqlDialect.cs + ISqlDialect.DriverAcceptsPrefixedParameterNames`：SQL Server 带前缀在两台实例上绿过 v0.19–v0.31、摘前缀后也绿（v0.32 起）；虚谷交 ":p0" 得 E10036"未绑定变量 P0"，必须摘；另三家"两种都认"只是 v0.32 的一句观察、没有各自的活库复点，按"没有实测就不改默认"留在摘的那边。
- `src/QuickSql.Abstractions/Sql/ISqlDialect.cs + ISqlDialect.SupportsDriverSavepoints`：虚谷实测（探针 obj/logs/v37-xg-recon-savepoint-1.log）：XGTransaction 只覆写无参 Rollback，带名字的 Save/Rollback(name)/Release(name) 三颗停在 DbTransaction 基类声明上，调用即 NotSupportedException；同一台库把文本三句收下且语义正确——保存点之后插的行被 ROLLBACK TO SAVEPOINT 撤掉、之前的行同事务仍数得到，RELEASE 后提交库里剩 1 行。同次探针证伪两件事：SAVE TRANSACTION 在这家是语法错（E19260）；用 reader 跑非查询的保存点语句会把进程撞在驱动 get_nextrow 的原生 0xC0000005 上——所以这三句执行层口径是"非查询"，由 AdoExecutor.ExecuteSavepoint 决定怎么发。取 true 的四家（SQL Server/PostgreSQL/MySQL/SQLite）各有活库见证（WritePathTests 的嵌套事务一场，四场跑过且绿）。
- `src/QuickSql.Abstractions/Sql/ISqlDialect.cs + ISqlDialect.AlwaysTruePredicate`：五家里四家吃默认、一家覆写，而那一次覆写今天不产出任何文本；IN 全集与空集合 Any() 两个用途今天没有接线，缺口记在 P1。
- `src/QuickSql.Abstractions/Sql/QuickFx.cs + QuickFx.DateRange`： endDate 端到端等价——求值后的参数与手写 AddDays(1) 逐字节相同（QuickFxTests 的口径等价钉）；beginDate 大于 endDate 是合法空段照发不拦，endDate 传 DateOnly.MaxValue 时上界那半边不发。
- `src/QuickSql.Abstractions/Sql/SqlLogging.cs + SqlLogging.ToLogSql`：已知代价——SQL 文本里若出现与参数同名的一段字符串字面量（如 N'a@p0'）会被一起替换；这是给人看的诊断输出，不做词法解析。

### Providers

- `src/QuickSql.Abstractions/Providers/ConnectionStringCatalog.cs + ConnectionStringCatalog`：.NET 10 实测（合成串 "; Initial Catalog=MedicineNext;"）：裸 DbConnectionStringBuilder 只按字面键比、仅忽略大小写——TryGetValue("Database") 对 Initial Catalog 写法返回 false，"InitialCatalog"、"initial_catalog"、双空格写法也一律 false。本项目实测过的故障形状：只读 Database 的实现让 CanCreateDatabase 一路报 true、建库那一步静默没跑，最后连不上库的是调用方，MySQL 报的 Unknown database 里已经没有"本应建库"这件事。
- `src/QuickSql.Abstractions/Providers/Bulk.cs + IBulkCopyChannel.UnavailableBecauseOf`：实测真库 8.4.8——缺 AllowLoadLocalInfile 时 Auto 交回 Method=MultiValue、Degraded=false，这句话原样进 Reason（Auto 不算降级，它没承诺过任何一层）；对照量具 m1 : m2，补上那颗连接串参数后同一条语句 24 格全绿。T-SQL 与 PG 血统默认 null（装载类不欠连接串任何开关）。
- `src/QuickSql.Abstractions/Providers/IDbProvider.cs + IDbProvider.ClassifyIntegrityError`：PG 实测（2026-10-23）约束名在 DbException.Data["ConstraintName"]。首批四家；18 颗叶子里 11 家有实现（PG 系 6 + MySQL 系 3 + SqlServer + Sqlite），DuckDb 显式退回"判不出"，其余 7 家交在这一格上。
- `src/QuickSql.Abstractions/Providers/IDbProvider.cs + IDbProvider.ChangeGuard`：分布 1 : 4——金仓一族一句 IS DISTINCT FROM；T-SQL、Oracle 血统、MySQL 血统与虚谷四家走三段 OR，第四家落进来的理由与前三家不同一条（见 MySqlChangeGuard），第五家又不同一条（见 XuguChangeGuard）。
- `src/QuickSql.Abstractions/Providers/IDbProvider.cs + IBulkStrategy`：装载通道三家落地（SqlBulkCopy、MySqlBulkCopy、PG 二进制 COPY 导入器），ParameterBudget 有四个真读数（2100 / 32767 / 65535 / 没报的家走共用层的 100）。"缺成员整家退多值档"是看不见的失败：MySQL 血统第一版就这么静默退了一整版——驱动在 C# 调用面与反射面之间有四格形状差（可选参数、ValueTask 返回值、构造子重载、没有 Dispose 面），逐颗见 MySqlBulkCopyChannel 的改判段；离线册只能证形状不能证命中，每一家都要在真库上跑一次才算接上。原稿那颗 CanResolveKeys 没有进来：那一档不是"通道能不能给键"，是"这一条出口要不要键"，后者是出口的形状不是库的能力。
- `src/QuickSql.Abstractions/Providers/IDbProvider.cs + IBulkStrategy.SupportsMergedStatements`：实测（集成册 BulkWritePathTests，五家各一趟，obj/logs/v40-{mysql,sqlserver,postgresql,xugu,sqlite}-bulkwrite-2.log）——UpdateRange 的合并档（四句 UPDATE 并成一条命令）在 SQL Server 与 MySQL 两支落库成功、逐列读回一致；没表态的三家（PG 血统、虚谷、SQLite）Auto 走的确实是逐条、点名 Merged 确实降级、Never 确实拒在连接之前。反面仍缺：PG 血统那家有活库，缺"在它身上跑一次并好的批"那一格——那次绿了再把 false 翻成 true。
- `src/QuickSql.Abstractions/Providers/IDbProvider.cs + IBulkStrategy.UpsertClause`：两种形状都是量出来的（探针 obj/probe-upsert、obj/logs/v40-upsert-shapes2.log）：MySQL 8.4.8 上老的 VALUES(col) 写法仍收下但每句报两条 Warning 1287（厂商写明后续版本移除），所以接 8.0.19+ 的行别名写法——同一条三值批收下且 SHOW WARNINGS 为空；PG 上 ON CONFLICT (键) DO UPDATE SET 列 = EXCLUDED.列 的三值批同样收下。与 DbCapabilities.Upsert_* 那三位不重叠：位是公开能力清单、槽是写侧选层判据，那三位今天仍无读者；若把 UpsertClause 从 IBulkStrategy 挪进方言层（它本质是语法不是批量档位），要重接已跑绿的 UpdateRange 档，记在 P1。

### Write

- `src/QuickSql.Abstractions/Write/IWriteBulk.cs + IUpdateBulk<T>.WithBatchTransaction`：实测 2000 行整行更新在同一台远端 InnoDB 实例上 6386 ms : 263 ms，差距全部来自提交次数（裸 ADO 同一形状 7419 : 920 是对照组）；出处 obj/logs/fscompare-C.txt S3 四支诊断档。2026-10-07 的裁决：默认 true；判据是语句数不是块数。
- `src/QuickSql.Abstractions/Write/IWriteBulk.cs + BulkWriteMethod.Upsert`：2026-10-07 第二条裁决——Auto 的取档判据第一条是语义而不是速度，upsert 不在阶梯上；"降级退到整行改、语义跟着退"写进 BulkWriteReport.Reason。
- `src/QuickSql.Abstractions/Write/IWriteBulk.cs + IUpdateBulk<T>.ExecuteBulkAsync`：降级不留痕于 AOP——本档的 BulkDegraded 接线等事件形状改成两种档位通用那天再补（缺口）。
- `src/QuickSql.Abstractions/Write/IUpdate.cs + IUpdate<T>`：UPDATE 链没有自增主键回填那一档——回填要的是改后整行（OUTPUT deleted/inserted 的另一套形状），随 SaveChanges 的 P1 一起设计。
- `src/QuickSql.Abstractions/Write/IUpdate.cs + IDelete<T>`：软删不往 WHERE 加"且尚未置位"那一支，加了就要先有人裁"被挡住的行算不算一次并发冲突"（P1 余项）。
- `src/QuickSql.Abstractions/Write/IInsert.cs + IInsert<T>.WithBulk`：rowsPerRoundTrip 是分块公式里后来补上的第二项（request.MaxRowsPerStatement）；只许往下压不许往上抬——抬过参数预算会产出驱动直接拒掉的语句，即"换宽表就出事"那一类。

### Schema / Values

- `src/QuickSql.Abstractions/Schema/IEntityTypeSchema.cs + IEntityTypeSchema<T>.Read`：旧的 ReadOnlySpan<int> 直读档已撤——它每次都 ToArray，而它想省的"数组间接"在装箱口面前不是主要的那笔钱。
- `src/QuickSql.Abstractions/Values/IValueBinder.cs + IValueBinder.CreateParameter`：hint 为 null 的猜测路径走反射式查表，热路径必须传非 null。

### Transactions

- `src/QuickSql.Abstractions/Transactions/IQuickSqlTransaction.cs + IQuickSqlTransaction`：AsyncLocal 实测——在被调方内写 AsyncLocal，await 之前或之后调用方都看不见，只有同步被调方的写入会回到调用方的帧；于是 await orm.TransactionAsync() 会得到"对象拿到了、后面的写入却没进事务"的句柄。规格 §6 的示例已按同步形状改写（v0.12 回写）。
- `src/QuickSql.Abstractions/Transactions/TransactionResult.cs + TransactionResult.RollbackFailure`：此前回滚失败会从 finally 里冒出来把业务异常整个换掉——现场只见"回滚失败"，真正的回滚原因（锁冲突/约束）无迹可寻；v0.39 起两条分开，各有各的原文。
- `src/QuickSql.Abstractions/Transactions/TransactionResult.cs + TransactionResult`：规格原稿写了 r.ErrorCode，但 DbException 这一层没有与库无关的错误码可读（SQL Server 的 SqlException.Number、金仓的 PostgresError.Code 都在派生类型上）；要做就得先给驱动 SPI 加 IDbProvider.DescribeError(DbException)——真出现"业务要按错误码分支"的需求时连同 SPI 一起做（P1 行）。
- `src/QuickSql.Abstractions/Transactions/TransactionPropagation.cs + TransactionPropagation`：对应规格 §6 (4)。

### Aop

- `src/QuickSql.Abstractions/Aop/QuickSqlAop.cs + QuickSqlAop.CudBuilding`：读侧的 QueryBuilding 与 CommandCreating 仍是桩（P1 那格）；黄金样本里"钩子表为空时产出不变量"的断言继续有效。
- `src/QuickSql.Abstractions/Aop/QuickSqlAop.cs + QuickSqlAop.BulkDegraded`：原稿要求每一次降级在三处留痕（LogLevel.Warning + OTel 标签 + BulkReport），而本仓库既没有日志器也没有 OTel，三处里今天只有 BulkReport 自带——"务必用 FallbackPolicy.Never 在 CI 跑一遍"那句在此前一直没有对象。
- `src/QuickSql.Abstractions/Aop/QuickSqlAop.cs + QuickSqlAop.Executing`：v0.38 前这一格"声明在场而不响"；CommandLogEvent 那颗自陈"它报的是结论不是意图"——Route 要等取连接那一步由 DataSourceRouting.Resolve 才判得出。
- `src/QuickSql.Abstractions/Aop/QuickSqlAop.cs + QuickSqlAop.EntityWritten`：只覆盖会话那一条路；链式 orm.Insert / UpdateRange / DeleteRange 不走跟踪器，那三条路上没有这一格（各有 AuditValue 与 CudBuilding）。
- `src/QuickSql.Abstractions/Aop/QuickSqlAop.cs + QuickSqlAop.SoftDeleteValues`：链式 orm.Delete().Where() 那一档没有行可给、不触发这一颗——那一档要自己写"谁删的"那一格。

### 元数据契约（列与默认值）

- `src/QuickSql.Abstractions/Metadata/ColumnDefaultText.cs + ColumnDefaultText`：bool 的两个字面量由各家按本家布尔存储形给（BIT/TINYINT 落 1/0，原生 BOOLEAN 落 TRUE/FALSE，ClickHouse 落 true/false）；字符串的引号、Unicode 前缀与反斜杠转义走 SqlWriter.AppendStringLiteral，与列备注同一条路。

---

# QuickSql 注意事项归档 · Part B（Core：Execution / Orchestration / Write / Tracking）

> 2026-11-02 对 QuickSql.Core 四个子目录（Execution / Orchestration / Write / Tracking）做"简短 `///`"精简时，
> 从注释里摘出来的坑位、边界、已知限制、实测读数、重开条件与历史账。代码里只留一两句能自立的说明；
> 这一份收"展开才说得清"的部分。锚格式：`文件相对路径 → 成员名`。

## Execution

- `src/QuickSql.Core/Execution/AdoExecutor.cs → MaxPreallocatedRows / QueryAsync`：预容量上限 8192 的出处——门禁钉都在 1 000 行那一档，一次扩容到 8192 只需 13 次翻倍；而"取全部"那一档（`Take(int.MaxValue)`）若按请求行数开数组，消费侧 `pageSize = int.MaxValue` 会在读第一行之前 `OutOfMemoryException`（Array dimensions exceeded supported range）。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → SessionTransaction`：归属不是线程安全的——同一实例上的并发分支共用一条连接、一个 DbTransaction，要并发只能各自开流或各自建实例，这一档不设闸门。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → BeginTransaction(string,…) 与 RentAsync`：跨集群的提交协议（两阶段、XA、按 SAVEPOINT 拼一场"看着像一场"的事务）规格未裁，重开条件 = 规格裁定后才有形状。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → BeginTransaction`（事务可选闸门）：历史账——这是"ClickHouse 重开·笔一"新增的格；此前 provider 上 `DbCapabilities.Transactions` 在 src/ 里没有真实读者（只有 SchemaSyncer 读过位表的另一颗），"能力位零消费者"旧账是靠把读者立起来结掉的。保存点那一支不用另设铁闸的原因：环境事务只能由这一格开出来，本格先抛之后保存点在本家不可达。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → ExecuteSavepoint`：实测读数——用 reader 跑文本保存点会把进程撞在虚谷驱动 `XGDataReader.get_nextrow` 的原生 0xC0000005 上（探针日志 `obj/logs/v37-xg-recon-savepoint-1.log`：第一趟走 reader 全红，第二趟全改 ExecuteNonQuery 之后 Q-08 到 Q-15 一次跑完）。三口都没有结果集，`affected` 不是判据（`OK affected=0` 与库里真建出了保存点是两件事，真判据是语义断言）。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → Bind`：历史账与实测——摘方言参数名前缀是 v0.32 一台真库逼出来的：虚谷驱动只认不带前缀的参数名，交 `":p0"` 得 E10036「未绑定变量 P0」，出处 `obj/logs/v32-recon-orm-1.log`（O-10…O-14 五条全红）与 `obj/logs/v32-recon-bind2-1.log`（摘前缀后九问全绿）。Substring 的代价已进门禁：1 000 行 × 29 列 +928 168 B、26 颗参数腿各 +832 B 等（`obj/logs/v32-gate-15/16/17.log`）。曾考虑第三条路（执行器内按命名形状缓存摘好的名字）维持不做：把执行器与名字拼法焊死，前两条路（DriverName 载体 + DriverAcceptsPrefixedParameterNames 声明位）落地后没有存在理由。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → WriteWithResultAsync`：历史账——"跳到第一个带列结果集"那一停是 v0.34 第一笔补的：MySQL 血统驱动的多语句批把第一个结果集交回 INSERT 自己的 0 列结果，键在下一个结果集里；为语句后回读而加，不为任何一家改路。触发器多塞进行数不在"返回受影响行数"的账里（那是另一项的范围）。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → ConnectionRelease`：实测读数——虚谷驱动只有 `Close()` 把会话还回池，`Dispose()`/`DisposeAsync()` 不还；`Close()` 单用与 Close+DisposeAsync 各跑满 3000 轮不红，`Dispose()` 单用与 DisposeAsync 单用正好在第 1000 次 Open 红，与"开完不还只交给终结器"同一颗数（探针 `obj/probe-xugu/Program.cs`，日志 `obj/logs/v40-xugu-release-shapes.log`）。另两道底：同一串累计 7500 次 Open→Close→Dispose 不红、300 条同时在场不红——1000 限的是"没还回去的会话条数"，不是登录次数也不是并发条数。
- `src/QuickSql.Core/Execution/AdoExecutor.cs → SessionTransaction / QuickSqlTransaction.For`：历史账——AsyncLocal 只有同步被调方写得回调用方的帧是 v0.12 实测纪律；ABP 工作单元在自己的 async 帧里开事务、回到仓储帧槽位为空，"一场工作单元一次提交"曾悄悄退化成每句自提，会话事务（钉实例）就是为这一形扩的归属。
- `src/QuickSql.Core/Execution/QuickSqlTransaction.cs → Rollback`：doc 对齐账——原稿注释写"不抛"与实现一直不符（try/finally 没有 catch，驱动的回滚错误照抛）；承诺已改为如实：幂等与 Finish 一定执行，异常照实向上。
- `src/QuickSql.Core/Execution/TransactionCapability.cs → 类注释`：历史账——分岔依据补齐前，全 src/ 对 `DbCapabilities.Transactions` 掩码的真实读者为零（只有 SchemaSyncer 读过别的位），"能力位零消费者"旧账靠立读者结掉，不是靠改位表；行为诚实条款同段也住在位注释与用法册 §2.4，三处同源、改判必须三处一起动。
- `src/QuickSql.Core/Execution/ConnectionSources.cs → 类注释`：连接串"不进版本库"的边界——连接串只活在这一格与 Options 上，不进元数据、不进契约。

## Orchestration

- `src/QuickSql.Core/Orchestration/DbContext.cs → _sets / SessionGate / ChangeTracker`：并发实测——共享会话的裸 Dictionary 并发首写被 MySQL 真库上的 ThreadConcurrencyPathTests 当场抓到（Operations that change non-concurrent collections must have exclusive access）；EF Core 的集缓存（IDbSetCache）同一档也是并发字典（对照记录）。
- `src/QuickSql.Core/Orchestration/DbContext.cs → Gate / SessionGate`：落地前实测读数（SQL Server LocalDB，各循环十遍）——并发读 6/10 在 DetectChanges 炸 NRE、4/10 静默；并发写 10/10 把 2 行双倍落成 4 行，全程无异常。最后那格比崩溃危险一个量级，只有对账才看得见。EF Core 同款两句实话：尽力检测、抓到即拒。
- `src/QuickSql.Core/Orchestration/DbContext.cs → OnModelCreating`：已知边界（记 §14）——结构同步走组装根那份注册表，覆写模型里新配的实体不在它的清单上；要同步它们就得把实体也喂给 orm 级 Contract〈T〉，或等这一格单独立账。
- `src/QuickSql.Core/Orchestration/DbContext.cs → AddRange`：不是无痕降级但也没有回滚——同键冲突抛出时批次是半成品，已收进会话的那几只不退回。
- `src/QuickSql.Core/Orchestration/DbContext.cs → DisposeAsync`：历史账——"这个方法不能是 async"是示例第一跑撞出来的：async 方法里哪怕第一个 await 之前赋值 AsyncLocal 也只活在它自己的副本上，调用方那条流从此一直留着"写过"的戳子，读写分离静默失效且绿得看不出来。
- `src/QuickSql.Core/Orchestration/QuickSqlOrm.cs → SchemaSyncColumns / SchemaSyncDropIndexes`：决策记录（2026-10-06 用户当面定的）——"改了表备注、小数长度、索引那些都要同步"（列级档默认开）；"补一列与删一条索引不是一个风险档"（删索引默认关）。
- `src/QuickSql.Core/Orchestration/QuickSqlOrm.cs → Filters`：欠账/重开条件——全局过滤器今天管不到 SaveChanges 与批量：那两条路上的 WHERE 长在 EntityTracker 上（判据与落地记录原注 §10.7 / §14 P1）。
- `src/QuickSql.Core/Orchestration/QuickSqlOrm.cs → CreateSharedContext`：并发边界——共享的是同一只 ChangeTracker 而它不是线程安全的，父帧先建再 Task.WhenAll 扇出的并发分支会同时改同一本账，这一档不设闸门（加了就是新公开语义，原记 §14 P1 余项）；要并发就各自开流或各自 CreateDbContext。
- `src/QuickSql.Core/Orchestration/QuickSqlOrm.cs → Update(entity)`：历史账——RefuseCollection 判据选"实例形状"而不是 typeof(T).GetInterface，是因为后者在裁剪产物上要求把 DynamicallyAccessedMembers 一路标到公开方法的泛型参数上；"整只实体写回 + 某一列在库里算"的混合现场在这一档不成立（原记 §14）；编译期就红这件事欠着（本家没有能看调用点的分析器）。
- `src/QuickSql.Core/Orchestration/QuickSqlOrm.cs → Select/RootEngine/Insert`：历史账——读路径 RUC/RDC 入口税是 G1 预翻译没落地前的形状，"G1 路径全链路 0 反射"闸门达成后应从入口逐个摘掉；写侧（Insert/InsertRange/UpdateRange/DeleteRange）已零反射（原 §14 验收项）。
- `src/QuickSql.Core/Orchestration/DbSet.cs → 类注释`：历史对照——"只差一个标记位"从 v0.16 起为真：跟踪与不跟踪的差别在引擎的标记位上，不在模型里；ITrackedSet 改名的理由写在它自己的注释上（历史注）。
- `src/QuickSql.Core/Orchestration/DbSet.cs → DiscoverFkColumns / ChangeTracker.ResolveCascadeForeignKey`：不变量——补载外键发现的三档与级联落键的三档同名同序，同一根边在两条路径上不许有两个答案，否则「级联插得进去、Load 捞不上来」是死路。

## Write

- `src/QuickSql.Core/Write/InsertEngine.cs → ReadBackAsync`：实测读数（v0.32）——虚谷活库上 `INSERT … RETURNING "ID"` 执行成功、行真进去了，回包却是 FIELDS=0 ROWS=0（`obj/logs/v32-recon-keys-1.log` N-06，多行 VALUES 形同红 N-07；`obj/logs/v32-recon-orm-2.log` O-11 把它印成"写侧 OK 回填的 Id=0"）；少一行比少一列更危险（少一列当场红，少几行是绿着交错值）。"这一判不改变任何一家已连过库的产出"由 §13.4 那 95 条在授权实例上复跑背书。
- `src/QuickSql.Core/Write/InsertEngine.cs → ExecuteAffrowsAsync`：结构边界——文本层出口拒收 Auto/CopyChannel 请求的另一半收益：它不可能走到装载通道，签名上没有 RequiresUnreferencedCode，写路径已有调用点都不必改。
- `src/QuickSql.Core/Write/InsertEngine.cs → Decide / ExecuteBulkAsync`：重开条件——Auto 拿不到装载通道时，拒绝理由要进 Reason：现场缺的常常是一颗连接串参数（MySQL 血统的 AllowLoadLocalInfile 是第一颗），判窄成"静默走 L3"会让调优的人去查 provider 那一侧。
- `src/QuickSql.Core/Write/InsertEngine.cs → CopyRequestFor`：性能账——名字按列物化一次取代逐格插值：此前 `"c" + column` 在 row × column 双层循环里逐格插值，百万行宽表的 COPY 预处理是上百万次字符串分配。
- `src/QuickSql.Core/Write/InsertOrUpdateEngine.cs → 类注释`：已知缺口（原记 §14）——upsert 的审计种类报 "update"：一条 upsert 的行可能插可能改，"只在插入时补 CreatedAt"那类语义留给能判命中与否的那一天，今天不假装分得开。
- `src/QuickSql.Core/Write/BulkWriteEngines.cs → UpdateBulkEngine.ExecuteBulkAsync / BulkAtomicity`：实测读数（改判依据，2026-10-07）——并档把往返并了而没把提交并了，InnoDB 每次提交 flush 一次 redo；同一台远端实例、同一批 2000 行整行更新 6386 ms ∶ 263 ms，裸 ADO 对照组 7419 ∶ 920，差距全部来自提交次数（`obj/logs/fscompare-C.txt` S3 四支诊断档）。原稿"跨命令不是原子的：要整批原子就包事务"按速度改判为默认整批一次提交。
- `src/QuickSql.Core/Write/BulkWriteEngines.cs → UpdateBulkEngine.Build`：性能账（v0.39 修的那格）——过滤器与点名条件此前在行循环里逐行重付：1000 行 × 2 条过滤器 = 2000 次完整表达式树归约；现翻译一次、行循环只做树拼接。
- `src/QuickSql.Core/Write/BulkWriteEngines.cs → Decide`：语义边界——Upsert 档退档换掉的是语义不只是速度：库里没有的那一行不会再插进去；Auto 不承诺任何档、点名而拿不到才算降级。
- `src/QuickSql.Core/Write/BulkWriteEngines.cs → DeleteBulkEngine`：形状边界——复合主键拒收的理由：IN 清单装不下元组，逐对 OR 展开又绕开参数预算的账，两害都不如指路。
- `src/QuickSql.Core/Write/StatementEmitter.cs → ConservativeParameterBudget`：对照读数（v0.31）——五家里走到这一份兜底的是两家（OracleProviderBase.Bulk 与 XuguProvider.Bulk 交回 null），报得出数字的是三家：2100 / 32767 / 65535；逐家块数由 DialectParityTests 钉死（3 / 1 / 50 / 1 / 50）。
- `src/QuickSql.Core/Write/StatementEmitter.cs → ApplyCud`：坑——多播调用会把同一个事件对象递给每个订阅者，各自写 Rewritten 就成"最后一个人赢"，前一个人改出来的文本第二个人看不见；必须逐个派发、每人一份事件。
- `src/QuickSql.Core/Write/StatementEmitter.cs → ApplyBinding`：已知缺口（原记 §14）——读侧的参数不过这道钩子（执行层 bind 之前发射是为回放逼出来的）。
- `src/QuickSql.Core/Write/UpdateEngine.cs → ColumnOf / SplitTable`：已知边界——IDENTITY_INSERT 那条路（显式写自增列）P0 未接；SET 值位不收行号（写侧没有"换个表源"可言，读侧的 WindowGuard 出路两样都接不上）。
- `src/QuickSql.Core/Write/UpdateEngine.cs → Check`：差异记录（原记 §14）——链式 UPDATE 只过值级两把尺子（长度/必填），DataAnnotations 与 Custom 档不参与（拿整实体校验得造假实体，会把没填的列判成"缺"）。
- `src/QuickSql.Core/Write/Storageable.cs → Canonical`：边界——整型统一成 long 的代价：ulong 键值高过 long.MaxValue 那一颗在对账前抛溢出；主键大到那一档的表不在本库服务面里，抛比错对账诚实。
- `src/QuickSql.Core/Write/Storageable.cs → KeyProjector`：性能账——存在性对账那一句从整行投影收成只投键列（`x => new T { Key = x.Key }` 落进投影翻译的成员初始化臂，选列按槽位出）：省的是**列宽不是行宽**——谓词只命中被点名的键，行数上限本就是清单大小，所以宽实体（长文本/JSON/位图列）才量得出差，三列小表基本无感。删除桶那一读刻意不动：`ExecuteDeleteAsync` 全程只用键，但 `DeleteList` 交出去的是"库原样读回的那一份"给人看，收成键投影就等于交出非键列全默认值的半成品。A/B 读数（sqlite 靶库，`Reconciliation_reads_only_the_key_columns`）：投影在场时对账句是 `"t0"."Sku"`，摘掉后是 `"t0"."Id", "t0"."Sku", "t0"."Quantity"`。
- `src/QuickSql.Core/Write/Storageable.cs → ToStorageAsync`：坑——对账两读都得带着全局过滤器（软删那一族）走 `orm.Select<T>()` 这条正路：过滤器只叠进 WHERE、从不进选列，所以收窄选列不影响它；哪天为省事把存在性判定换成绕过过滤器的裸 SQL 或逐颗键 EXISTS，库里被软删的行会判成"存在"→ 发一句打不到行的 UPDATE，或反过来判成"不存在"→ 种出重复行。

## Tracking

- `src/QuickSql.Core/Tracking/ChangeTracker.cs → DetectChanges`：重开条件——哈希短路（没碰过的行不进列比较）还没接线，今天全员逐列比；该门禁项（ChangeTrackerDetect10k）未实现，理由原记 §14。
- `src/QuickSql.Core/Tracking/ChangeTracker.cs → EnsureNoSplitWrites`：裁决记录——分表实体与跟踪器互斥：会话的账按主键记，而分表里"同键的两行"是能存在的两行（Id 自增而表按月切时，两张片各自的 1 号互不相干）；身份映射不成立要先定"跨片的一行由什么指认"（原记 §16.4 已裁）。
- `src/QuickSql.Core/Tracking/ChangeTracker.cs → WriteCluster`：裁决记录——跨集群的保存"整批拒"而不是"按集群拆成几场事务依次提交"：后者改了「一批=一场事务」的承诺，第二批失败时第一批已在库而报告只有一份 Success=false。
- `src/QuickSql.Core/Tracking/ChangeTracker.cs → HasPendingTemps / ResolveTempKeys`：边界——自引用（同一类型内部）的临时键换不出：类型序排不出同一类型内部的先后（原记 §14，"先插一行空外键再补 UPDATE"的延迟写入那一档不做）。
- `src/QuickSql.Core/Tracking/ChangeTracker.cs → FlushDueAsync`：坑——「同一张表这一轮既插又删」要提前整档删除：先插撞在还赖在表里的那一行（23505 duplicate key，撞的还是本轮自己要删的行）；提前的是整档删除而非单表删除，否则拆散「被依赖的那一行最后删」不变量。
- `src/QuickSql.Core/Tracking/ChangeTracker.cs → Reparented`：已知边界——改父（同一只实例从甲串挪到乙串）不接线：要写的是一句 SET 外键，会话不替它猜（原记 §14）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → Signed / AdaptKey`：已知边界（原记 §14）——short/byte 外键捏不住 int/long 临时键：探测把它当成"不是我发的号"放过去，不换也不拒；真正触发"不替它截断"的是 long 主键配 int 外键且库里键已大过 int.MaxValue。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → _writable / Unwritable`：坑——主键/自增/并发列判脏了却不能进 SET 是"响了"而不是"剔掉"：静默剔掉的后果是「改了主键保存三次没生效」。并发列报错还有一种来路：上一批保存被回滚过（回滚不会把已读进实体的值撤回，会话停在"以为写过了"的那一版）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → Track`：边界——物化路径不覆盖已跟踪实例的值（可能已被改过还没保存）；"重读并让库赢"是 Refresh 的活，未接线（原记 §14 P1）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → AssignTempKey`：边界——临时主键只服务"单列、带符号整型、自增"一种形状；客户端生成的键（Guid 那一派）不代填也不回读（原记 §14）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → PostFlush`：边界——整批抛在半路时结算走不到，临时键的账不清：那是"回滚后同一会话继续写"那一格的题（原记 §14）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → WriteUpdatesAsync`：口径账——合并档的 Rows 是真变化的行数（守卫挡掉"库里已经是这个值"的行），逐条档是命中数；两档各有一条真库用例钉着（原 §12.4 第 2 项）。合并之后 Rows 少于行数不能区分「没变」与「已经不在了」，要逐行归因开单行桶。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → WriteDeletesAsync`：裁决留白——软删在册时 0 行只有"版本不匹配"与"行已经不在了"两种成因；刻意不往 WHERE 加"且尚未置位"那一支，加了才需要"被挡住的行算不算一次并发冲突"那句裁决（归用户，原记 §14 P1 余项）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → IsIntegerClrType`：历史账——旧判据把 uint/ulong 也算进"应用自维护"，与 IncrementTokenValue 的支持面对不上，那类实体会安静通过启动期、到第一次 SaveChanges 的回填步才炸；收窄后走回读（值库不改，语义成立）。
- `src/QuickSql.Core/Tracking/EntityTracker.cs → ConcurrencyPredicate`：坑——可空的并发列必须写 IS NULL：`[列] = @p` 在哪家都是 UNKNOWN，每一次保存都成假冲突而异常说"别人改过"。
- `src/QuickSql.Core/Tracking/IdentityKey.cs → 类注释`：已知账（原记 §14 P1）——键这一路的装箱发生在 schema.GetValue 取值那一步；消除它要给 schema 加一份非装箱的主键读取口。byte[] 按内容判是 v0.39 修的那格（object.Equals 对数组是引用相等，两次物化的同键行判不成同一身份）。
- `src/QuickSql.Core/Tracking/Node.cs → TrackedNodes`：实测读数——三个调用点各拷一份的代价在 1 000 只那一档上是 24 KB 一次保存（原 §12.8 第 13 行 ③−① 那一截），所以返回表本身并附带"枚举期间不许改"的约束。
- `src/QuickSql.Core/Tracking/Cascade.cs → CascadeWalker`：坑——级联游标每次 Add/Remove 新建一份，共用一份游标会让递归内层覆盖外层的边，「孙实体挂到错的父上」只有崩了才看得见。
- `src/QuickSql.Core/Tracking/TrackedEntry.cs → IsModified`：坑——`IsModified = IsModified` 这句看似无害的写法会把"没表过态"变成"表过态"，从此这一列不再参与比较；退回比较只有整行状态重设一条路。

---

# QuickSql 注意事项归档 · part C（QuickSql.Core）

> 2026-11-03 QuickSql.Core 九个分区（Query／Translation／Materialization／Schema／Sql／Metadata／Behaviors／DependencyInjection／DriverLoading）注释精简时摘出的坑位、边界、已知限制、实测读数、重开条件与历史账。代码里只留一两句能自立的说明；这一份收"展开才说得清"的部分。锚格式：`文件相对路径 + 成员名`（相对 src/QuickSql.Core/）。

### Query（Query/）

- `Query/QueryEngine.cs + QueryInBatchesAsync`：复合键此前若发「每列各一个 IN」是值的叉积——fk1=1,fk2=2 的父会配上 fk1=1,fk2=9 的子行，缝合静默出错；且键值元素是 object[]，按标量绑定驱动必炸。可移植形状是逐行等值的 OR 链（每行仍 N 颗参数，与预算分母同口径）。元组 IN (fk1,fk2) IN ((…),(…)) 要方言逐家声明支持形状，留给方言层立格（重开条件）。v0.39 修的那格。
- `Query/QueryEngine.cs + QueryInBatchesAsync`：不分块的后果不是慢，是整条子查询在驱动侧失败——SQL Server 参数上限 2100、Oracle IN 列表上限 1000 项；"父集合多大"由调用方上一趟查询决定，Take(int.MaxValue) 的"取全部"档不是假想（AdoExecutor 预容量那一格同一现场）。写侧早就按预算分块（§12.3），读侧这四条 IN 此前是漏网的那一族。
- `Query/QueryEngine.cs + QueryInBatchesAsync`：Materializer.EntityRows 的直读票按 reader 引用每场次自探（§16.21），跨块复用是它声明支持的形状。
- `Query/QueryEngine.cs + Detect / StreamAsync / QueryOp`：出处账——Detect 是 §8.2「物化之前先 DetectChanges」与「零额外分配」的落点；流式出口是 §12.7 E；QueryOp 是 §5.5 的稳定标识。
- `Query/QueryEngine.cs + AggregateAsync / AnyAsync / AggregateModel / Resolve / TotalCount`：§5.12 的账——"复用带 OrderBy 的 iq 先 Count 再取页必炸"高频事故由剥序挡住；Any 从 COUNT(*)>0 改为 SELECT 1 取第一行（记名的 P1，旧版"语义对但没到最优"的记账结清）；CountMethod.CountOverWindow（Q1 单往返）与 SeparateTransaction（Q3 同连接同事务一致性）未接入；派生表档"分组/DISTINCT/带页后聚合是错数字"的三形状出处；StripOrderingForCount=false 的报错表。
- `Query/QueryEngine.cs + PagedCore`：偏移溢出实测读数——int.MaxValue 档（消费侧仓储"取全部"的缺省，现场一次可授权角色列表撞到的）在 int 上翻成负数，发出去是 LIMIT -2147483648，库以 "LIMIT must not be negative"（PG 2201W）收尾，读起来像库坏了其实是溢出。
- `Query/QueryEngine.cs + FirstOrThrowAsync / AllAsync`：两格落地日期账（2026-11-01 补齐）。
- `Query/QueryModelPrinter.cs + Print`：规范文本的设计约束——表源两种非默认来路、合并形状、选片物理名、OVER 两段都必须在文本里看得见，否则等价性测试漏掉一整类差异；新增 ProjectionPlan 必须同时在 Printer 登记规范形态（已由异常消息钉住）。（要点已留注释，此条补出处叙述。）

### Translation（Translation/）

- `Translation/WindowGuard.cs + WindowGuard`：v41 门禁首场读数——每谓词 +8 B ∶ 一颗过滤器 +56 B 的涨幅不是这一格量出来的；把闸门整个摘掉再跑一场，六格读数一字不动（obj/logs/v41-gate-3-control-nogate.log），那笔账在别处（变更记录）。手写循环写法的先例是 DialectBase.RenderArguments。
- `Translation/ExpressionTranslator.cs + ReferencesScope`：MemberInit 臂的完整坑账——缺这一臂时"引用了行的成员初始化"被判成不引用参数，走进编译退路吐 InvalidOperationException（variable 'o' of type 'X' referenced from scope ''），补上后同形拿到"该形态当前无映射"（实测两形：new S { A = o.A } == default 与 new S { A = o.A }.ToString() != "x"）；net10 上 MemberInitExpression 基类是 Expression（属性叫 NewExpression 不叫 New），靠 UnaryExpression 兜不到（实测过）；两类仍未覆盖、症状同上（§14 已记）：集合初始化 new C { Items = { o.A } }（不走 OfType&lt;MemberAssignment&gt;）与 IN 的集合侧（new[]{ o.X }.Contains(y)，直接 Evaluate 不过分类器）。
- `Translation/ExpressionTranslator.cs + ValueEquality`：实测形状——Int32.Equals(Convert(lid, Object)) 那一格：intCol.Equals(someLong) 在 CLR 恒 false（装箱后只有同型才相等），照 = 发射会把"一行都不该中"翻成"值相同就中"；实测十八格形状见当时 ValueEquality 那条注释。历史账——原先这一支只认 declaring == typeof(string)，ABP 类仓储常态 x.Id.Equals(id) 整族落到"为 Object.Equals 注册映射"那句指错地方的报错上（Id 是 long/Guid 时绑到 Int64.Equals 或 Object.Equals，量侧还多一层装箱 Convert）。
- `Translation/ExpressionTranslator.cs + Call`（Contains 分支）：实测形状（.NET 10）——int[] 的 ids.Contains(x) 编成 2 元 MemoryExtensions.Contains；枚举数组同写法编成 3 元、末尾多一个值为 null 的 IEqualityComparer 实参；判别条件只能钉在"这两个类"上。（要点已留注释。）
- `Translation/ExpressionTranslator.cs + Evaluate / CompiledEvaluators`：编译产物缓存为链式复用服务（§5.11）；整句翻译的缓存要过"别名/参数位随查询模型变"那一关，没在这里一起做（边界）。
- `Translation/NavigationJoins.cs + NavigationJoinScout`：收养机制的历史——"两张而不是复用"当初按 Include 在前那一序裁的，判据是"复用会引用到一颗没进 FROM 的表"；那一半今天由收养时落成模型里的真 JOIN 顶住（Include 那条是编译期才决定的，模型那条不是），剩下那一半只是贵，贵在句子读得出来的时候就该省。
- `Translation/NavigationJoins.cs + _pending`：每谓词字节的分配账出处（§12.8 第 4 行）。
- `Translation/Scope.cs + ForFilter / Create(scout) / ForGrouping / Matches`：§10.7（过滤器形参可赋值）、§5.2（自动联表现场）、§5.3（分组绑定）、§5.1（按位置绑表）的出处账。
- `Translation/ProjectionTranslator.cs + DeclaredType / UnwrapType`：§5.7 的"NULL 交回声明类型默认值"规则与 §14 那格（int? 摊平后构造器臂抛 ArgumentException）的出处。

### Materialization（Materialization/）

- `Materialization/ReadOps.cs + ReadOps`：探测从每值挪到每场次的提升（§16.21 记的那条）——上一版每读一颗值问一次 GetFieldType，买 24 B 的省、赔 41.5 ns 的探（§12.7 C 末尾那张表）；搬到每场次一次后每值只剩 IsDBNull + 原始访问器，实测 89.0 ns / 0 B 对装箱口 95.2 ns / 24 B，两头都赢，那一刀才成立。
- `Materialization/ReadOps.cs + DirectRead`：非空值类型臂去掉 IsDBNull 的实测账——真驱动上这一截按行加总（八列，只有有臂的列付）mysql 268.9 ns、sqlite 135.7、虚谷 70.5、sqlserver 50.4、postgresql 3.7，出自 obj/probe-real 的「臂 ∶ 裸臂」两档；旧版每条臂"先 IsDBNull 再取"一次取值付两趟驱动调用。见证测试：TypedReadTests 与 ZeroBoxingTests 钉住"每个臂都必须出现一次 ValueConverter.Read"；ZeroBoxingTests.A_null_keeps_the_nullability_of_the_member 钉住可空臂族（删任何一首对应型重新摊平）。§12.7 C 第二条（某一列的读法只有一处可改）的出处。
- `Materialization/Materializer.cs + ValueConverter.Read`：被撤掉的强类型门完整账（2026-09-21 按实测撤）——原门写 GetFieldType(ordinal)==typeof(T) 时改走 IsDBNull+GetFieldValue&lt;T&gt;：真驱动（Microsoft.Data.SqlClient，LocalDB）int 列读 24 B→0 B，但 95.2 ns→136.7 ns，门的边际 +41.5 ns 买 24 B；别拿单独读数相加（GetFieldType 27.4 + IsDBNull 53.2 + GetFieldValue&lt;int&gt; 71.4 = 152 ≠ 实测 136.7，那些数各自单独测，只能取两行之差，逐格数字在 §12.7 C 末尾）；假驱动（门禁那片 DataTableReader）更差：没实现强类型路径、装箱一分不省（§12.8 第 3 行仍 736 B/行），物化时间 748 µs→1 145 µs；两侧同号——换 GC 压力、赔 CPU，刀不成立。成立条件记 §16.21：探测从每值提升到每场次并落到 GetInt32/GetDecimal 原始访问器（实测 49/53 ns，比 GetValue 的 91 ns 还快）。落地在 ReadOps/DirectRead（v0.38 第五笔丙），每场次一张 ReadPlan；标量/聚合档先领票是第八笔（结 §14 债③）。见证测试：TypedReadTests.The_read_door_is_byte_for_byte_the_converter（本句没被偷偷换掉）、ZeroBoxingTests（有臂的列不走装箱且两路逐格同值）。
- `Materialization/Materializer.cs + ScalarRows / ScalarArm`：第八笔推翻第五笔丙的理由——"调用点是泛型的、T 编译期不知道、臂是静态方法"不成立：ScalarRows&lt;T&gt; 里 typeof(T) 每支实例化都定死，按型落臂只是从"编译期已知"换成"实例化期已知"；真正留着的分别是空值守卫归谁问。
- `Materialization/Materializer.cs + To<T>`（DateTimeOffset 两臂）：ABP 家族 LockoutEnd 一派声明 DateTimeOffset?，而真 PG（Npgsql 10）在 timestamptz 列交回 Kind=Utc 的 DateTime——从前以 Convert.ChangeType 的 Invalid cast from 'System.DateTime' to 'System.DateTimeOffset' 收尾，宿主启动即 FTL、症状是整站登录不了；写侧对应 PostgreSqlValues 的 DateTimeOffset 臂，两边凑回来是同一个瞬时。
- `Materialization/Materializer.cs + ReaderKey`：手写相等换掉 record 自动生成的账——record 对数组字段比引用，形状相同数组不同永远判不等，缓存静默不命中且只在基准上表现为"没优化"；手写版换掉未命中那 1.8 ms 的表达式树编译，代价是命中路径一个小数组键（三槽位约 72 B，没单独测）。§12.7 C 第一条与 §12.7 B 容器规则、§12.8 第 5 行的出处。
- `Materialization/MemberAccessors.cs + MemberAccessors`：单独立支的来历——同一段体在五个地方各写过一遍（取键两对、引用/集合导航、Include 导航设置器），每遍都只记得反射定位、忘了反射调用，每行每颗键留一次反射调度（§12.8 第 8 行那一格）；换掉的反射调度按实测每次数十纳秒量级；G1 生成的实现自带等价委托（EntitySchemaRegistry 同一取向）。§12.7 A「写侧 0 反射」出处（CascadeEnter）。
- `Materialization/MemberAccessors.cs + ParameterlessConstructor`：实测——把可见性当门槛时 INSERT 与 SELECT 响同一句话（整族实体读写两侧一起拒）。
- `Materialization/MemberAccessors.cs + Getter / ReflectionEntitySchema.BuildReader`：门禁第 8 行（数 src/ 反射字样）约束了两处写法（方法组转换代替反射查找、局部 var 代替静态 MethodInfo 字段）。

### Schema（Schema/）

- `Schema/TypeTable.cs + TypeTable`：来历——各家方言的类型表早就用冻结表，唯独两个注册表停在 ConcurrentDictionary，每次取 schema 过一次分桶锁读路径（§12.8 第 5 行量的是这一格）；§12.7 B「读多写极少 → FrozenDictionary」出处。O(实体数²) 一次性重建换零锁读的算账。
- `Schema/GeneratedEntitySchemaRegistry.cs + _g1Types`：RegisteredTypes 曾只透传兜底那份——G1 命中的实体从不触碰兜底注册表，纯 G1 库上 SchemaSyncer 枚举不到任何一张生成表，SyncStructureAsync 静默交回 Empty；启动期"先 Sync 后写"的顺序下症状是运行期"表不存在"。
- `Schema/GeneratedEntitySchemaRegistry.cs + GeneratedEntitySchemaRegistry`：§12.7 A/B、§10.2（Fluent 压在注解之上）、§14「G1 路径下全链路 0 反射」验收项、§13.11 对拍的出处账。
- `Schema/ReflectionEntitySchema.cs + BuildReader / ReflectionEntityValidator`：§12.7 C 第二条、§13.11 对拍、§10.6（两条尺子在 ContractRules 一处）、§15.7 白名单收口（StringLength.MinimumLength 例外）的出处账。

### Sql（Sql/）

- `Sql/ChangeGuardShapes.cs + ChangeGuardShapes`：版本史——三段式从 SqlServerChangeGuard 提出；v0.30 第四家落进三段（它有 IS DISTINCT FROM 也有 &lt;=&gt;，落进来的理由不是结构性限制，而是"共用层发不出那颗算子、IS DISTINCT FROM 要按版本发"，来路记在 MySqlChangeGuard 类注释）；v0.31 第五家（虚谷）是第三条来路、也是唯一一条"按缺席读"的——既无结构性限制的证据也无有算子的证据（文档与两份产物都没出现 IS DISTINCT FROM），三段式不是选择而是唯一发得出的形状（那一格写在 XuguChangeGuard 第一段后半）；它记进 §14 的方式与另两条不同：那两条是"知道为什么"，这一条是"不知道为什么也必须这么发"。多数派 1∶4（一句 vs 三段）的账；§9.1 矩阵里 Oracle+ 行往后每接一家先落三段边。
- `Sql/DialectBase.cs + Precedence`（IsDistinctFrom 臂）：完整出处——IS DISTINCT FROM 排在比较符之下的依据是 PG 手册 4.1.6 Table 4.2（&lt; &gt; = &lt;= &gt;= &lt;&gt; 那一行在 IS ISNULL NOTNULL 之上，表由高到低）与文法产生式 distinct_expr: a_expr_compare IS NOT DISTINCT FROM a_expr_compare（左右操作数只吃到比较符一层）；9.5 之前这一档相反（手册 4.1.6 末尾 Note："IS tests used to have higher priority"，9.5 起才改）；§9.1 记的金仓 V8 内核是 9.2 一线，落在旧规则侧的可能性是真的——这一档两家可能不同形，未实测，记在 §14；今天谁都碰不到（合并守卫把 IS DISTINCT FROM 直接挂在 AND/OR 下，左右操作数都是列与参数），改这个数不动任何黄金样本（跑整套离线套件已核）。
- `Sql/DialectBase.cs + EmitRowNumber`：没有活库见证的那几家（§13.5 那本账上的离线档）此处读数是「文本钉住了」不是「库里接得住」，翻档要各家出处，不在这一格预支；DbCapabilities.WindowFunctions 位表今天零读者，判据落在代码真走到的那一格（ISqlDialect.SupportsJoinedWorks 头部口径）。
- `Sql/DialectBase.cs + EmitPagingQualifier`：§9.4"三套实现"到 Informix 血统（SELECT FIRST n SKIP m）变四套的账；与 OracleDialect 那一对（PagingWrapsSelect ∶ 空的 EmitPaging）同一种成对纪律。
- `Sql/DialectBase.cs + EmitFunction`：§1.2 原则 4、§5.8 内联纪律、§12.7 共享计划的出处账（错配的句子只在批量的第 3 千行上红，这一格没有第二次机会被评审看见）。
- `Sql/DialectBase.cs + Emit`（分页槽）/ EmitPagingOrdering / SourceAliasKeyword / EmitWrappedPaging：§9.4、§5.8 的出处账。

### Metadata（Metadata/）

- `Metadata/ContractResolver.cs + ResolveTable`（表备注）：备注那一格从前由自研 [Table(Comment=…)] 承担，该格退场后由组件模型 [Description]（System.ComponentModel）接棒，座次照旧排在 Fluent 之后。
- `Metadata/ContractResolver.cs + ResolveColumn`（备注链）：这条链从前只到 [Display]，[Description] 与它们同层竞争；后改为 Description 在前、Display 兜底。
- `Metadata/ContractResolver.cs + ResolveColumn`（并发列）：TimestampAttribute/ConcurrencyCheckAttribute 住在 EF Core 的程序集里，引它等于引 EF Core（语义依赖，已保留在注释）；ConcurrencyStamp 约定来自 ABP/IHasConcurrencyStamp 一族的现成拼写（已保留）。
- `Metadata/ContractResolver.cs + ResolveNullable`：Unknown 分支原注点名"整份 ABP/DDD 框架代码天天在这一格"（已保留精简版）；§10.6 必填尺子、§13.11 对拍、NullableAnnotation.None 的出处账。
- `Metadata/ContractResolver.cs + ResolveDeclaredFkColumns / IsReferenceNavigation / CollectionElementOf / ForeignKeyOf / ResolveSkipIndexes`：§10.2 层清单、§8.2 拓扑、§10.3、§13.11 的出处账。

### Behaviors（Behaviors/）

- `Behaviors/BehaviorRegistry.cs + BehaviorRegistry`：改判史——规格 §10.7 约束 3 原文说改写"发生在 CudBuilding"（文本层），本版改判到 AST 层，判的是那句话的目的（日志与审计看到真正发出去的那一句）而非字面；v0.18 那份文本改写机制没有被动过、还在原处，SqlServerCudBuildingTests 那几条仍钉着它。§10.7 约束 1（批量绕过过滤器）与约束 3 示例的出处。
- `Behaviors/BehaviorRegistry.cs + SoftDelete`：§10.7 示例第四句的那一只（出处账）；与 FilterRegistry 的约束 4 欠的是同一半（接口/基类键注册期查不出，已保留）。
- `Behaviors/BehaviorRegistry.cs + HasAny`：§12.8 写侧基线的出处账。

### DependencyInjection（DependencyInjection/）

- `DependencyInjection/QuickSqlServiceCollectionExtensions.cs + QuickSqlOrmBuilder / AddQuickSqlOrm`：删除的 EF Core 对照——"EF Core 风格的注册入口"、"与 AddDbContext 同一条判据"、"与 AddDbContext&lt;T&gt;(Action&lt;IServiceProvider, DbContextOptionsBuilder&gt;) 同一姿态"、"与 EF Core 把 IModel 缓存在单例里同一条路"；语义（Scoped 理由 = FilterRegistry 每实例一份、重对象 Singleton = 编译产物缓存一次）已保留在注释里。
- `DependencyInjection/QuickSqlServiceCollectionExtensions.cs + Replicas / Clusters / SchemaSync*`：§16.4②、§10.2 优先级 1、§9.4/§9.5 的出处账。

### DriverLoading（DriverLoading/）

- `DriverLoading/BuiltinDrivers.cs + BuiltinDrivers`：历史账——FreeSql 现场姿势对照（Type.GetType("FreeSql.MySql.MySqlProvider`1,FreeSql.Provider.MySql") 是它核心里写死的按名字的表，不是配置文件）；曾经的 QuickSqlDrivers.json 清单层（连 QUICKSQL_DRIVERS_CONFIG 环境变量与 DriverManifest 解析）整层删除：两份同构表必然漂移，漂移钉住后清单只剩覆盖手段一种身份，覆盖的两件事各有更直接姿势（换程序集名 = DLL 丢 drivers/；换工厂解析 = DriverRegistry.Register 注入）；原清单各行 $comment 的出处台账当时迁进逐行注释，本轮再收进本档；§1.7、§13.5 的出处。
- `DriverLoading/BuiltinDrivers.cs + postgresql 行`：v0.37 第二笔立行、第九笔实测改判——已在真库解析成功并跑完整本集成册（QUICKSQL_TEST_PROVIDER=postgresql，100 红 ∶ 14 skip，obj/logs/v37-pg-int-3.log；库回包异常型名 Npgsql.PostgresException，两份日志可读，证明"兼容"不是前件）；四颗派生键（opengauss/gaussdb/highgo/gbase8c）不因本格改判。
- `DriverLoading/BuiltinDrivers.cs + kingbase 行`：兼容 Npgsql 的现场可把 DLL 按 Npgsql.dll 摆进 drivers/（独立上下文按文件名装载），provider 代码不用动；§13.5 整行未实测口径。
- `DriverLoading/BuiltinDrivers.cs + opengauss 行`：v0.36 第二笔；fork 来路记 §9.1 第四列；从未解析成功、整行未实测、无本机实例（重开条件已留：现场装上先按厂商读数改回自己的程序集名）。
- `DriverLoading/BuiltinDrivers.cs + gaussdb 行`：v0.36 第三笔；"厂商分发的是 Npgsql 的 fork"只写在 §9.1 的 openGauss 半边，本行不替它引申。
- `DriverLoading/BuiltinDrivers.cs + highgo 行`：v0.36 第四笔；来路是 §9.1 行 907 第四列「兼容 PG」；PG 血统凑齐三颗键同一份 assembly，来路两种（借兼容臂 2 ∶ 直称 primary 1）的差别不抹平。
- `DriverLoading/BuiltinDrivers.cs + gbase8c 行`：v0.36 第六笔；与瀚高共享同一来路原词（两行第四列逐字相同）；PG 血统凑齐四颗键，来路 2∶2（借臂 ∶ 直称），"凑齐四颗"不等于"四颗同质"。
- `DriverLoading/BuiltinDrivers.cs + oracle 行`：v0.37 第三笔；来路是全表第四种、最薄的一种——本仓库自家注释（OracleProviderBase 类注释「本程序集编译期不认识 Oracle.ManagedDataAccess」），现场不能当厂商读数用；Oracle 血统三颗键凑齐三份互不相同的 assembly（本行 ∶ DmProvider ∶ Oscar），同族之内驱动按厂商各发一份在本组完整成立、在 PG 组完全不成立。
- `DriverLoading/BuiltinDrivers.cs + dameng 行`：§13.5 整行未实测；NuGet DmProvider 2.0.0 的 NU1701 死路（已留）。
- `DriverLoading/BuiltinDrivers.cs + shentong 行`：v0.36 第五笔；来路是 §9.1 行 902 第四列（全文就一个词，厂商文档口径而非现场读数）。
- `DriverLoading/BuiltinDrivers.cs + mysql 行`：v0.37 第九笔改判——已解析成功（交回的事务对象是 MySqlConnector.MySqlTransaction，实测·T-01，obj/logs/v37-my-recon-3.log）并跑完整本集成册（QUICKSQL_TEST_PROVIDER=mysql，100 红 ∶ 14 skip，obj/logs/v37-my-int-3.log）；全表第一颗靠"扫导出类型"兜底真跑通的键（达梦/金仓立这条纪律时没有任何一家走过它）；现场读数 MySQL Community Server 8.4.8（实测·S-01∶S-02）；tidb/oceanbase 两颗同 assembly 的键不因本格改判。
- `DriverLoading/BuiltinDrivers.cs + tidb 行`：v0.35 第三笔；全表第一次出现「两颗键、同一份 assembly」；§9.1 TiDB 行；mysql 行的实测推不到本行（迁移性只证到 mysql 自己那一台）。
- `DriverLoading/BuiltinDrivers.cs + oceanbase 行`：v0.35 第四笔；「三颗键、同一份 assembly」的第三个现场；比 tidb 多一层悬空——双模、模式在连接上决定（§9.1），Oracle 模该走另一颗今天不存在的键（缺口记 §14，与金仓兼容模式同一行）；mysql 行实测是 MySQL 官方 8.4.8 上的读数，推不到这一家自己的 MySQL 模实现。
- `DriverLoading/BuiltinDrivers.cs + xugu 行`：v0.32 改判与厂商 4.0.0.8 改判完整账——FullName = XuguClient, Version=1.0.0.0；4.0.0.8 起 AssemblyVersion 跟厂商版本号走（实测 FullName = XuguClient, Version=4.0.0.8；探测按程序集名命中，不受影响）；厂商参考页写 XGProviderFactory.Instance，实测是 public static 字段且就是工厂本身（兜底扫描 FACTORIES=1）；六格摆法五格走到"打开+只读查询成功"（obj/logs/v32-matrix-*-open.log，gitignored）；重跑姿势：先摆 XUGU_HOST/PORT/DB/USER/PASS 五颗环境变量（口令只走环境，脚本与日志都不落它），再 powershell -NoProfile -File obj/probe-xugu-shipped/run.ps1 -Case probing -Open -Log obj/logs/v32-matrix-probing-open.log；§13.5 台账这一行仍是「未实测」且不许被改判翻掉——解析与连通已见证，本程序集产出的文本一条都没在那台库上跑过；旧失败面（要原生同伴 xugusql.dll / libxugusql.so，缺原生时 TryEnsureLoaded 照样返回 true、到 Open 才抛 DllNotFoundException 0x8007007E——v0.32 bare 那颗反证）在 4.0.0.8 上不存在：纯托管一颗，drivers/ 只摆 XuguClient.dll 时 Open 实测成功，那句反证从此读作 3.3.4.5 的事实；显式 path 声明入口已随清单层删除，v0.32 那颗正斜杠归一化坑随之消失（探测名由代码拼接，分隔符不经过任何人的手）。
- `DriverLoading/BuiltinDrivers.cs + gbase8s 行`：v0.37 第一笔；Informix 血统第一颗键，全表唯一「程序集名只有一处出处、而那处不是文档」（§9.1 行 906 第四列；第六列写这一族「独立」，Informix 官方托管驱动叫什么同样没有读数）；连一颗候选工厂类名都没有，兜底比别行更无从判断；现场装上那天，改名字之前还有一步前置：先证那颗等价关系成立到什么程度（缺口记 §14）。
- `DriverLoading/BuiltinDrivers.cs + sqlite 行`：v0.37 第四笔集成册绿过一次（QUICKSQL_TEST_PROVIDER=sqlite，95 红 ∶ 14 跳，obj/logs/v37-sqlite-int-4.log），跑出去的是 QueryEngine 与 InsertEngine 自己拼的语句；引擎 sqlite_version() = 3.46.1（四颗读数记 §13.5 第十五台账）；CodeFirst 的产物在本仓库没有执行路径，所以不写「每一格都实测过」，逐位档位记台账。
- `DriverLoading/BuiltinDrivers.cs + duckdb 行`：provider 侧 CanCreateDatabase=false，血统的 EnsureDatabaseExistsAsync 在本家整格无害可走；NuGet 包按原生伴件分 Full 档。
- `DriverLoading/BuiltinDrivers.cs + clickhouse 行`：官方一手 ADO.NET 驱动页 https://clickhouse.com/docs/integrations/language-clients/dotnet/ 。
- `DriverLoading/DriverRegistry.cs + DriverRegistry`：曾经的 QuickSqlDrivers.json 覆盖层已整层删除（历史账，与 BuiltinDrivers 类同一条）；§12.7、§9.5 出处。

---

# QuickSql 注意事项归档（part-d）

> 2026-11-03 注释精简（简短 `///`）第二轮：从 `src/QuickSql.Generators`、`src/QuickSql.Providers.SqlServer`、`src/QuickSql.Providers.Sqlite` 三个项目的代码注释里摘出的坑位、边界、已知限制、实测读数与历史账。锚格式为 `文件相对路径 · 成员名`。

### QuickSql.Generators

- `EntityContractGenerator.cs · XmlDocCommentsOption`：开这一档必须另写 `<CompilerVisibleProperty Include="QuickSqlXmlDocComments" />`——SDK 只把 `@(CompilerVisibleProperty)` 里点名的属性折进 `build_property.` 那份编辑器配置，光设属性生成器读不到（编译器的口径，不是生成器的兜底）；键名被测试抄成字符串后改这一处即静默假绿。
- `EntityContractGenerator.cs · Read`：开关只有字面 `true` 算开，缺键/空串/`1`/`yes` 一律当关——这一档会把源码开发期注释搬进库备注，误开代价比误关大。
- `EntityContractGenerator.cs · QuickSqlDiagnostics`：诊断 `messageFormat` 恒为 `{0}`、话术由 EntityFactsBuilder 现拼，与反射侧 `MetadataConflictException` / `EntityContractFactory`（MapToColumns、MapSkipIndexes、默认值值域表）对应句逐字对齐——两处各写一份模板就谈不上一致；QuickSql003（NotGenerated）不是错，G2 接得住、只是没有"编译期零反射"待遇。
- `EntityFactsBuilder.cs · Build`：G1 的公共无参构造门槛——非公共无参构造在生成侧编不出字面 `new T()`（G2 反射侧认非公共，见 MemberAccessors.ParameterlessConstructor），挡的是"少一份编译期事实册"不是"实体不能物化"。
- `EntityFactsBuilder.cs · Build`：`xmlDocComments` 是整条链唯一 G1 独有来源，开着它同一张表在两条启动路径上给出两个备注（对拍 `comment=` 格会红）——默认关是为了让"开它"是一次签字。
- `EntityFactsBuilder.cs · ColumnOf`：StringLength 与 MaxLength 同层互斥，取严格者会藏"建表按 20、校验按 50"数月的 bug，G1 侧在编译期同样拒绝。
- `EntityFactsBuilder.cs · ColumnOf`：没打并发标记就不写 ConcurrencyNote（G2 侧同一格是 null，对拍比整个 ColumnFacts，多写一句常量就是"两侧各自合理但文本不一样"）；`ConcurrencyStamp` 名字约定是 ABP 那一族的现成拼写。
- `EntityFactsBuilder.cs · DirectArmOf`：枚举不在直读臂名单（成员型是 `State` 而驱动回底层整数，探测永远不落编号）；`byte[]`、`DateOnly`、`TimeOnly`、`DateTimeOffset`、`TimeSpan`、`sbyte`、无符号整族也不在——DbDataReader 对这几型没有"一次调用一个值"的原始访问器（`GetTimeSpan` 根本不存在，构建期撞出来的）。
- `EntityFactsBuilder.cs · DirectArmOf`：可空值类型换 `ReadNullable` 臂——非空臂返回底层型、NULL 由门给 default(T)，交给 `DateTime?` 成员就是静默摊平（0001-01-01 冒充"没有值"）；字符串不换（`string?` 与 `string` 是同一运行期类型）。
- `EntityFactsBuilder.cs · NullableOf`：编译期 `NullableAnnotation.None` 对齐反射侧 NullabilityInfoContext 的 WriteState=Unknown；可空上下文之外（`#nullable disable`、没带可空元数据的引用程序集——整份 ABP 框架代码天天在这一格）落 false，只摘必填尺子不改列形状。
- `EntityFactsBuilder.cs · SummaryOf`：实测（2026-10-08 端到端）——csc 不带 `/doc` 时 DocumentationMode=None，同一行 `///` 是普通 SingleLineCommentTrivia（原文带 `///`）；IDE 与内存量具为 Parse（SingleLineDocumentationCommentTrivia，`ToString()` 交回结构文本、`///` 已不在）。只按 kind 筛 → None 档读不到（真实构建就是这一档：量具全绿而字段为空）；只按前缀筛 → Parse 档读不到。两把并起来 IDE 与构建才算得出同一份元数据。不要求 GenerateDocumentationFile（那颗只管 .xml 边车，给运行期反射读）。
- `EntityFactsBuilder.cs · SummaryOf`：只截 `<summary>…</summary>` 段解析而不拼整棵树——一行 `//// 分节线` 里出现裸 `<` 会把整棵树的解析连旁边写得好好的 summary 一起判死；畸形交回 null 不抛（作者写裸 `&` 时编译器已有 CS1570，再说一遍是把警告升成构建失败）。区间运算符用不了：生成器吐 netstandard2.0，那套引用里没有 System.Index/System.Range（CS0518，不是缺 using）。
- `EntityFactsBuilder.cs · Fold`：全空 `<summary></summary>` 与只有 `<inheritdoc/>` 而没有 summary 的都交回 null = 没声明，而不是空串（"说过而说的是没有备注"是另一档，见 TableInfo.Comment）。
- `EntityFactsBuilder.cs · RelationshipsOf`：约定档（导航名 + "Id"）命不中就没有这条关系、不是错误——那正是 v0.19 之前两侧共同的行为（历史账）。
- `EntityFactsBuilder.cs · IndexesOf`：索引名约定与反射侧同句——表名没模板用表名、分表（表名是 `{year}` 样模板）用类名；对拍连错误话术一起管。
- `EntityFactsBuilder.cs · EnumExpression`：枚举档名存"可直接落码的表达式"——`(SplitKind)99` 这类值不在生成期咽掉，让它在运行期撞 SplitRouting 那句"未知的分片档"，两侧一致性由此而来而不是靠生成器再校一遍。
- `EntityModel.cs · DirectArm`：臂名字写错是编译错误；挑错臂（如给 `short` 列挑了 `ReadInt32`）则两侧编号对不上、静默退回装箱口——能错的方向是"没变快"，不是"读错值"（§16.21）。
- `EntityFactsWriter.cs · RenderFacts`：分表/集群/表备注三颗参数没声明也要把 null 写出来——"缺省写 null"与"压根没抄"是两个形状，反射侧交缺省值，两侧到 EntityContractFactory 之前形状一致（§13.11 逐字段相等才比得到）。SkipIndexFacts 的缺省格同理不写。
- `EntityFactsWriter.cs · RenderLookup`：取数口展开成一串引用相等比较而不是字典——§12.8 的"G1 路径 0 字典查找"，实体再多也该先量过再换。
- `EntityFactsWriter.cs · DefaultLiteral`：带后缀型（decimal/float/double/ulong）把后缀写死，否则生成的代码在整数字面上类型悄悄漂移（`12.5` 是 double 不是 decimal）。
- `EntityFactsWriter.cs · RenderSchema`：生成文件里 `from`/`ops` 局部变量——序号是"起点 + 列位"一个加法（票上只有起点与档位），整表无臂时不写 ops 局部。
- `ProviderRegistrationGenerator.cs · 类注`：三条边界——provider 叶子自己不产这段生成（注册发生在组装根）；没引 provider 或未挂生成器时零产出、ProviderRegistry 空表，QuickSqlOrm.Create 抛"未挂生成器或没引用 provider"；同一颗 DbKind 多来源按 (Kind, 全名) 稳定排序、后写为准。
- `ProviderRegistrationGenerator.cs · Collect`：同 Kind 撞车按全名字典序保留"最后一个"、本版不发诊断——将来若真需要"两份 provider 抢同一颗 Kind"应响，再补 QuickSql005（重开条件）。
- `ProviderRegistrationGenerator.cs · Collect`：缺 `public const string DriverKey` 就整颗不登记——QuickSqlOrm.Create 走内建表解析时抛"该 Kind 无登记"更清晰。
- `ProviderRegistrationGenerator.cs · Collect`：四个类型（attribute/dbKind/idbProvider/factory）缺一即零产出，真正的守门是"只引了 provider 而没把生成器挂作 Analyzer"。

### QuickSql.Providers.SqlServer

- `SqlServerBulk.cs · 类注`：历史账——`ResolveCopyChannel` 是"本版补的接线"：能力位 Bulk_Copy 从第一天报 true 而 SPI 上根本没有通道可取，是"规格里写了设计、今天没接线"的本家样本；今天报位与交通道是同一份实现。
- `SqlServerBulk.cs · 构造函数`：诊断跟着"调用出现在哪个方法里"走——反射取通道写在构造函数里就得给所有构造者交税（只产 SQL 的离线测试也构造 provider）。
- `SqlServerBulk.cs · SupportsMergedStatements`：实测读数——200 行整行批与逐条批的耗时差复跑四趟 193∶137、277∶151、322∶174、250∶174（毫秒），方向稳而反（并档省往返、添解析，本地实例往返太便宜）；消费者四句 UPDATE 并一条命令落库成功、逐列读回一致。见证：集成册 BulkWritePathTests、`obj/logs/v40-sqlserver-bulkwrite-2.log`、`v40-ss-write-repeat-{1,2}.log` ∶ `v40-sqlserver-integration-fix1.log`；按 §13.4 只记账不钉成断言。与 MySQL 那一支读数正对，两家的表态都没因此改。
- `SqlServerBulkCopy.cs · ConnectionScopedCtor`：那颗"收连接、开关、外部事务"的构造子按形状找——真库 7.x 上量到四条公开构造子里只有这一颗三位；写死 typeof(DbConnection) 会当场点空、整家退 L3，且与"根本没有装载类"失败形状完全同形。
- `SqlServerBulkCopy.cs · NetType`：缺席三类明细——char（值门给 DbType.String 加 Size=1，与 DataTable 的 System.Char 不是同一说法）；DateOnly/TimeOnly（DataTable 类型面里没有，要进通道得先文本化成 DateTime、那是替库改类型）；无符号族与 object（T-SQL 侧没有对应存储类型）。枚举按底层整数判（值门已折成整数，拿 ClrType 原样判会把每条带枚举的批量白退一次）。
- `SqlServerChangeGuard.cs · 类注`：IS DISTINCT FROM 对照——MySQL 血统 8.0.29 起才有且要按版本发而 SPI 没有"库的版本"格（MySqlChangeGuard 仍取三段）；§12.4 原文近似式 `(a IS NULL) <> (s.a IS NULL)` 在 T-SQL 连语法都过不了（布尔表达式不是一等值，真库用例钉住了报错）。三段形状 v0.23 提出去 ChangeGuardShapes——第三家撞上同一条限制，"提出去"是那一版的结论（一个方言族接出来之后多数派换边）。
- `SqlServerChangeGuard.cs · Incomparable`：rowversion 名义上是 binary(8)，`WHERE [Version] = @p` 在两个授权实例上被 v0.18 的并发用例跑通过——算成不可比会让带并发列的表整批退回逐条（历史账）。
- `SqlServerCodeFirst.cs · 类注`：阶段账——P0 只产出文本与黄金样本比对、不执行（CodeFirst 在 P2）。
- `SqlServerCodeFirst.cs · EmitColumnDefinition`：§10.6 必填尺子量的是整只实体，"新增时手里没有值"在实体侧只能声明成可空；§14 那一格 v0.24 收（历史账）。
- `SqlServerCodeFirst.cs · EmitDefault`：2026-11-01 那一格的 T-SQL 半边（日期账；BIT 无布尔字面量落 1/0）。
- `SqlServerDialect.cs · DriverAcceptsPrefixedParameterNames`：v0.34 第二笔；带前缀参数名在本家绿过两段历史——v0.19–v0.31 原样递 `@p0`（两台实例 95 条全绿）、v0.32 摘前缀之后也全绿；§12.8 第 6/11/12 行那五枚钉的量就是这笔账（历史账）。
- `SqlServerDialect.cs · Emit(upsert)`：HOLDLOCK 档位口径【文档·Microsoft MERGE 语句】——是否真挡住"两条并发 MERGE 交错"那一竞态要在一台活 SQL Server 上并发跑一次才算结清（§14、集成册补一场；重开条件）。
- `SqlServerDialect.cs · Emit(upsert) // 注`：544 报错是集成册 QUICKSQL_TEST_CONNECTION 第一跑红在此（见证账）；SQL Server 收 MultiStatement，IDENTITY_INSERT 开关对与 MERGE 同一条命令递交、同一个会话。
- `SqlServerProvider.cs · 类注`：历史账——SqlServer 走反射加载路径是为了让"驱动反射加载"在 P0 第一次真跑起来，而不是等到只有私有源驱动的 P3 才首次运行（§14 代价表点名的坑）。
- `SqlServerProvider.cs · ClassifyIntegrityError`：2026-10-23 本机实测（集成目标实例撞一次唯一索引）；`2601` 实测、`2627` 是厂商文档号但已被集成册"插同一个主键第二行"用例转成实测、`515`（非空）仍未在本机撞过；547 等愿意按文案分档的时机再说（重开条件）；§8.4 那条"通用层读 SqlState 就通吃"待拍举的就是这一家。
- `SqlServerProvider.cs · Catalog`：判据与症状记在 ConnectionStringCatalog，MySQL 血统那一格炸过（历史账）；裸 DbConnectionStringBuilder 实测只按字面键比、仅忽略大小写，候选表少列一颗就静默少读一颗。
- `SqlServerTypes.cs · ApplyStorageDefaults`：遗留同义词 `timestamp` 刻意不列——建表时确实等价于 rowversion，但契约里那一格的名字归两侧（列注解写 timestamp、sys.columns 也报 timestamp），写进来就是一格没有对拍的猜测（触发条件见 §14 末尾；边界/重开条件）。
- `SqlServerValues.cs · InlineNumber`：历史账——v0.22 写了两次才发现，原稿方向写反（对指数式也补）；`SELECT 1E-08` 是合法 float 常量（回读 1E-08），补成 `1E-08.0` 报「.0 附近有语法错误」。
- `SqlServerSchemaReader.cs · 类注`：历史账——`sys.extended_properties` 与 `sys.indexes` 从前根本没人查，于是"改了表备注/加了索引"在结构同步那一头读不回来、判不出差别。
- `SqlServerSchemaReader.cs · ProbeLengthSemanticsAsync`：cp1252 下中文塌成 '?' 是静默损坏，那一条由 UnsafeLengthSemanticsException 在启动期拦（边界）。

### QuickSql.Providers.Sqlite

- `SqliteChangeGuard.cs · 类注`：本家有 IS DISTINCT FROM 且三处量过——投影【D1】、`UPDATE … WHERE`【2112】、大对象上【3105 那行第五列，`"B" IS DISTINCT FROM @b` 在 BLOB 列上判得对】；与虚谷（三形正问各交语法错）恰好两侧。版本门槛：算子是引擎 3.39 才有的，而 DbCapabilities 没有"库的版本"格——引擎内嵌、版本由应用侧包图决定，问入口只有探针里的 `SELECT sqlite_version()`【A1 交回 3.46.1】。
- `SqliteChangeGuard.cs · 类注`：三段式四格逐颗实测（本家第一次数）——参数与原值同：有值行 0、NULL 行 1【3101】；参数换不同文本：两行 1【3102】；参数给 NULL：有值行 1、NULL 行交回 `<dbnull>` 而不是 0【3103 ∶ 3204】——这是三段式与句式唯一语义差（NULL<>NULL 是 UNKNOWN、`NULL IS DISTINCT FROM NULL` 是 0【3203】），在 UPDATE…WHERE 位 UNKNOWN 与 0 同效、不构成缺陷，只有当投影值取出来才显形；整段当 WHERE 命中数 2 与"只有那一行"两形【3109 ∶ 3205 ∶ 3206】。
- `SqliteChangeGuard.cs · Compares`：BLOB 大对象读数——`= ∶ <> ∶ IS ∶ IS DISTINCT FROM` 四形（非空行 1∶0∶1∶0，NULL 行前两形 UNKNOWN、后两形 0∶1）【3105】，三段式在 BLOB 上同样数得出【3106】；量是当场写读的 200,000 字节 ∶ 1,000,000 字符【3108a/b 写入、3108c 回读 `'blob',200000,'text',1000000`、3108d/e 各自 =1 ∶ <>0】；`VARCHAR(n)` 落同族文本比较、宽度不参与存储【4001 ∶ 4002】。
- `SqliteChangeGuard.cs · 类注`：可达性——本家是七家唯一"闸门与守卫不同向"：SupportsJoinedWrites 同样 false 而守卫恒真，没有"false 但走不到"那层缓冲（MySql/Xugu 两家由那道闸代跑过 Compares=false）。
- `SqliteChangeGuard.cs · 类注`：钱列读数——库里 `'12.3400'` 而新值 `12.34m` → 文本 `'12.34'` → `<>` 成立【3201 第一列 0 ∶ 3205 命中】，守卫判"变了"多发一次写（空写方向：与虚谷"存入去尾空格"同向、与 MySQL 血统 CI 排序规则反向——那一个是丢写）；本家没有丢写面（不变形文本相同则数值必相同，蕴含由 SqliteValues.Normalize 的绑定形状给）。CI 风险为量出来的 0：`'Abc'='abc'` 得 0【97】、`'Abc' COLLATE NOCASE ='abc'` 得 1、`'ä'='Ä' COLLATE NOCASE` 仍 0【2239 三列 1∶0∶0】（NOCASE 只到 ASCII；NOCASE 要手写进列声明、provider 不翻译不校验）。反方向读数：LIKE 默认不分大小写（`'A' LIKE 'a'` 得 1、`'A' GLOB 'a'` 得 0【96】）——"守卫说没变而 WHERE…LIKE… 说变了"可同时成立（两半两套比较规则）。
- `SqliteChangeGuard.cs · 类注`：本家没有内置哈希函数——三十多轮没有一颗 `*hash*` 被问到过，`UUID()` 当场报 no such function【2134】；`<>` 在 200KB 量上比得动，没有被库逼出"必须换算法"的档位。
- `SqliteConcurrency.cs · 类注`：rowversion 缺档的证法——虚谷是被正面推翻的（库里建得成 ROWVERSION、真自增、驱动读不回——XuguConcurrency 的 RV-/A- 编号，v0.32 第三笔【实测否定】）；本家是"问的对象不存在"：七颗型名一次建表目录原样回读【B1/C2】、值形由 CLR 值型决定而驱动不看 DbType 与 Size【500/500b/501】、AUTOINCREMENT 只作用 rowid【2802 ∶ 2810】、触发器那一路本仓库没有形状（四十二轮一条没量过）。写 `[Column(TypeName="rowversion")]` 得到一列普通列、诚实不递增；"看起来在用并发列其实没有"归 §14"人写的型名 provider 不校验"通例。
- `SqliteConcurrency.cs · 类注`：比较式两态读数——`affrows=1 ∶ 0`【3302 ∶ 3303】、整型版本列同【3308 ∶ 3309】；版本列 NULL：`"T"=@p` 恒不命中【3306】而 `"T" IS NULL` 命中【3307】——不折的话那一行每次保存都成假冲突，冲突的处置路径是回滚与重读、不是丢写；并发谓词与 RETURNING 叠一句走得通【3310】；"回填不改变计数"另有读数【340/341】。
- `SqliteConcurrency.cs · 类注`：时间戳是文本——库里那一格是自己写的参数打得中【3304】（顺带量出驱动无尾数时不写小数点）、别的写入方留的打不中【3305】；`DateTime` 驱动写 ISO 文本、100 纳秒保得住【318/319】；`Stored()` 规整器只管自己发出去的文本、管不到库里已有行。假冲突方向（每次保存都撞、永不成功写入），与 MySqlChangeGuard CI 反向、与 XuguChangeGuard 去尾空格同边。重开触发：任何"与非本仓库写入方共享一张表"的场景第一天就会撞（§14）。
- `SqliteConcurrency.cs · 类注`：没有行级锁——`SELECT … FOR UPDATE` 是语法错【第 2 轮 103 ∶ 2164 同一句 near "UPDATE": syntax error】；现场读数 `journal_mode = delete`、`busy_timeout = 0`【第 2 轮 118/118b】——"冲突"的真实形状是两个写者撞同一颗文件时立刻抛错而不是排队。SQLITE_BUSY（两连接对撞）本次没量过，退避策略配在哪一层（连接串 Default Timeout ∶ PRAGMA busy_timeout ∶ 生产走脚本）整格记 §14、任务 #21 两连接对撞（重开条件）。
- `SqliteConnectorFactory.cs · TryEnsureLoaded`：两层分岔读数——`CreateConnection()` 交回 `Microsoft.Data.Sqlite.SqliteConnection`【A0b】（只证托管半边）、`SELECT sqlite_version()` 交回 3.46.1【A1】（原生同伴到位的见证）。虚谷反例：托管装上而原生不在路径时照样 true，缺失到 Open 才以 DllNotFoundException 现身（XuguConnectorFactory，出处 `obj/logs/v32-matrix-bare-open.log`；那颗驱动 3.3.4.5，交付的 4.0.0.8 纯托管）；本家原生同伴 `e_sqlite3` 随同一 NuGet 图落下、装不上是整颗装不上——"挡得住托管挡不住原生"在本家没有可量的形状。
- `SqliteConnectorFactory.cs · CreateCommand`：`SqliteFactory.Instance.CreateCommand()` 当场交回 SqliteCommand（A0b 后半）——"只走连接"在本家不是被库逼出来的；`DbConnection.DbProviderFactory` 是 protected（编译期语言事实、不是运行时读数，不记 A0b 名下），驱动只能由外部注入。
- `SqliteConnectorFactory.cs · 类注`：历史账——第一轮连接串 `Data Source=<目录>/db/probe.sqlite`；"一条串指向一个文件"的正面读数在第 30 轮：ATTACH 第二条路就多一颗模式【3000/3001】；探针脚本留档 `obj/sqlite-probe2/Program*.cs.txt`（二十一份）∶ `obj/sqlite-probe/Program1.cs.txt` ∶ `Program5.cs.txt`。
- `SqliteKeys.cs · 类注`：四宿主读数——INSERT【D3/403】、UPDATE【第 2 轮 105 ∶ 404 ∶ 1319】、DELETE【第 2 轮 106】、UPSERT … DO UPDATE【130/421】、多列【K/124/421】、多行【1320/1011/1013】——本仓库第一份四宿主全在活库逐条问过的实现（另六家有两颗拿的是厂商文档）。
- `SqliteKeys.cs · 类注`："回填不改变计数"读数【340/341】——若 RETURNING 把 ExecuteNonQuery 计数变成"回传的行数"，ExecuteAffrowsAsync 那一族调用全体错位（另六家默认成立而没人问）。
- `SqliteKeys.cs · 类注`：自增读数——NULL 进自增列由库补号【210/211】；显式值后序列挪到它上面（100 写进下一号 101【54 ∶ 55】、31 亿写进下一号 3000000001【310/310b】）——Gbase8sKeys 那颗"连会不会撞都答不出"（§14）在本家有答案"不会撞"；T-SQL 要 SET IDENTITY_INSERT ON、本家任何时候都允许（IdentityInsertToggle 返回 null 与另五家同路不同因：那五家是"没读到这能力"、本家是"不需要开关"）。`sqlite_sequence` 表名字段存不带所有者限定的裸名【56 ∶ 3810 name 列】，这张表第一次自增插入才存在【D6 ∶ 56】——"读下一号"要跨两张表而 SPI 没有那一格（§14 与 §9.1）。CREATE SEQUENCE 不存在（SqliteDialect 类注释）。
- `SqliteKeys.cs · 类注`：last_insert_rowid() 是连接级的（跨连接问不到【D5】）、一条多行 INSERT 只回最后一行的号【212/1018】；"同一条命令文本放两句 SQL"实测过【E3b/152/213/226/426】。SupportsPostStatementKeyRead=false 是"做得到但没有入口"（Core 只在批量位 false 时才问这颗开关，与 PostgreSqlKeys 同一读法、第七家第二次用）。IdentityInsertToggle 刻意不是 virtual（Core 今天没有调用点，六家已把它记成 §14 一格）。
- `SqliteDialect.cs · 类注`：分页哨兵三派——MySQL 血统写 18446744073709551615（厂商上限）、虚谷写 2147483647（自型写死）、本家写 -1（"不设上限"记号不是数量）：单独出现就收【832】、`LIMIT -1 OFFSET 2` 交回空集【32】、带 OFFSET 与大偏移都成立【315/315b/408】；本家不许光写 OFFSET【A4/31/317】而 PG 血统合法（PostgreSqlDialect 靠这一格省掉哨兵）——"同一套文本形状里两家实现不通用"第一例，§9.4 按套数记的账要读成"套数 ∶ 每套里的实现数"。无 ORDER BY 的 LIMIT 直接通过【34】。
- `SqliteDialect.cs · 类注`：占位符——四种记号 `@ ∶ : ∶ $ ∶ #` 同一条语句各绑定成功【第 2 轮 113】（驱动与引擎都不挑前缀）；取 '@' 因共用层 SqlLogging.ToLogSql 按"前缀 + 字母"找占位符、同形少特判；匿名 `?` 驱动直接拒【225】而引擎自己认 `?1`【N】——"引擎支持而驱动不支持"本仓库第一次有实测；换一颗直接绑 `sqlite3_bind_*` 的驱动，前缀取向就不是唯一答案（§14 与 BuiltinDrivers 本家注）。
- `SqliteDialect.cs · 类注`：三格零覆写且基类默认第一次被正面量过——恒假/恒真谓词【2110/2111】；布尔 `= 1` 成立【H0/335/607/831】且 `= 1` 与 `IS TRUE` 两形都合法同判（PG 血统只有后一支、其余只有前一支；"位只跟存储型走"第一次由实测给理由）；写语句目标别名两半都能带【2100 UPDATE ∶ 2104 DELETE ∶ 2101 别名与表名同名】——T-SQL 为它重写整条语句，另四族反而关掉。
- `SqliteDialect.cs · 类注`：SupportsJoinedWrites 两半各缺——UPDATE … FROM 在【1311-1314/2108】（普通表源、命名子查询、UNION、裸 `(VALUES…)` 都能当源【2109】，隐式列名 column1..N【1200/1207】）；DELETE … FROM 与 USING 都不在【1317/1318 两形各一次语法错】；真挡点是列名清单 `AS t(cols)` 五形五拒【1201-1205，2107 是 UPDATE 侧第六次】；异常消息里两条等价路都有实测（标量子查询【2108】、WHERE EXISTS【2106】）——GBase 8s 那份两条只有【文档】。
- `SqliteDialect.cs · 类注`：DEFAULT 关键字——`INSERT … VALUES (DEFAULT, DEFAULT)` 是语法错【2158】（DialectBase.cs:173 那一支七个方言族今天都走不到，InsertEngine.RowsOf 每格参数化）；重开触发写具体：§16 的 IgnoreColumns 或任何"部分列插入"落地时，本家要说的是"多行 VALUES 里无法表达列默认值"，单行退路 `INSERT … DEFAULT VALUES` 接不住逐列选择；"绑 NULL 代替 DEFAULT"看着能用而语义不同——`NOT NULL DEFAULT 'd'` 列上差一整个约束【2145 能用 ∶ 348 绑 NULL 报 NOT NULL constraint failed】，只有自增档恰好同解【210/211】。
- `SqliteDialect.cs · 类注`：N'…' 在这一族是语法错【P】（PrefixUnicodeLiterals=false 的最硬来由）；引擎版本门槛——RETURNING 3.35+、IS DISTINCT FROM 3.39+、DROP COLUMN 3.35+（现场 3.46.1 跨过【A1】）；STRICT 表【2153/2154】与生成列【2155/2156】不发（前者会把"文本存定点"写法直接拒掉，后者契约没有对应格）。
- `SqliteDialect.cs · 类注`：探针编号口径——本文件读数量的是"SQLite 3.46.1 ∶ Microsoft.Data.Sqlite 9.0.0"这一对（A0/A1/H1 现场读出），不是"这一族的所有部署"；探针量的是手写语句，provider 产出的文本（除集成册那一场）还没发出去跑过。
- `SqliteDialect.cs · PrepareForEmission`：v0.37 排查读数——SQLite 存储类排序 INTEGER < TEXT 让 `1700 > '1000'` 得 0（历史账；修复即 CAST(@p AS NUMERIC) 包 decimal 比较参数）。
- `SqliteDialect.cs · Emit(upsert)`：ON CONFLICT 四臂各量过【第 2 轮 40∶41∶42∶43∶46】（更新、不动、无目标、表达式目标报错）；句尾槽取不到的原因是 Bulk=null、槽长在 IBulkStrategy 上；与 PostgreSqlBulk.UpsertClause 逐字同形而两家各写一份（provider 禁止互引）；将来若把句尾从策略槽挪进方言层，两家能并成一条出路（§14）。档位口径：InsertOrUpdateEngine 经这一颗发出的那一条具体语句还没在活 SQLite 上跑过（task #11、集成册补一场才算结清；重开条件）。
- `SqliteProvider.cs · DriverKey`：factoryType 实名是现场读到的（A0 系列读数）；按"名字读过而内建表不写"本家排第三——sqlserver 从第一天写实名、虚谷在 v0.32 结算日也读到过 XuguClient.XGProviderFactory 而内建表行照旧省着，本行第一次把现场实名写进内建表（历史账）。
- `SqliteProvider.cs · 类注`：本家是七家第一份"每位能力位背后站活库读数"的 census（另六家只有虚谷翻过 Live 四位且靠人逐句敲）；逐位读数——Paging_LimitOffset【A3 ∶ 32 ∶ 315/315b/408】、Returning【第 2 轮 105 ∶ 106 ∶ 404 ∶ 1319 ∶ 1013】、Identity_ExplicitInsert【54 ∶ 55 ∶ 310/310b】、Schemas（建表 main./side.【3001】、ALTER/DROP 限定【1308 ∶ 1310】、索引限定落对的库【3005 ∶ 3006】、读侧 pragma_table_info 两档【3705 ∶ 3706】）、MultiStatement（三句一串收、尾句不带分号也执行【2820 ∶ 2822 ∶ 2912/2913 ∶ 2908/2910】）。
- `SqliteProvider.cs · 类注`：MultiStatement 是七家第二份声明（第一份 SqlServerProvider.cs:43）而本家是第一份背后站着活库读数的；OracleCodeFirst.cs:26-28 记着五家 CodeFirst 全都产出 `;` 串而只有 T-SQL 声明——另几家不声明的理由：PG/Oracle 扩展协议下不合法（PostgreSqlProviderBase.cs:21 ∶ OracleProviderBase.cs:52）、MySQL 要一颗连接串参数（MySqlProviderBase.cs:72-73）、虚谷没有任何产物演示（XuguProvider.cs:84）、GBase 8s 指键回读接在建表句后（Gbase8sProvider.cs:76）；"建新表→拷数据→删旧→改名→重建索引"换型序列一条文本走通【4310 ∶ 4312 ∶ 4315，读回 4313 ∶ 4314】。
- `SqliteProvider.cs · 类注`：交集账——本家交出后交集第一次只剩一颗（上一版 Schemas ∶ Alter_Column_Precision，Gbase8sProvider.cs:56）；`CREATE SCHEMA` 发不出【2824】而 Schemas 仍声明：那颗位量的是"限定名发得出而库认得它"，且它在两家手里指的不是同一样东西（GBase 是所有者、本家是挂进来的文件）——"交集里还有这一位"不等于"跨族建表迁移可以按这一位发"。
- `SqliteProvider.cs · 类注`：不声明清单的库侧读数——Boolean_Type（INTEGER affinity 下 BOOLEAN 与 INTEGER 同一颗存储【610】，谓词侧交回 0/1【第 2 轮 104 ∶ 1112】）；Guid_Raw16/Char36（Guid 落 TEXT 无任何约束：36 字符 GetGuid 读回成立【336 ∶ 337】、绑 byte[16] 进得去读回 Byte[]【V ∶ X】）；Upsert_OnConflict（四臂量过、带 RETURNING 那臂在 130，而 src/ 里 `.OnConflict(` 零处；PG 血统已有 UpsertClause 发射口、本家仍零处，接一家不改其余各家）；Comment_On_Column（COMMENT 语法错【F5 ∶ 2828】，sqlite_master 回显规范化 DDL【3013】）；Alter_Column_Precision（F4 ∶ 2815；宽度标度从不参与存储【4001 ∶ 4002】）；CaseSensitive_Identifiers（带引号写错大小照样解析、"只差大小写的两张表"是同一张【第 2 轮 11/12/16/19/20/18 那颗 table mixedcase already exists】）；Sequences（CREATE SEQUENCE 语法错【第 2 轮 107】）；Cte/WindowFunctions/Count_Over_Window（WITH 与 COUNT(*) OVER()【第 2 轮 102 ∶ 1014 ∶ 836】、ROW_NUMBER() OVER【2144】已有发射口 QuickFx.RowNumber→DialectBase.EmitRowNumber 而位上没有读者）；SkipLocked/Lock_NoWait（SELECT FOR UPDATE 语法错【第 2 轮 103 ∶ 2164】）；Truncate_Cascade（TRUNCATE 语法错【93b ∶ 2161】）；TempTable（CREATE TEMP TABLE 活【第 2 轮 109】，temp 与 main 同名遮蔽已量【3805 ∶ 3909/3910/3911/3912】，src/ 没有发射口）；Json_Type（json_extract 与 json() 可用【第 2 轮 101 ∶ 1015】，Json<T> 在 src/ 0 处）；Array_Type/RefCursor（本轮没设点位，引擎侧答案要看编译开关【推断】）；MultiResultSets（一条文本三颗 SELECT 交三串【4405】；src/ 唯一的 NextResult 在 AdoExecutor.cs:172、容忍多份而从不读第二份）；Bulk_Copy/Bulk_ArrayBind（驱动没有"一次交一批"的公开口，参数预算不是约束——上限是驱动侧常量）。
- `SqliteProvider.cs · 类注`：provider 不报引擎版本门槛——五位里三颗带版本下限（RETURNING/DROP COLUMN 3.35+、IS DISTINCT FROM 3.39+【文档·SQLite 发布页，三颗门槛没从引擎侧读到】）；这一族部署可以差得很远（单文件内嵌、编译开关、是否带 ICU），换一台老引擎文本当场报语法错而 provider 不会预告（比另六家"版本没读"更要紧，因为本家读数全部来自一颗具体引擎版本）。
- `SqliteProvider.cs · 类注`：台账账——编号排到 48 的四十三份留档报告、上千颗点位在 `obj/sqlite-probe*/probe*-report.txt`（第 2 与第 4 轮各自从 100 起编号、中段十六颗同名不同物，引读必须带轮次）；v0.37 第四笔集成册 `QUICKSQL_TEST_PROVIDER=sqlite` 一跑 95∶0 红∶14 跳（`obj/logs/v37-sqlite-int-4.log`），分页/句尾 RETURNING/建表三行翻 Live（发它的是 Database.cs:812 那句 ExecuteNonQuery，见证是 IX_QuickSqlOrder_CreatedAt 在场、全仓库只有这条路径会建它）；仍不成立的两格：带模式限定的 DDL 留 OfflineText（夹具十一张表全建在 main）、Identity_ExplicitInsert 留 VendorDoc（src/ 没有可发文本）；上一版"CodeFirst 的产物没有执行口"作废（逐位档位记在 CapabilityLedgerTests 本家段）。
- `SqliteProvider.cs · ClassifyIntegrityError`：本机实测重复键——SqlState 是 null、Data 是空字典、ErrorCode 是 HRESULT；真正的号（SqliteErrorCode 19、SqliteExtendedErrorCode 2067 = SQLITE_CONSTRAINT_UNIQUE）只挂驱动专有成员而"调用不行"够不着；实测文案原文 `SQLite Error 19: 'UNIQUE constraint failed: 表.列, 表.列'.`；另三句（NOT NULL / FOREIGN KEY / CHECK constraint failed）在本台没撞过、一次只押一句已实测的；好处是这句带列名清单、是四家唯一不需回契约就能报坏在哪几列的形态——§16.22 ②（按约束名回契约查列）动工时本家取法与别家不同条（重开条件）。
- `SqliteProvider.cs · Bulk`：L1 没有装载类形状的 API（高速面是"一条事务包住一批语句"，那是执行编排不是装载通道，§12.1 阶梯里没有那一层）；Merged 档驱动"看起来"收得下分号批，但"看起来收得下"与"一条批被逐句执行、行数按句回报"之间差的是一场没跑过的读数（MySQL 那颗 true 有 postRead 回填支每轮绿背书，本家只有推断）；重开：集成册补一场多语句批当场改判。
- `SqliteCodeFirst.cs · 类注`：自增换的是整条键——内联 `INTEGER PRIMARY KEY AUTOINCREMENT` 建得成【2802 的 notnull=1∶pk=1，目录回显 2803 原文】而同一张表再发表级主键（复合【305】、单列具名【3009】、`CONSTRAINT … PRIMARY KEY(自增列,…)`【2806】）都报 `table "x" has more than one primary key`——语法级互斥；型名必须是 INTEGER：BIGINT 被正面拒【306 ∶ 2807 同句 AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY】；这不是把键弄窄：存储就是 64 位 rowid（30 亿写进原样读回、下一号 3000000001【310/310b】）；取 AUTOINCREMENT 而不取裸 INTEGER PRIMARY KEY 的对照——裸形把删掉的最大号还回去【2809：删 2 后下一号是 2】、AUTOINCREMENT 只增【2810：删 50 后下一号是 51】（另六家序列语义都是"只增"），发裸形会让一句 DELETE 后的 INSERT 复用刚删掉的号；sqlite_sequence 同样只增【56/57】。
- `SqliteCodeFirst.cs · 类注`：索引限定换边——表侧限定四颗独立问到同一句语法错【1301 ∶ 2826 ∶ 2919 ∶ 4604】，索引名侧限定不但能过还落进正确的库【3005 ∶ 3006】；第 46 轮 ATTACH 重跑：`ATTACH DATABASE '…' AS app`【4600 ∶ 4601 pragma_database_list = main(0)∶app(2)】、表只建在 app【4602】、`CREATE INDEX "app"."IX_t46_CreatedAt" ON "t46" (…)` 建得成【4603】、索引落 app【4605 app.sqlite_master ∶ 4606 pragma_index_list('t46','app')】、两半都限定语法错【4604】；不带限定会去 main 找表报 `no such table: main.s30`【3002】；建索引里裸表名解析的是"索引名限定词所指的库"（不是永远的 main），而查询侧裸名也认得【4608】——CREATE INDEX 独有的解析规则；库里存的 DDL 文本把名字上的限定词抹掉了（4605 的 sql 列原文）——sqlite_master.sql 与发射串逐字比时限定形永远对不上，比的是存在性；本家是七家唯一"限定名可发而限定表不许发"的。
- `SqliteCodeFirst.cs · EmitAddColumn`：ADD COLUMN 只有"可空、无键、无唯一"一档活——自增列【312b ∶ 2813 同句 Cannot add a PRIMARY KEY column】、非空列【313 ∶ 2811 同句 Cannot add a NOT NULL column with default value NULL】各报一句；带上 DEFAULT 0 就建得成【3010】（契约"列默认值"格的正面读数）。挡在发射点而不是建完之后。
- `SqliteCodeFirst.cs · EmitAddColumn // 注`：2026-11-01 起契约有了「列默认值」格：配了默认值的 NOT NULL 新列在结构同步（StructureDiff.AddOrRefuse）放行，313 ∶ 2811 那颗号打不到它（日期账）。
- `SqliteCodeFirst.cs · EmitAlterColumnPrecision`：两层——语法层没有改列语句【F4 ∶ 2815 同句 near "ALTER": syntax error】；语义层这一问没有对象：声明宽度从不参与存储【4001 ∶ 4002】，VARCHAR(4) 改 VARCHAR(9) 在库里改不动任何东西；真正要改型状的那族操作是"建新表、拷数据、换名"，其形状本次没读数（RENAME TO 活【AG】，其余没有）——不在本版建模（§14）。
- `SqliteCodeFirst.cs · TryEmitColumnComment`：COMMENT 一词是语法错【F5 ∶ 2828 同句 near "COMMENT": syntax error】，sqlite_master 回显引擎规范化 DDL（列按顺序追加、表级约束排最后【3013】），"夹进尾巴再读回来"不通；本文件的 false 是七家第二次（第一次 Gbase8sCodeFirst，那家原因是"注释在目录表里"）。
- `SqliteCodeFirst.cs · 类注`："模式"格起稿写错过——Schemas 声明的理由是"限定名发得出而库认得它"（三处限定都有现场读数），`CREATE SCHEMA` 语法错【2824】、建到不存在的模式报 unknown database【2825】、这一族的模式实际是一颗 attach 进来的文件【3000/3001】；本家丢的 Alter_Column_Precision 与上一笔 GBase 掉的那颗缺法不同：GBase 是"发不出那条语句"，本家是"发不出而且没有对象"——交集缩水的理由完完全全在库那一侧。
- `SqliteCodeFirst.cs · EmitCreateTable`：具名表级主键这一形成立（2805 复合键 ∶ 3007/3008 单列键，目录里 pk 序号逐颗对得上）。
- `SqliteCodeFirst.cs · EmitCreateIndex`：全文索引的出处是枚举式——普通/UNIQUE/部分索引三形都建得成【2816/2817/2818】而里面没有全文；这一族有 fts5 整族虚表而本仓库对它一条读数都没有；3012 量到 ADD COLUMN 收生成列而契约没有那颗（与 §14 虚表/生成列并排）；ASC/DESC 两颗都发【307 ∶ 2816】，当场建成的索引在 pragma_index_list 看得见【2819】。
- `SqliteCodeFirst.cs · EmitDefault`：2026-11-01 那一格的 SQLite 半边（日期账）。
- `SqliteExpressions.cs · 类注`：Stored 归一的判据读数——驱动写库是"去尾零、整秒连小数点都不留"【2400 的 id 1…6 六行】；"加零天再等回自己"归一形命中 id=4/5 两行、没套的三行全不命中【2403 对 2404】；归一形对内联字面量数到 1、未归一数到 0【2405 对 2406】；%f 只到毫秒【2214 的 .4567 → .457】，库里 .0000001 过任何日期算术退成 .000【2402/2403 的 id=6】——列是 100 纳秒而算式只到毫秒（§14 一行）。
- `SqliteExpressions.cs · 类注`：%+d 与 %+f 分工读数——`printf('%+d day', 1.5)` 交回 '+1 day'（向零截断）【4219】、绑定参数臂同形【2702：1.5 → '+1 years' → 2027-03-09】；年月若发 %+f 会差十九天而不报错【2409：1.5 → 2026-04-24 vs CLR 2026-04-09】；日时分秒必须 %+f【2410：1.5 天 → +36 小时，%+d 会截成一天；2215 秒档 1.5 也在】。
- `SqliteExpressions.cs · 类注`：月末溢出读数——`'2026-01-31' +1 month` 交回 2026-03-03【2138】而 CLR AddMonths 交 2026-02-28；三行同判【2707：01-31→03-03、01-30→03-02、闰年 2024-01-31→03-02】、真列【2705】。能修的写法量到底了（第 43 轮）：CASE WHEN 判滚月 + `start of month / +2 month / -1 day` 夹到目标月月末 + STRFTIME 重接时分秒，四条输入对过 CLR【4308 四臂：01-31/01-30 → 02-28、2024-01-31+12 → 2025-01-31、2024-02-29+12 → 2025-02-28】、真列【4333】；三颗代价：start of month 把时钟抹到零点【4301/4302】而裸 +n month 不抹【4303】、DATETIME() 输出永远不带小数秒【4330 ∶ 4331】且 %f 定宽三位【4334：.7 → .700、缺省 → .000、七位折三位】——修正本身把 100 纳秒降级成毫秒；探针第一版就错在月末（`start of month/+1 month/-1 day` 量的是起始月月末【4304 ∶ 4309】，要拿目标月得写 +2 month）。取向：发短的那一形。九处 STRFTIME 掩码从审美变精度：DATETIME() 交 12:34:56 而 STRFTIME(…%f) 交 12:34:56.789【4330 那行的第一格与第四格】。
- `SqliteExpressions.cs · 类注`：文本载体两颗税——七颗定点按列排序 0.0, 1.05, 100.0000, 12.3400, …【2504】；MAX 交 '9.9999' 而场上最大是 1.2e21【2506】；`MAX("m", 100)` 交回 '12.34'——文本在这一家类型序里恒大于任何数【2710】；两定点过 MAX() 同判【2608】。好消息：算术函数自己折文本【2703：CEIL('12.34')=13、POWER('12.34',2)】；ROUND 打在 '-0.5' 交回 -1——一半向外舍，与 CLR Math.Round（银行家）相反【2703 第二行 ∶ 4217 字面量臂】，七家发的都是 SQL ROUND、非本家特有。折得不一致：CEIL('abc') 交 NULL、ABS('abc') 交 0【2704】。
- `SqliteExpressions.cs · 类注`：Blank 登记条件——@p0 同一句文本出现两次是驱动合法用法【4220 两臂 ∶ 4221 三处】；Gbase8sExpressions 为同一条形丢了这颗键（位置形 ? 下"文本两颗、表里一颗"会把值绑到隔壁列）。TRIM 只去空格【2203 的 char(9) 臂】，IsNullOrWhiteSpace("\t") 在本家不成立（六家同然，本家第一家量出来）。
- `SqliteExpressions.cs · 类注`：ToDate 零点形两臂——裸形命中三行而遇不上 DateTime 零点绑定参数（2605 数到 0）【2600/2604】、零点形命中同批三行【2602/2603】；取零点形与另六家同语义（虚谷 CAST(x AS date) 真列交 2026-05-06 00:00:00.0000000，XuguExpressions E-08a——七家契约是"截到日而仍是时刻"）；与 DateOnly 参数相遇本形不等（差一段 ' 00:00:00'，那一档要裸 DATE({0})）。
- `SqliteExpressions.cs · 类注`：DateSecond 截整秒【2515：.456 行的秒是 30 不是 30.456】（与虚谷 GETSECOND 交 9.456 的错法相反）；日期字段族打在带偏移文本上读 UTC 字段【2412：10:20:30.456+08:00 过 STRFTIME('%H') 得 02】、无偏移文本读本地格——同一颗键两种语义；Now/UtcNow 不是"库那台机器的钟"（引擎在进程内、与 CLR DateTime.Now 无网络/时区差），唯一损失是 %f 毫秒档【2211/2237】。
- `SqliteExpressions.cs · ConcatOperator`：`'a' || NULL` → NULL【2200 第二臂】；src/ 零消费者（七家同一格，判据在 MySqlDialectTests 钉着）。
- `SqliteExpressions.cs · LikeEscapeCharacter`：`'a_b' LIKE 'a_b'` 命中【2119】——没有默认转义符；反斜杠两臂【2120】、换 # 也认【2121】、绑定参数臂【2225】。
- `SqliteExpressions.cs · SupportsRegex`：REGEXP 操作符与函数两形都报 no such function【2700/2701】，要宿主进程注册（Microsoft.Data.Sqlite 没有注册）；场上有 GLOB【2123】与 LIKE【2119 起】。
- `SqliteExpressions.cs · Build // 注`：`1 || 2 = 12` 得 0（算子被 = 抢先）、`(1 || 2) = '12'` 才得 1【2230】；COALESCE 折叠读数——二参与三参 COALESCE 都交回右支【2143：COALESCE(NULL,NULL,3)→3、IFNULL(NULL,'d')→'d'】；七家 CONCAT 三档分布：T-SQL/PG 血统/虚谷模板自带跳 NULL（虚谷真行上双向数过【XuguExpressions 第三条】、T-SQL 立论跑在真库、PG 档只有 MySqlExpressions 对照无活库证人）、MySQL 血统/Oracle 血统/GBase 8s 按文档传播 NULL 且零活库证人（不由本笔替它们改）；本家量到"不折就错"（历史案：UpdateDeletePathTests.A_string_set_computed_from_the_column_concatenates_and_treats_null_as_empty 在第七家第一跑交回 NULL，`obj/logs/v37-sqlite-int-1.log`）。
- `SqliteExpressions.cs · Build // 注（逐键读数）`：Length【2201：'中文'→2、'😀'→1；2708：'12.34'→5】；Upper/Lower【2202：'éa'→'éA'、'İ' 不动；2239：'ä'/'Ä' 在 NOCASE 下也不等】；Trim【2203】；Blank【2204：数到 1 的是 '' 那一行】；Substring【2205 两臂 + 2610 真列；2130/2131：SUBSTR('abcdef',0) 交整串、SUBSTR('abc',1,0) 交 ''、负起点从串尾数——闸是防 CLR 会抛的档被静默发下去】；Replace【2206、2236：REPLACE('aaa','aa','z')→'za' 不回头扫】；IndexOf【2207 两臂、2226 NULL 传播、2612 绑定臂】；ToDate【2209：DATE() 交 text】；CastText【2208：CAST(12 AS TEXT)→'12'、typeof→'text'；2606：2.5→'2.5'】；DateYear 族【2210 六臂、2515；2227/2613：STRFTIME('%Y','not a date')→NULL——一句 WHERE Year(x)=2026 在混脏数据的列上静默漏行；2232：裸整数 1700000000 → NULL，TimeSpan/TimeOnly 落 Ticks 那两列不该用】；Now/UtcNow【2211 两臂差八小时；2400 前三行三种 Kind 落同一串 23 字符；2407/2408/2516 归一命中】；Add*【2213：单复数两形都收、+1.5 day 没量过不赌；2614/2511】；Round【2217/2234：ROUND 认一元、2.5→3、-2.5→-3、ROUND(1234.0,-2)→1234 负标度被忽略（CLR 抛所以是"少一步"不是错值）】；Ceiling/Floor【2221/2609/2703、2218：CEIL=CEILING 同值且交回 REAL——decimal 过这两颗退成浮点】；Power/Sqrt【2219：POW/POWER 两形、SQRT(9)→Double 落不进定点列不报错；EXP(1) 与 LOG(1000) 在场而 LOG 是常用对数不是自然对数、LN 没量——真要加 math.log 先答这一问】；Greatest/Least【2220 四臂：MIN(NULL,1) 交 NULL 不跳过，不补 COALESCE 因为那会藏"参与比较的是 NULL"】；Coalesce/NullIf【2223：IIF 形本表不用】；聚合【2222 八臂、2607；2224：GROUP_CONCAT 要带分隔符、CLR 无对应符号；4212/4213/4214：SUM 全 NULL → NULL 而 TOTAL → 0.0，跟 SUM 因 CLR Sum() 对空集交 null】。
- `SqliteExpressions.cs · Stored`：RTRIM 两颗的读数【2401 四臂：…10:20:30.000 → …10:20:30 而不是 …10:20:3（点停在"秒位零"与"小数位零"的分界上）；2401 第四臂 10:20:00 不削成 10:20:0】；2513 四行全等、2706 两行全等；带偏移输入会被折成 UTC 裸时刻【2412】——DateTimeOffset 列不能过日期算术的硬约束（与 SqliteValues.InlineOffset 旁边"能等值而不能排序"并排记 §14）。
- `SqliteTypes.cs · 类注`：探针编号撞号史——第 2 与第 4 轮各自从 100 起编号（`obj/sqlite-probe/probe2-report.txt` ∶ `probe4-report.txt`），第 2 轮到 119、第 4 轮到 156，中段恰好十六颗同名不同物（100∶106 七颗、110∶115 六颗、117∶119 三颗；107∶109 第 4 轮没编、116 第 2 轮写作 116a）——引到那一段必须带"第 2 轮"或"第 4 轮"。更正：第 2 轮 111 的列声明是 TEXT（RENAME COLUMN 而来），它证的是"TEXT 装得下 20 字符"，宽度不强制的正面读数在第 40 轮【4001 ∶ 4002 ∶ 4006】（历史账/更正）。
- `SqliteTypes.cs · 类注`：affinity 配方读数——声明成 DATETIME 的列落 NUMERIC affinity，写进去的 ISO 文本因"不像数"原样留成文本【600/610/612/703】——行为取决于值长什么样（定点不走 DECIMAL(n,m) 的全部理由）；DECIMAL(18,4) 里插文本静默变 REAL 12.34【606 ∶ 4206】。
- `SqliteTypes.cs · 类注`：布尔读数——typeof 对 BOOLEAN 声明列报 integer【607】，`= 1` 与 `IS TRUE` 两形都成立且同判【H0/335/607/831】；MySQL 血统与虚谷不取真布尔位的理由（那一型装得下 2、两判据）在本家反号：库里没有布尔这一型，声明它只是换一颗 affinity 配方——"位只跟存储型走"第一次由实测给理由。
- `SqliteTypes.cs · 类注`：定点绑定读数——驱动重写【63g：SELECT @v 里 12.3400m 得 12.34】；无 affinity 列逐颗复点【2500：12.3400→'12.34'、100→'100.0'、22 位整数逐位保真只多 '.0'】——这批读数推翻初稿"驱动走双精度"的归因（真走双精度会得 1.23E+21；内部哪一环没读数留 §14）；两串永不相等【2503："v"=12.34 得 0 而 "v"='12.3400' 得 1】；标度 4 原样留库【326/333/701 走文本、2501 复点对齐】；ORDER BY 词法序七行【2504】、MAX/MIN → '9.9999'/'0.0' 而 SUM 一步跳成 1.23E+21【2506】、`WHERE "v" > 10` 七行全中【2509】——聚合、排序与过滤走三条互不相干的尺；算术是对的（"v"*2 得 24.68【2508】、数学函数吃文本【2703】）——缺口只在比较与排序、不在求值（Greatest/Least 不包 CAST 的理由）。
- `SqliteTypes.cs · 类注`：ulong 读数——驱动把 64 位无符号往 long 里塞，ulong.MaxValue 现场读回 -1【505】；绑不变文本后 18446744073709551615 原样可回【702】；NUMERIC(20,0)/DECIMAL(20,0) 方案会被 affinity 改写成 REAL，不能照抄。
- `SqliteTypes.cs · 类注`：时刻读数——DateTimeOffset 自己的偏移原样进原样出【Y/509/604】（MySQL 是会话偏移、虚谷驱动不认、GBase 没有第三型）；驱动 ISO 文本可变宽且保 100 纳秒【G2/318/319/907/908】。第 44 轮推翻"按定宽文本绑定"的设计（初稿引的第 12 轮 912/913 读数按标签看方向相反且脚本没留档）：驱动写回全是可变宽【4402：同批十二行出现 …30 ∶ …30.05 ∶ …30.005 ∶ …30.4567891 四种宽度，最长 27 最短 19】；小数位可变宽在词法序上不翻——八颗最坏形状按文本排正是时刻序【4403】、整列文本排与 julianday 排同串且对 4402 逐颗读回正是时刻序【4405】、WHERE col > @bound 三颗小数位枢轴各命中 CLR 判【4406 三臂】；反倒是钉死宽度会坏：STRFTIME('%f') 只到毫秒且四舍五入【4407：.4567891 → .457，九行不再等于库里那颗】。两处边界：整数位宽度不齐（2026-3-9、1:20:30）翻词法序而 julianday 解不出交 NULL【4408 前两行】；同列混带偏移与不带偏移两形时词法序同样翻【4408 第三行】——§14 记的是"同一列混两形时文本序不可信"，不是"本家时刻序是错的"。
- `SqliteTypes.cs · 类注`：TimeSpan/TimeOnly 分道——同一颗 TimeOnly 走驱动默认路得到文本 13:45:00【507/508】，两型一起落 INTEGER 绑 Ticks【605/700 TS】；七家第一颗"跨度存整数而时刻存文本"（时刻要能被 strftime 问、跨度要能被减）。
- `SqliteTypes.cs · 类注`：浮点——REAL 是 8 字节 IEEE【文档·SQLite 数据类型第 3 节】而本家量不到这一颗：`length(CAST(x AS BLOB))` 交回的是十进制文本的字节数（3.5→3、1e300→8、-0.5→4，两颗 REAL 列逐字相等【4207 ∶ 4209 ∶ 4210】）——这一族的 BLOB 转换是文本化不是 IEEE 图像；目录原样回显声明字形【22/81/616】。
- `SqliteTypes.cs · 类注`：自增——AUTOINCREMENT 必须挂内联 INTEGER PRIMARY KEY【300/301/303/305】，表级 PRIMARY KEY 子句接不住；INTEGER 那颗宽是硬条件（BIGINT 直接被拒【306】）。
- `SqliteTypes.cs · StoreToClr`：零调用（TryGetClrType 在 src 与 tests 双侧无 caller）——"给 doctor 预备的读侧知识"而非现状（七族同规）；整数族按声明名给窄形是对账用（存储只有一颗 64 位整数【200 的 Int64 读数】，声明窄存储宽是常态）；boolean/bool/bit 认而不发；时刻四形读侧与发侧错位——别人建的表回显 DATETIME 等字形而本表说不出库里实际形状。
- `SqliteTypes.cs · String`：n 是注释不是约束【4001 ∶ 4002】——本方法发 VARCHAR(n) 唯一理由是对账可读（目录原样回显【22/81/616】）；统一发 TEXT 会让每颗带宽度列在 doctor 里变成真差。风险与重开条件：建侧宽度不挡超长值，挡它的是 FitsChars 而那道闸只在仓库负责写入的路径生效，手写 SQL 无闸——§14 记"哪一层要把'库不强制宽度'变成可见警告"。
- `SqliteValues.cs · 类注`：驱动完全不看 DbType 与 Size【500/500b/501：DbType 报 Int16、Size 报 1，交 long 仍原样落库读回 Int64】——ADO.NET 两颗"提示"是纯装饰，值形由 CLR 值型本身决定。
- `SqliteValues.cs · 类注`：定点与 ulong 必须绑文本——读数【63g；2500；505（ulong.MaxValue → -1）；2400 的 id 19 独立复点；326/333/701/702 逐字可回】；505c"小数整段没了"那颗读数脚本没留底、读数在而输入字形不在，只能算"还有一条没对上的旧读数"、不能拿来定原因（§14）；让绑定侧不把形状交给驱动的真正理由是 2503 那颗（"v"=12.34 得 0、"v"='12.3400' 得 1）——"谁去格式化定点"在本家是命中与否的问题。
- `SqliteValues.cs · 类注`：时刻与跨度读数——驱动写空格分隔 ISO 文本【G2/G3b】、100 纳秒由 .4567 那两颗量住【318/319】、Kind 丢【320/321】；TimeSpan/TimeOnly 原形那一路驱动写成 .NET 往返文本 '10:20:30.4560000' / '1.02:03:04.0050000'【2400 的 id 11/12/13，负跨度带 '-'】与 INTEGER 列声明撞格；DateOnly 交出去是裸日期 '2026-03-09'（10 字符串）不是零点形【2400 的 id 10】——Fn.ToDate 两臂之差的另一半。
- `SqliteValues.cs · Normalize`：char 改判——2400 的 id 20 量到两支同形（驱动自己把 char 写成 'A'），初稿【推断】被第二十四轮量掉、改判整颗记 §13.5（历史账）。
- `SqliteValues.cs · InlineDateTime`：抄驱动私有形状的读数——.4560000→.456、.4561000→.4561【907】；整秒不带小数点【908】；7 位全有效逐位保真【921 ∶ 922 那颗 27 字符串】；40 毫秒写成 '.04'（id 5）、1 tick 写成 '.0000001'（id 6，没有被就近舍到 6 位）【2400 id 1…6】——已知宽度（0/2/3/4/7 位）各有一颗读数；仍没量的是跨零那侧（23:59:59.9999999 过驱动后是不是还是那一串，归"文本序 vs 时刻序"口子）。
- `SqliteValues.cs · InlineOffset`：读数——偏移前不带空格、带冒号【604】；+08:00 与 -05:30 原样入库、零偏移写的是 +00:00 不是 Z 也不是不留【2400 的 id 7/8/9/25】；id 9 复点 Stamp（整秒+偏移 → 10:20:30-05:30，小数点与小数一起不留而偏移那颗 30 留着——"先拼小数再拼偏移"的顺序由 TrimEnd('0') 会咬到偏移决定）。
- `SqliteValues.cs · InlineOffset`：词法序读数【2413：id 7 与 id 8 是同一串钟面（10:20:30.456）配两个偏移，按真实瞬间 +08:00 那颗早八小时而 "7" < "8" 数到 0——词法上 '+0' 排在 '+8' 前面；id 9（-05:30）与 id 4（无偏移）相比也得 0——前缀档：无偏移串是带偏移串的开头】——DateTimeOffset 列能等值而不能排序（排序语义缺口不是等号缺口，§14 与 SqliteTypes 第五条并排）。
- `SqliteValues.cs · InlineNumber`：本家可问对象读数——整数字面量进 REAL 列 typeof 交回 real【4204】、UPDATE … SET r = 5 与绑定参数两路同【4226 ∶ 4228 ∶ 4230】——补不补小数点在浮点臂不落存储；同一条字面量进 TEXT 列交回 '3'（本家定点列正是 TEXT）；初稿反话（"整数字面量进 REAL 列会存成 3 而不是 3.0"）被第 42 轮当场否掉（§13.5"本版被实测改掉的设计判断"；历史账）。
- `SqliteValues.cs · Literal`：N'…' 在本族是语法错【实测·P】——unicode:false 是被正面量过的；双引号在这一族不是标识符引号而是"字符串字面量的第二形"【13/14/15/L】——"标识符写错成静默字符串"陷阱的成因，防线在 IdentifierPolicy 与 SqliteDialect 两处。
- `SqliteSchemaReader.cs · 类注`：目录认文本不认标识符的读数——函数参数走引擎名字解析、大小写不敏感（pragma_table_info('R34') 对声明成 r34 的表照样交回四列【3404】，xinfo('T41') 同【4113】——本文件没有 Fold）；行集里 "name" 是普通文本列、等号比字面（WHERE "name"='v' 对声明成 V 的列 0 行【4109】、加 COLLATE NOCASE 命中【4110】）；NOCASE 只折 ASCII：声明成 ä 的列用 Ä 去比在 BINARY 与默认两形都 0 行【4111 ∶ 4111b】、声明形才命中【4112】，库自己的大小写函数也 ASCII-only（upper('ü')='Ü' 为 0【第 4 轮 111、112】）——"与非限定名在 SQL 里的解析完全一致"的边界没读定【无出处】。
- `SqliteSchemaReader.cs · 类注`：空表读数——函数对不存在的表交回空集不报错【3402】，"表不存在"与"这表没有列"同一份读数；`CREATE TABLE t ()` 是语法错【3503】所以"存在而读不到列"那支是空的。真正的静默入口是名字里装点：`main.w40` 绑进函数参数交回 0 行【4007】、两点都绑一样【4008】而数据侧读得到【4009】，数据侧对不存在的表是响的（no such table【3801】）——不补守卫（TableInfo 把 Schema 与 Name 拆开、点分不是合法形，§14）。
- `SqliteSchemaReader.cs · 类注`：表 ∶ 视图闸——本家构造不出"同名视图混进读数"（视图已存在时建表报 view v37 already exists、反向 table s37 already exists【3701 ∶ 3702】，同库共用名字空间）；补闸反而亏：pragma_table_list 等值比是大小写敏感的【3608 用 R36A 交回 0 行】换来假阴性，分辨表与视图的能力有且量过（table_list 的 type 对 v37 给 'view'【3713】、对 r34 给 'table'【3407】）——"能问而不问"的取舍。
- `SqliteSchemaReader.cs · 类注`：未点 schema 读数——第二 arg 绑 DBNull 等于"不点"而不等价于 main：对 main 里的表两形同解【3504 ∶ 3405】，对 temp 表不点交回它、点 main 交回 0 行【3601 ∶ 3507b】；更硬的：temp 同名表遮住 main 且目录与数据都被遮（目录不点交 temp 三列、点 main 交两列【3704 ∶ 3705】；SELECT b FROM s37 报 no such column【3902】、未点 INSERT 落 temp【3904/3905/3906】、未点 DROP TABLE 打掉 temp 而 main 活着【3909/3910/3911，目录又交回 main 两列【3912】】）——读侧与写侧必须用同一颗 TableInfo.Schema；`pragma_table_info('t','')` 报 unknown database ''【3505】。
- `SqliteSchemaReader.cs · 类注`：四颗读不出的字段——宽度没有独立字段（只活在 type 文本里【4003 原样回显、4004 小写回显小写】，DeclaredChars 从文本读数是七家独一份）；排序规则不在列上（pragma_collation_list 只收 0 颗入参【3509】、collation_list('t') 函数不存在【3510】、pragma_collations 表不存在【3511】，列上 NOCASE 只在建表文本里看得见【3502 ∶ 3512】——CollationName 恒空串，"某列声明 NOCASE 就变 CI"读侧看不见，不解析别人 DDL）；自增读不出（AUTOINCREMENT 在目录里 type='INTEGER'、pk=1、dflt_value 空【3401 第一行】，"会自增"只在建表文本里【3408】，SchemaColumn 无槽位——与 Gbase8s 的 SERIAL 同缺口不同因）；生成列读不到是选择（table_info 四列 vs xinfo 五列【3501 ∶ 3401】，hidden 只量到 0 与 2 两值、第三值没读定【无出处】——doctor 看不见生成列，"契约多写一颗生成列"读不出来，§14）；dflt_value 不该读（驱动行集元数据报 Byte[]【4102 GetFieldType】而 SQL 层是文本【4114】，两形自相矛盾且无槽位）。
- `SqliteSchemaReader.cs · 类注`：声明宽度不参与存储——VARCHAR(4) 装进 20 字符、CHAR(2) 装进 10、NVARCHAR(6) 装进 10【4001 ∶ 4002 ∶ 4006】，NUMERIC(10,2) 收文本原样留（typeof 为 text）——"字符数还是字节数"连可观察行为都没有，CharSemanticsProven=false，两颗长度填同一个数（自己写进 DDL 的自述，doctor 只能比自述、比不了"库容得下"）。取数口：数字字段在驱动里是 Int64【4102】而 GetInt32 在 Int64 列上过【4103 ∶ 4104 ∶ 4107】；GetBoolean 可用【4106】而不选——notnull 除 0/1 还有没有别的值没读定【无出处】。
- `SqliteSchemaReader.cs · ColumnsSql`：ORDER BY "cid" 不可换成 "pk"——复合键上两序不同解（声明顺序 B,A,C 而 pk 给 2,1,0【3604】），按 pk 排会把契约列序读乱；notnull 报的是声明形状不是强制形状（INTEGER PRIMARY KEY 不带 NOT NULL 给 0【3603】、同一约束在 WITHOUT ROWID 表上给 1【3605】）。

---

# QuickSql 注意事项归档（part-e）

> 2026-11-03 注释精简（简短 `///`）第三轮：从 `src/QuickSql.Providers.MySqlBase`、`QuickSql.Providers.MySql`、`QuickSql.Providers.TiDb`、`QuickSql.Providers.OceanBase`、`QuickSql.Providers.PostgreSqlBase`、`QuickSql.Providers.PostgreSql`、`QuickSql.Providers.Kingbase`、`QuickSql.Providers.OpenGauss`、`QuickSql.Providers.GaussDb`、`QuickSql.Providers.Highgo`、`QuickSql.Providers.Gbase8c` 十一个项目的代码注释里摘出的坑位、边界、已知限制、实测读数与历史账，按血统族归档。锚格式为 `文件相对路径 · 成员名`。

### MySQL 血统

- `src/QuickSql.Providers.MySqlBase/MySqlBulk.cs · 类注`：参数预算口径噪声——厂商文档另有一个"65,536"的写法（差的那一颗是 0 号位），今天没有一台库能分辨，取小的那个（协议里占位符计数字段只有两字节宽，65,535 是"Prepared Statement 限制"一节给的数）；报小只多几次往返，报大会在真库上得到"参数过多"。
- `src/QuickSql.Providers.MySqlBase/MySqlBulk.cs · 类注`：max_allowed_packet 是这一族"字节而不是个数"的第二道闸门（会话与部署两侧的数，默认随版本 4~64 MB）；65,535 颗参数在 LONGBLOB 表上可以先撞包体再撞占位符。跨族历史账：虚谷厂商文档给一行记录大小上限 64K，同档闸门而没有任何槽位承接（ColumnContract 只有列宽没有行预算）——本家那颗至少能折算到列上（65535 / 4 = 16383 已写进 MaxChars），那一颗连折算落点都没有。
- `src/QuickSql.Providers.MySqlBase/MySqlBulk.cs · 类注`：五家批量对照读数——块数 3 / 1 / 50 / 1 / 50、预算 2100 / 32767 / 100 / 65535 / 100、每语句行数 270 / 4212 / 12 / 8425 / 12（"三家块数互不相同"的原判据被这一族的加入推翻，重复的两处都落在"没有真预算"拿 100 兜底那一边）；这一格真正被钉住的性质是"兜底分支有消费者"，不是任何一位数。
- `src/QuickSql.Providers.MySqlBase/MySqlBulk.cs · SupportsMergedStatements`：实测读数——200 行整行更新合并档 701 ms ∶ 逐条 871 ms（省的是往返、省不了那 200 句的执行，实例往返本就约 1 ms）；四句 UPDATE 并一条命令落库成功、逐列读回一致。见证：集成册 BulkWritePathTests、`obj/logs/v40-my-bulkwrite-2.log`。
- `src/QuickSql.Providers.MySqlBase/MySqlBulk.cs · UpsertClause`：未实测——AS 行别名是 8.0.19 起的语法，挂在共用的 MySqlBulk 上三颗叶子（MySQL ∶ TiDB ∶ OceanBase）一起领，本仓库只证到 MySQL 8.4.8；另两家的 ON DUPLICATE KEY UPDATE 行别名是否等价未实测（与 LOAD DATA LOCAL 那家两叶子同档）。老写法 VALUES(col) 在 8.4.8 每句带两条 Warning 1287（厂商写明将移除），行别名写法收下且 SHOW WARNINGS 为空（探针 `obj/logs/v40-upsert-shapes2.log`）。
- `src/QuickSql.Providers.MySqlBase/MySqlBulkCopy.cs · TryResolve`：反射面四格形状差（真库第一趟把把交回 null、静默整批退 L3 的根因，失败形状与"没有装载类"完全同形，离线探针一根都看不见）——① MySqlBulkCopy 只有一颗公开构造子 `(MySqlConnection, MySqlTransaction? = null)`，按"一位参数"点形状永远点空；② MySqlBulkCopyColumnMapping 是 `(int, string, string? expression = null)`，Activator.CreateInstance 的绑定器不认默认值，当场 MissingMethodException；③ WriteToServerAsync(DataTable, CancellationToken) 交回 ValueTask\<MySqlBulkCopyResult\> 而不是 Task，(Task) 硬转会红；④ 这一族装载类没有任何 Dispose 面（零接口、无 Dispose/DisposeAsync），不能 cast IDisposable。成员形状逐颗在强类型量具里跑通过 2000 行 × 7 列与 20000 行（MySqlConnector 2.6.2 / MySQL 8.4.8）。
- `src/QuickSql.Providers.MySqlBase/MySqlBulkCopy.cs · UnavailableBecauseOf`：实测——缺 AllowLoadLocalInfile=true 时驱动在执行当场抛 NotSupportedException（补参前两条批量路径同红）；通道把它提到选层之前，Auto 落 L3 交回 Method=MultiValue、Degraded=false，那句话原样进 BulkReport.Reason——Auto 那条路上唯一的可见面，写成"没有通道"会把人支到 provider 侧，该查的是连接串配置。
- `src/QuickSql.Providers.MySqlBase/MySqlBulkCopy.cs · 类注`：边界——服务端 local_infile 开关（8.4.8 实测为 ON）不在客户端可见面里，判不了就不装判，那条红线由写当场抛驱动自己的话接住。
- `src/QuickSql.Providers.MySqlBase/MySqlBulkCopy.cs · WriteAsync`：实测——活事务里经通道写入的四行随回滚一并消失（提交那一半读得到、回滚那一半读不到，两半是同一颗通道）。历史账：旧句"构造子只收连接、L1 交不出外部事务"来自强类型调用面，已按反射面（两位构造子）改判。
- `src/QuickSql.Providers.MySqlBase/MySqlChangeGuard.cs · 类注`：坑——这一家的 <> 走列上排序规则，默认 utf8mb4_…_ci 不区分大小写与重音，"库里是 ABC、新值 abc"会被判成没变而挡在 UPDATE 外（存储字节确实会变，挡掉就是丢写）；T-SQL 同病（默认 CI）、达梦取决于实例参数、金仓取决于列 COLLATE；承载它的合并档被方言闸门挡住，要等接上 MERGE INTO / UPDATE … JOIN 才显形；重开判据=要不要给守卫强制 BINARY 比较。
- `src/QuickSql.Providers.MySqlBase/MySqlChangeGuard.cs · 类注`：可达性边界——EntityTracker.Mergeable 先问 Dialect.SupportsJoinedWrites（EntityTracker.cs:860），逐列问 Compares 排在其后（:869）；这一家那颗开关是 false，"守卫关一列 ⇒ 整批退回一句一实体"的后果由方言闸门代跑，守卫自己的 false 只被离线手工构造 AST 的用例量到。历史账：上一版写"合并档整批退回逐条"，交账时按发射顺序重数推翻——后果对、归因错。
- `src/QuickSql.Providers.MySqlBase/MySqlChangeGuard.cs · 类注`：json 关闭的理由与第三家那四颗 LOB 不同条——不是厂商明文限制，是"<> 在它上面的行为问不出来"（比较运算符对 JSON 的支持有版本线，3142 那档报错的真实触发条件是排序与 < / >，不是守卫发的这一句）；加上类型映射根本产不出 json 列（Json\<T\> 在 src/ 0 处），所以它是护栏。
- `src/QuickSql.Providers.MySqlBase/MySqlChangeGuard.cs · 类注`：文本层对照——TEXT/BLOB 可以进比较式（与 T-SQL 同档），同一根无上限 string 在 NVARCHAR(MAX) 与 TEXT 上守卫形状相同；Oracle 血统产得出同一份三段式却发不出承载它的那一句。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · 类注`：坑——MODIFY COLUMN id INT 会顺手摘掉那一列的 AUTO_INCREMENT，它是主键时库直接报 1075；型、可空性、自增（加注释时还有 COMMENT）必须一起写全。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · EmitAlterColumnPrecision / TryEmitColumnComment`：实测（8.4.8）——同一列先发带 COMMENT 的 MODIFY、备注读得回来，再发省掉 COMMENT 的一句，目录里 column_comment 就成空串：改精度若不带上契约里的注释会抹掉库里已有注释，库里已有而契约没有的注释会在改精度时丢失（已知代价）。读数 `obj/logs/v38-my-recon-1.log` R-07/R-08/R-09。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · TryEmitTableComment`：实测——ALTER TABLE … COMMENT = '…' 不吃 ALGORITHM = INSTANT（报 "ALGORITHM=INSTANT is not supported for this operation"，INPLACE, LOCK = NONE 才认），所以刻意不带任何 ALGORITHM 子句；写值、清值都当场生效，COMMENT = '' 写的就是"没有备注"、目录读回的也是空串，写与读闭合。读数 R-06。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · EmitCreateIndex / TryEmitDropIndex`：实测——对 PRIMARY 直接发 ALTER TABLE … DROP PRIMARY KEY 被库挡下（报"自增列必须定义成 key"，R-11）；外键支撑索引那一句报 Cannot drop index …: needed in a foreign key constraint（R-10，由计划层归进"要人工"）。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · 类注`：已知限制——FULLTEXT 默认解析器按空格与标点切词，中文要 WITH PARSER ngram（8.0 起自带但要显式选），而 IndexContract 没有那一格：中文全文索引今天建成的是英文分词，且这一家不报错（比"抛"更该写清）。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · 类注`：已知限制——索引列的字节上限（旧 767 / 新 3072，utf8mb4 下折 191 / 768 字符）今天不判：长字符串列建索引红在库而不是红在这里，前缀长度在契约里没有格（重开=契约长出前缀长度槽位）。
- `src/QuickSql.Providers.MySqlBase/MySqlCodeFirst.cs · EmitTable`：未实测——据厂商目录形状，CONSTRAINT 名落在索引目录（information_schema.STATISTICS 的 INDEX_NAME = 'PRIMARY'）而另三家在约束目录各有一行；若属实，同一条 DDL 在四家元数据里不是同一个东西，而 doctor 读目录不读文本——有真库的那一天第一个重读。五家的目录视图今天没有一台真库跑过。
- `src/QuickSql.Providers.MySqlBase/MySqlConcurrency.cs · 类注`：重开条件——接"库给值的版本列"需两步：类型映射按"并发标记 + DateTime"置 StoreFillsValue = true，CodeFirst 写 ON UPDATE CURRENT_TIMESTAMP 子句；第一步不是免费的——置了它那一列就从 INSERT 列清单掉出去（"值归库给"分支），没打算用库维护的现场会突然插不进那一列。触发条件：第一次有人要求在 MySQL 上用库维护的版本列跑并发用例。
- `src/QuickSql.Providers.MySqlBase/MySqlDialect.cs · 类注`：未实测——本类产出文本没有在一台真库上跑过（授权实例里没有一家是 MySQL 血统），逐条判据是离线文本钉的。
- `src/QuickSql.Providers.MySqlBase/MySqlDialect.cs · EmitPaging`：三家三样的"只有 OFFSET"格——金仓可以光写 OFFSET，这一家补 2^64-1 哨兵（LIMIT 吃无符号大整数），第五家补厂商写死的 2147483647（同形不同义，"两形两颗数"）。
- `src/QuickSql.Providers.MySqlBase/MySqlDialect.cs · 类注`：第五家有没有布尔字面量【无出处】——"两家共用 1 = 0/1 = 1"那句不跟着涨；它落在 1 = 0 这一边是事实，为什么落在这一边还没读到。
- `src/QuickSql.Providers.MySqlBase/MySqlExpressions.cs · 类注 / Concat 模板`：实测读数（8.4.8）——CONCAT(NULL, '/尾') 交 NULL（列上同形）、GREATEST(NULL, 5) ∶ LEAST(NULL, 5) 各交 NULL；IFNULL 包装与 CONCAT_WS 两候选在四组问句上同数，取 IFNULL 因为"哪一颗实参被折成空串"写在文本里而 CONCAT_WS 藏在分隔符常量里。历史账：曾写"与 T-SQL 同边"，实际共用层在 T-SQL 侧发的是 CONCAT 函数（跳过 NULL），两边恰恰分岔——集成册 `SET [Note] = [Note] + '/尾'` 在 NULL 行交回 "NULL" 而契约要 "/尾"（`obj/logs/v37-my-int-1.log` 第一条红）。
- `src/QuickSql.Providers.MySqlBase/MySqlExpressions.cs · ConcatOperator`：实测（8.4.8）——('a' || 'b') ∶ (1 || 0) ∶ ('' || 'b') 交回 0 ∶ 1 ∶ 0：字符串被隐式折成数值再进 OR，比"恒假"更糟（连实参是什么都不看）；拼接不能换成 ||，与"CONCAT 要包 IFNULL"是同一格的两侧。
- `src/QuickSql.Providers.MySqlBase/MySqlExpressions.cs · LikeEscapeCharacter`：代价可数——含通配符的 StartsWith/Contains 会转义成 a~%b 而不是 a\%b，五家对读 LIKE 参数值时这一格不同（"各家各按自己的转义符"的正常结果，不是缺陷）；重开条件=谁接了 sql_mode 建模（NO_BACKSLASH_ESCAPES）就可换回 \。改成 '\\' 要动 IExpressionRules 的形状（字符 → 字符串），那是接口契约。
- `src/QuickSql.Providers.MySqlBase/MySqlExpressions.cs · Substring 模板`：FROM/FOR 两支能吃多复杂的表达式没有一台库能证——取标准支是"与金仓同形"与"少一处可指认的实测依据"之间的交易。
- `src/QuickSql.Providers.MySqlBase/MySqlKeys.cs · 类注`：未实测台账——本机没有授权 MySQL 实例，SupportsPostStatementKeyRead 这一位的全部凭据是离线文本 + 厂商文档，"已接上"不等于"已实测"（同档客户虚谷有真库见证、这一家没有）。
- `src/QuickSql.Providers.MySqlBase/MySqlKeys.cs · EmitReturning`：多行 VALUES 的 id 连续性要连 innodb_autoinc_lock_mode 一起建模（interleaved 与主键冲突/忽略行都会断它），那一档不在这里假装能用；历史账：连续性推断那一格在"每块单行 + 同一条命令"建模之前是"不接一个看起来能用的回读"。
- `src/QuickSql.Providers.MySqlBase/MySqlKeys.cs · IdentityInsertToggle`：IdentityInsertToggle 那一口在 Core 里至今没有任何调用点——覆写它会得到一颗永远不被调用的虚函数（SupportsExplicitIdentityInsert 五家实现里四家 false，只有 SqlServerKeys 发得出 SET IDENTITY_INSERT）。
- `src/QuickSql.Providers.MySqlBase/MySqlProviderBase.cs · 类注`：刻意不声明的位与理由——Returning（EmitReturning 是抛的）；Boolean_Type（TINYINT(1) 装得下 2，声明等于让谓词走 IS TRUE 那一支，与方言判断相反；它有 BOOL 这个名字，曾是落地各档里最容易误声明的一位）；CaseSensitive_Identifiers（列名一律不区分大小写，表名那颗开关 lower_case_table_names 建库时定死而不在库里、管的是文件系统；引号照加与大小写敏感是两件事——前者防保留字、后者是解析器判等）；Upsert_OnDuplicate（整份 src 没有一颗读者按这一位分派，写侧选层走 IBulkStrategy.UpsertClause 策略槽——位与槽两面，走到的是槽、清账的是位）；Cte / WindowFunctions / Count_Over_Window / Json_Type / TempTable（库里都有但代码没有对应的发射口；窗口函数那颗经 QuickFx.RowNumber 有发射口但发出来的句子不按这一位分派）；MultiStatement / MultiResultSets（见下一条）。
- `src/QuickSql.Providers.MySqlBase/MySqlProviderBase.cs · 类注`：历史账（MultiStatement）——原文"要驱动显式开启（AllowUserVariables/AllowLoadLocalInfile 之外的那一颗）"没有真库读数背书，读数上它不成立：多值 INSERT 带 ; SELECT LAST_INSERT_ID() 的 postRead 回填支每轮集成册都在活 MySQL 上绿，那条串里没有为多语句放行任何东西；位仍不声明，真理由是"没人按这一位分派"。EmitCreateTable 尾部的 " ; " 串联在五家都只能走脚本路径。
- `src/QuickSql.Providers.MySqlBase/MySqlProviderBase.cs · 类注`：能力位台账——6 位 = 5 位离线文本 + 1 位装载通道；Bulk_Copy 要按两半读：驱动 API 本身有真库读数（对照量具 m1∶m2 强类型两条批量路径），provider 里那条反射装配还没在集成册跑过（"成员形状已证、装配路径未证"档）。历史账：5 位时期"0 位仅文档"的口径重数（"四档里唯一一个"被推翻——达梦 6 = 6 + 0、金仓 14 = 7 + 7、第五家 7 = 7 + 0），与"并列最少而不是唯一最少"各错过一次。
- `src/QuickSql.Providers.MySqlBase/MySqlProviderBase.cs · Bulk`：实测读数——2000 行走 L1 与走 L3 的耗时差复跑三趟 67 ∶ 108、68 ∶ 105、58 ∶ 107（毫秒，同一台 8.4.8 同一颗驱动）；"快约一倍"在新读下落到 1.5~1.8 倍之间，不把一次现场读数钉成断言；更早一趟 `obj/logs/v40-my-bulkcopy-3.log` 的"约一倍"是历史读数。反射版第一趟在真库上把把交回 null（四格形状差，见 MySqlBulkCopy 条目）。
- `src/QuickSql.Providers.MySqlBase/MySqlProviderBase.cs · 类注`：历史账——"MariaDB 的 RETURNING 与 TiDB 的 AUTO_ID_CACHE 是两颗已点名的将来覆写者"的预测落空：MariaDB 档已从 DbKind 撤下、本仓库不立（撤档理由与重开条件在设计文档 §14），AUTO_ID_CACHE 那格 TiDB 叶子没领；两颗分叉叶子到场后掩码一位没加没减。
- `src/QuickSql.Providers.MySqlBase/MySqlSchemaReader.cs · CatalogLength`：实测坑——DbDataReader.GetInt32 在 4294967295 那格直接抛 OverflowException（驱动器 TextSignedInt64ColumnReader 抛的，8.4.8 活库量出），一张有 LONGTEXT 列的表会让整趟 ReadTableAsync 红掉而不是那一列缺格；所以先按 64 位取、装不下交 null。
- `src/QuickSql.Providers.MySqlBase/MySqlSchemaReader.cs · 类注`：缺口——tinyint(1) 与 tinyint(4) 只有 column_type 那一列分得出（活库实测：同一张表上两列的 data_type 都是 tinyint，读数 R-02），而 SchemaColumn 只有 StoreType 一格、型名从 data_type 拼；重开=doctor 读 column_type，不靠"看到 tinyint 就当 bool"猜（猜错方向是把 255 装进 bool）。
- `src/QuickSql.Providers.MySqlBase/MySqlSchemaReader.cs · ColumnsSql / MaxBytes`：历史账（旧伤）——MaxBytes 的注释从早期就写"字节上限是目录里现成的那一颗"，读的序号却是 CHARACTER_MAXIMUM_LENGTH（字符数），utf8mb4 下两把尺差四倍、交回的数一直比真值小一个量级；已改成读 CHARACTER_OCTET_LENGTH。SchemaColumn.MaxBytes 今天在 Core 一侧没有消费者（StructureDiff 只看型名、可空性、备注与主键位），改读法不动任何同步判据。
- `src/QuickSql.Providers.MySqlBase/MySqlSchemaReader.cs · ReadIndexesAsync`：实测（Y-07）——lower_case_table_names 只动表名不动索引名，索引名按建它时那份拼法存着，比较器因此用 Ordinal。
- `src/QuickSql.Providers.MySqlBase/MySqlSchemaReader.cs · 类注`：函数索引（8.0.13 起的表达式索引）column_name 为 NULL，契约表达不了它：读回来是"库里多出一条索引"里一条没有列的假名，开了删索引那一档它会被删掉，所以整条挡在外面（与 PG 家挡 indkey = 0 同一取向）。
- `src/QuickSql.Providers.MySqlBase/MySqlTypes.cs · 类注`：边界——单列 VARCHAR 上限 16383 字符是行级预算的一部分（65535 / 4），ColumnContract 只有"这一列多宽"的槽位、没有"这张表还剩多少行预算"的格；重开=长出行预算槽位。
- `src/QuickSql.Providers.MySqlBase/MySqlTypes.cs · GetStoreType`：TIMESTAMP 的三坑——范围只到 2038（且取决于版本）、按会话时区解释、"第一列可能被隐式挂上 ON UPDATE CURRENT_TIMESTAMP"的历史行为；所以 DateTime 落 DATETIME(3) 而不是 TIMESTAMP。DateTimeOffset 宁抛不静默：TIMESTAMP 存 UTC 而显示按会话时区换算，装不下值的偏移，悄悄按本地时间写是把 +08:00 变 08:00 再读回 00:00 的那种静默改值。
- `src/QuickSql.Providers.MySqlBase/MySqlTypes.cs · 类注`：Guid 的 BINARY(16) 存法要连接串 GuidFormat=BinaryUuid 与字节置换一致，SPI 没有"连接串参数改变方言假设"的槽位（同一缺口的实例：金仓 Oracle 兼容模式、lower_case_table_names、ANSI_QUOTES、第五家 CHAR_SET / COMPATIBLE_MODE——户数涨而缺口原地不动）。
- `src/QuickSql.Providers.MySqlBase/MySqlValues.cs · CreateParameter`：填 Size 的收益今天只落在人工对读——五家在断言里逐颗写出 AdoParameter 清单的只有 SqlServerValueTests 与 KingbaseDialectTests 两份，其余三家比的是 ToLogSql 渲染行；多填一颗不要紧的参数，不是闸门。
- `src/QuickSql.Providers.MySqlBase/MySqlValues.cs · InlineNumber`：未实测——FLOAT/DOUBLE 列上 3 与 3.0 是不是同一档隐式转换没有读数，取"与另两族一致"而不是"这一家更松"；判据出处是 SqlServerValues.InlineNumber 真库实测（1E-08 合法、1E-08.0 是语法错）之后的形状。
- `src/QuickSql.Providers.MySqlBase/MySqlValues.cs · TryInlineLiteral`：NaN——这一家有 'NaN' 那种"带引号再 +0"的写法，而裸 NaN 会被解析成列名；与金仓同一格判断："没有一台库能证"。
- `src/QuickSql.Providers.MySql/MySqlProvider.cs · 类注`：历史账——"本家是唯一一处把版本选型写在叶子类注释上"的位数变迁：三处版本侧（本家按 8.0 最新侧、PG 血统按 9.2 最老侧的 CAST 包装、Oracle 血统按 12c 的 IDENTITY）→ 两处明示（PG 本尊到场把三段区间写在叶子注释上）；两个正典叶方向相反——本家把代价推给派生叶、PG 本尊把代价留给自己；判据与全文在设计文档 §14"三颗 IdentityClause"那行的注。OracleCodeFirst 类注释末段还有一颗 12.1 之前的 30 字节上限没判。
- `src/QuickSql.Providers.TiDb/TiDbProvider.cs · 类注`：推断与未实测——AUTO_INCREMENT 同词同位置是按"TiDB 以 MySQL 语法为兼容基线"推断（本机无一枚实例可问）；ENGINE = InnoDB 在 TiDB 上被接受并被忽略、逐条 + LAST_INSERT_ID() 与 MySQL 同读数均未实测；重开=真实例到场（官方镜像）。"已适配"三句全立不起来：驱动从未解析成功、零条语句执行、中文从未问过；SHARD_ROW_ID_BITS 是每表选项，SPI 没有那一格。
- `src/QuickSql.Providers.OceanBase/OceanBaseProvider.cs · 类注`：两层未实测——第一层 MySqlConnector 从没解析成功过（mysql/tidb/oceanbase 三键共用 assembly，整行未实测）；第二层"这条连接到底建在哪个模"问不出来（连接级事实而非库种事实，与金仓兼容模式是同一颗未建模缺口）。重开=真实例到场，且比 TiDB 多一件要先问：确认那台连接建在 MySQL 模——今天没有任何程序入口能答。已适配三句全立不起来。
- `src/QuickSql.Providers.OceanBase/OceanBaseProvider.cs · DriverKey`：若现场把库建在 Oracle 模，该走的是另一颗驱动（今天内建表里没有——Oracle 模那颗叶子被裁后置）；这一格差异今天无处声明。
- `src/QuickSql.Providers.Kingbase/KingbaseProvider.cs · 类注`：未实测——金仓 V8 内核认不认 GENERATED BY DEFAULT AS IDENTITY 没有读数；Oracle 兼容模式开关会改变分页与 NVARCHAR2 行为，SPI 没有"兼容模式"槽位、未建模（缺口与重开条件在设计文档 §14）。

### PG 血统（PostgreSQL 系）

- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlBulk.cs · 类注`：未实测——参数预算 32767 是 PG 前端/后端协议 Parse/Bind 消息的有符号 int16 上限（出处是协议文档不是现场实测），接上活库后要重测。Merged 档的 false 有协议级禁止靠山（扩展协议下一次发送塞 ; 分隔的多条语句不合法），但"条文说禁"与"驱动会不会自己拆一条批"是两件事，后者没有读数；重开=集成册补跑一场多语句批。实测反面（`obj/logs/v40-postgresql-bulkwrite-2.log`）：Auto 落逐条、点名 Merged 降级并说出 provider、FallbackPolicy.Never 拒在连接之前，IN 档照旧一条语句删整批——条文既没被推翻也没被证实。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlBulk.cs · UpsertClause`：实测（探针 `obj/logs/v40-upsert-shapes2.log`）——三值一批（其中一颗键库里没有）收下、缺的那行插进去、已有两行被改掉。指定冲突目标的入口今天没有：现场靠另一颗唯一键去重的表，本档不接那种调用姿势（缺口待接线）。ON CONFLICT … DO UPDATE 是 9.5 起的语法，金仓与国产各支的内核版本下限没有活库可量。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlBulkCopy.cs · 类注`：边界——BulkConnection.Transaction 交不出去（导入器挂在连接上、没有外部事务入参面），量具与集成册跑到这一档的路径都是无事务的，带活事务的 L1 是未实测边界；二进制 COPY 对列类型不匹配的报错落在流的中段，一批里第 n 行不合格给不出"坏在哪一行"——这也是白名单不放宽到 char/DateOnly/TimeOnly 的原因之一（不是进不了二进制格式，是过不了与文本层同一把类型尺）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlBulkCopy.cs · TryResolve`：行数泛型随版本漂——Complete() 同步那一颗在 Npgsql 10.0.3 上是 ulong；认死一种泛型参数会把本来能成的整家退掉，所以只认 long/ulong 两种。借邻家臂的叶子装的是没有导入器的 fork 时通道整家缺席，照旧走文本层。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlCodeFirst.cs · IdentityClause`：推断——"9.2 内核不认 IDENTITY"是从内核代数推的，厂商行明列的分岔里没有这一句；三段区间（PG 10 之前建表语法错 ∶ 10–13 恰合 ∶ 14 起多一层不再必要的 CAST）是本仓库自己算的【推断】（由 IDENTITY 要 10+ 与 CAST 因 14 才必要两句朝相反方向指推得），厂商行里没有这一句。真实例到场第一问：这句 CREATE TABLE 收不收／现场那台是几。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlCodeFirst.cs · IdentityClause`：结构性事实——血统构造直 new 本类，覆写入口本来不存在（三族血统只有 Oracle 留了 CreateCodeFirst 虚口，达梦的 IDENTITY(1,1) 靠它落地）；历史账：五颗叶子逐笔到场逐笔重裁均不覆写（判据：文档明写的分岔才配覆写；瀚高连"裁"都不成立、8c 前件是借来的、PG 本尊问的是版本）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlConcurrency.cs · 类注`：缺口——触发器生成没接线：不配 BEFORE UPDATE 触发器或应用侧赋值就是静默假乐观锁（保存永远成功、冲突永不发生）；"能建、能比、库不替你维护"是四家共有处境而"库为什么不维护"在三条不同的路上（金仓/Oracle 语法里没有那档只能建触发器、MySQL 语法里有没接、第五家连语法都没读到）——共用缺口、不共用重开成本。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlDialect.cs · 类注`：未实测——本类产出文本没有在一台真库上跑过（现场没有授权的 PG 系实例）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlDialect.cs · EmptySetPredicate`：AlwaysTruePredicate 在 Core 里 0 处消费（只有接口声明、基类默认、本行覆写三份）；"合并语句的守卫"是接口注释里写的用途，不是已接线路径——五家同一格缺口。五家里只有这一族覆写了恒假/恒真两颗（1 ∶ 4，4 那边是"没覆写"不是"不支持"）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlExpressions.cs · Replace/STRPOS 模板`：历史账——这两格原本照 T-SQL 旧键抄成 /2 与 /1，于是永远查不到（不是渲染错，是这两个函数在两家都从来没被翻译出来过）；修复与黄金样本同版落地（`golden/sqlserver/string-replace-and-indexof.sql`、`golden/kingbase/string-functions.sql`）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlExpressions.cs · 类注`：str.length 的两家差（T-SQL 的 LEN 不计尾部空格、LENGTH 计）在第五家同名函数落在哪一侧没有出处——记"未知"那一格，不跟着涨。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlProviderBase.cs · FamilyCapabilities / Bulk`：Bulk_Copy 已接线未见证——反射装配没有集成册读数（活 PG 实例在场而 L1 路径未跑过，按"已接线、未见证"读）；借臂叶子 fork 里没有导入器时整家退 L3，那一种现场位报的是"代码可达而这一支驱动没有"（与 SqlServer 被裁剪驱动同档处境）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlProviderBase.cs · ClassifyIntegrityError`：实测口径（PG 18.6，pg_temp 夹具五违例各撞一次）——非空那一档库不给约束名（本来就没有命名约束，给的是列名与表名）；外键的插入侧与删除侧同报 23503，分岔只在文案（23001 是 ON DELETE RESTRICT 档、同处置）；DETAIL 在默认连接串上被驱动整段涂掉（"Specify 'Include Error Detail'"），"从文案里取键列"在生产连接串上根本没有材料——键列要么按约束名回契约查（还没接），要么不给。金仓/openGauss/瀚高/GBase 跟着这份走依据的是声明继承，不等于验过；跑错就在叶子上覆写。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlProviderBase.cs · Catalog`：库名键 Database/dbname/db 是 Npgsql 文档值——没有驱动可测，只列到文档写到的那一格，别当成实测读数（与 MySQL 血统实测钉下的两颗不同源）。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlSchemaReader.cs · IndexesSql`：推断——WITH ORDINALITY 要 9.4，`unnest(i.indkey::int[]) WITH ORDINALITY` 在 9.2 内核那一档可能是语法错（厂商行没写这一句，没有授权实例可问；没有读数不换写法——换 generate_series 要连累列序序号一起重写）；唯一约束位不用 conindid 判（那一颗要 PG 12.1+，叶子有 9.2 内核的），按"名字对名字"判。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlSchemaReader.cs · StoreType`：旧伤——裸 NUMERIC 从前无条件 GetInt32，读到一颗不带括号的 numeric 就抛（"不限定"与"0"不是一回事），已补 IsDBNull。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlValues.cs · BareOrOffset`：实测坑——Npgsql 6 起按 DateTime.Kind 分型：Kind=Utc 只肯进带时区那一型，写进裸 timestamp 在绑定当场抛（Cannot write DateTime with Kind=UTC to PostgreSQL type 'timestamp without time zone'）；现场是 timestamp without time zone 列族 + ABP 时钟交 Kind=Utc 钟面，症状是宿主启动就死、一次登录写一条安全日志就 500 而库里一个字节都没进去（旧底座 SqlSugar 发自己的参数对象，这一格从来量不到）。裸列摘 Utc（SpecifyKind(Unspecified)）是逐字节同文；换 Local 才是数据改写。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlValues.cs · InlineNumber`：非有限值——这一家有 'NaN'::double precision 那种"带引号再 cast"的写法，但裸 NaN 会被解析成列名，且没有"哪种设置下都成立"的读数，宁可不内联。
- `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlChangeGuard.cs · Compares`：Json_Type 位已撤声明（映射产不出 json 列，位就没有消费者）——真实现单独一批落地时随位一起重开；在那之前关 json 是护栏不是现状。
- `src/QuickSql.Providers.Kingbase/KingbaseProvider.cs · DriverKey`：assembly 按厂商文档填 Kdbndp，现场也可以注 Npgsql——两条都走同一份注入路径，本程序集编译期都不认识（未实测行）。
- `src/QuickSql.Providers.OpenGauss/OpenGaussProvider.cs · 类注`：推断与未实测——"9.2 内核不认 IDENTITY"是内核代数推断非厂商读数，按"文档明写的分岔才配覆写"不覆写，真实例到场第一问就是它；RETURNING 在哪些列约束下受限没有任何见证（厂商行只给了半句）；分布式 DN 序列不连续【推断】而更甚；assembly 填 Npgsql 是因为厂商分发的是 Npgsql 的 fork 而 fork 的托管程序集名没有可引读数（不猜名字；金仓行的 primary Kdbndp 不共享过来）。已适配三句立不全：驱动从未解析、覆盖面只有文本层、无授权实例。
- `src/QuickSql.Providers.GaussDb/GaussDbProvider.cs · 类注`：更薄一档——GaussDB（华为企业版）与 openGauss fork 的关系、自己的托管驱动叫什么、A/B 两种部署形态在方言假设上分不分叉，今天没有任何可引读数（不新立推断）；真实例五问清单第一问之前要先问"这台库与 openGauss 是不是同一套文本假设"。现场若装的是自己分发的那份，第一件事是按厂商读数改回它自己的程序集名。
- `src/QuickSql.Providers.Highgo/HighgoProvider.cs · 类注`：厂商行坑列只有权限模型与审计策略（都在方言层之外）——IDENTITY 问题对本家不是"裁不落"而是问都不成立（"文档明写的分岔才配覆写"的另一半：没有明写的分岔，连"裁过"都不该假装做过）；真实例第一问="这一家兼容的是哪一代 PG"。瀚高自己分发的托管驱动叫什么没有可引读数，不猜名字。
- `src/QuickSql.Providers.Gbase8c/Gbase8cProvider.cs · 类注`：明写分岔"分片语法差异"落在 CREATE TABLE 的句内子句位而本仓库恰好不发那一格（分片子句不写），所以覆写判据不触发；重开条件=仓库自己哪天长出分片 DDL——那天它立刻从发射面外进到面内。"与 openGauss 高度相似"像到哪一层没有读数（第一问在参照物上：像到内核代数就落进 openGauss 那格的老问题；不猜"8c 就是 openGauss 发行版"）。GBase 自己分发的托管驱动叫什么没有读数。
- `src/QuickSql.Providers.PostgreSql/PostgreSqlProvider.cs · 类注`：未实测——本机没有任何一台 PostgreSQL，Npgsql 这颗名字从未解析成功、整行未实测；它是否公开可下载、什么许可、AOT 友不友好没有一处读数（mysql 行那句"公开 NuGet、MIT、AOT 友好"既不翻案也不背书）。历史账——本尊落地那天的壳数/程序集数自纠（实为第十一层文本层壳、第十七颗 provider 程序集，旧句按"派生挂载者"口径数成九层/十六颗；"本尊算不算一层壳"的口径本笔不裁，两种读数并排）。
- `src/QuickSql.Providers.PostgreSql/PostgreSqlProvider.cs · 类注`：三段区间【推断，本仓库自己算的】——IDENTITY 要 PG 10+ 而三颗 CAST 包装因 14 才不再必要，两句朝相反方向指；本尊具体是第几代没有读数也不猜（不拿"它肯定 ≥ 14"当依据写代码）。它是 PG 血统第一颗"本尊比派生更保守"的叶子：前两族的正典叶都是被对齐的那一侧，本家是对齐别人的那一侧。

---

# Part F：注释精简时摘出的坑位、边界、实测读数与重开条件

> 2026-11-03 QuickSql.Providers.Xugu 与 QuickSql.Providers.Gbase8s 全量注释精简（简短 ///）时，从代码注释里删掉的"展开才说得清"的部分，按项目归档于此。锚为 `文件相对路径 + 成员名`。代码里只留一两句能自立的说明；本仓库统一约定（会话/兼容参数改变方言假设、脚本路径、坐标对账等）的通例不在此重复展开。

### QuickSql.Providers.Xugu

- `src/QuickSql.Providers.Xugu/XuguChangeGuard.cs + XuguChangeGuard`：三段式理由的完整版——本家没有 IS DISTINCT FROM / IS NOT DISTINCT FROM（交 E19260 syntax error, unexpected DISTINCT，期望集 expecting _FALSE or _NULL or _TRUE or _UNKNOWN，即 IS 后只接这四颗）与 NULL 安全等号（E19260 unexpected '>'），三形都是活库正问出的语法错；"布尔不是一等值"那条理由落不到本家头上——三段近似式在真行上双向数得清（BOOL_VALUE=0 ∶ BOOL_INVERSE=2，相加等于靶表两行，两颗列均不可空），F-22/F-23 各交 D=1 与 D=True。三段式按调用点是 4 家三段 ∶ 1 家一句（金仓 IS DISTINCT FROM）。
- `src/QuickSql.Providers.Xugu/XuguChangeGuard.cs + XuguChangeGuard`：Compares 关两颗的改判史——上一版关 text/clob/blob 三颗，按厂商文档改判两颗（TEXT 是 VARCHAR 同义字、带宽度、可比）；"问不出答案"档与 MySqlChangeGuard 的 json 同级。**可达性 2026-11-05 再改判**：建侧无宽度已改发 CLOB（见 XuguTypes 那条），于是"不可比"从"要显式点名才踩到"变成"未声明宽度的 string 默认就踩到"；byte[] → BLOB 与 >60000 字符 string → CLOB 仍是默认路径。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + GetStoreType`：无宽度档 2026-11-05 由 TEXT 改 CLOB——厂商文档明写 TEXT/VARCHAR2/NVARCHAR/NVARCHAR2 是 VARCHAR 的映射类型、效果等同；不写长度只吃行记录剩余空间（行 64K），活库实测（TESTDB）：10,000 字符过，60,000 / 60,001 / 100,000 全拒；CLOB 10 万与 100 万字符写入、回读一致，`= / > / LIKE` 能用，但 `ORDER BY` 与 `CREATE INDEX` 都报 E10008（无比较函数）。tracking 不受影响：Compares 只在"同掩码多行合并成一条 UPDATE"那一支被读（EntityTracker.Mergeable），而虚谷 `SupportsJoinedWrites=false`，那条路本来就不走；变更检测是客户端快照逐列 Equals。迁移注意：旧表上的 TEXT 列与新契约的 CLOB 形状不同，结构同步会把它判成待改列。
- `src/QuickSql.Providers.Xugu/XuguChangeGuard.cs + XuguChangeGuard`：守卫关列的后果今天为零——EntityTracker.Mergeable 先问 Dialect.SupportsJoinedWrites（EntityTracker.cs:860），逐列问 Compares 排在后面（:869）；"守卫关一列 ⇒ 整批退回一句一实体"由方言闸门代跑，守卫自己的 false 只被离线手工构造 AST 的用例量到。这是"不该写成 if (是达梦)"的第三个实例。
- `src/QuickSql.Providers.Xugu/XuguChangeGuard.cs + XuguChangeGuard`：去尾空格不对称账——厂商文档明写 CHAR/VARCHAR 存入去尾，"库里 abc、新值 abc␣"在参数比较上不等（比较发生在写入之前），守卫判"变了"发 UPDATE，写回的规范化值与库里逐字相同——多写一行不丢写。MySQL 那档（CI 排序规则）是丢写，两行都在跨库差异表、优先级不同。本条不在黄金样本里：没有真库能证"去尾发生在参数比较之前"，是从"存入时"三个字读的。
- `src/QuickSql.Providers.Xugu/XuguConcurrency.cs + XuguConcurrency`：rowversion 全账【实测】——CREATE TABLE … ("ID" INT, "V" ROWVERSION) 建得成（TYPE_NAME="ROWVERSION"、SCALE=-1，小写同绿，RV-01a/b/c）；真的会自增（RV-11b…h 链：灌行不带 V → 快照抄 V → 改前两表 V 相等数到 1 → 改一次别列 → 比数到 0 → 二改仍 0）；但值取不回：带它的投影一取行抛 System.DataOutOfBindException（RV-08b/08d/09e 三场同一颗红）；SET "V" = 5 被 E16010 拒、LENGTHB("V") 被 E10049 拒（两号厂商中文文本在裸连日志里是残字，判读不建在文本上）。对照排错：不带 V 的 COUNT(*) 绿（RV-09a=1）、带 V 零行绿（RV-09b，FIELDS=2 ROWS_SHOWN=0）、V 只进 COUNT("V") 绿（RV-09c=1，值非 NULL）。分布改读成"2 家库会给值，其中这一家读不回"；卡点在 SPI 不在 provider。
- `src/QuickSql.Providers.Xugu/XuguConcurrency.cs + XuguConcurrency`：rowversion 后续——"要不要拒收"已裁为拒收（DDL 发射点抛，判据即上条读数）；"能不能拿库给值的版本戳做并发"仍开：等 reader 侧补 DataOutOfBindException 槽位（A-06c）与 SPI 补"排除并发列 + 事后回读"形状。ON UPDATE CURRENT_TIMESTAMP 那格：MySQL 缺的是工程（两步可接），本家缺的是事实（连语法都没读到），重开成本要先有一台库。
- `src/QuickSql.Providers.Xugu/XuguConcurrency.cs + XuguConcurrency`："库给值"的三个出口各缺一个槽位：RETURNING … INTO 要 OUT 绑定（AdoParameter 无方向位）、SYS_UUID()/UUID()/GEN_RANDOM_UUID()/NEWID() 取号函数要列级默认值那一格、IDENTITY_MODE 要会话参数建模——缺的都是形状不是实现。
- `src/QuickSql.Providers.Xugu/XuguConnectorFactory.cs + XuguConnectorFactory`：原生同伴实测账——原生库（xugusql.dll/libxugusql.so）不在探测路径上时 CreateConnection 照样成功、TryEnsureLoaded 交回 true，缺失到 Open 才以 DllNotFoundException（0x8007007E）现身（出处 obj/logs/v32-matrix-bare-open.log）。该反证只对 3.3.4.5 那颗驱动成立；本仓交付的 4.0.0.8 是纯托管一颗，原生层随换版消失。
- `src/QuickSql.Providers.Xugu/XuguKeys.cs + XuguKeys`：RETURNING 三档出处账——【文档】INSERT … RETURNING 列 [BULK COLLECT] [INTO :变量]，示例全在 PL/SQL 块里绑宿主变量；【产物·支持】一家适配器逐字发 " RETURNING "（MERGE INTO、limit…offset…returning 同份产物）；【产物·反对】另一家发完 INSERT 追 ";SELECT LAST_INSERT_ID();" 并按 "ROWID" 重查回填——最硬的一档指向"语句后回读"。多行 VALUES…RETURNING 回几行无任何产物能证；无 INTO 形只在单行路径有产物出处，批量从未被用过。
- `src/QuickSql.Providers.Xugu/XuguKeys.cs + SupportsBulkKeyRetrieval`：true→false 的改判史——原判"红在被库驳回"被实测换掉：N-06 单行与 N-07 多行两形全空，执行成功、行进去了、回包 FIELDS=0 ROWS=0（obj/logs/v32-recon-keys-1.log），比驳回更险的是"绿着交出 0"；ReadBackAsync 的 0/N 行数判把红钉在当场。N-09 交回 Int64；T-03/T-04/T-01/T-02（v32-recon-txn-3.log）证多语句批 INSERT…;SELECT LAST_INSERT_ID() 跑通、答的是本会话刚插那颗、事务内同样成立。
- `src/QuickSql.Providers.Xugu/XuguKeys.cs + SupportsPostStatementKeyRead`：带并发列的回填红点——RETURNING 交空结果集与 SELECT "VERSION" 的 DataOutOfBindException（v32-recon-mat-4.log A-06c）同记并发那颗契约格。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + XuguDialect`：分页实测账——S-10 交 SELECT 1 LIMIT 1 OFFSET 0（不带 ORDER BY 的两子句形在真库收），S-11 交 SELECT TOP 1（两支都能跑，取哪支是取向）；F-46/F-46b 问穿另一支：OFFSET 0 ROWS FETCH FIRST 3 ROWS ONLY 交 E19260 syntax error, unexpected OFFSET。产物里也认 TOP n 与逗号形 LIMIT 偏移,条数（顺序与阅读方向相反），两支都没取。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + XuguDialect`：布尔字面量与恒真/恒假——S-13 SELECT TRUE 交 True（Boolean）、S-14 CASE WHEN TRUE… 交 "YES"（当值与当谓词两问都绿）；有出处仍不覆写：基类那两颗（1=0/1=1）四家共用、只有金仓整对改写，且实测对谓词没有收益（恒假在两形下同样不匹配任何行）。共用层一格空白（五家同岗）：EmptySetPredicate 在 Core 有 1 处消费者（ExpressionTranslator.cs:456，空集合 IN () 特化），AlwaysTruePredicate 调用点 0 处（grep 三行：接口声明、基类默认、金仓覆写）——缺口记 §14。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + XuguDialect`：目标起别名的风险不对称论证——Oracle 血统"目标不能起别名"有文档背书，本家只有"两份产物都没写"；若本家其实允许，不发别名代价为零；若不许而我发了，代价是每条更新都红——取错不起的那一边。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + XuguDialect`："跑过"的三句边界——本层产出文本在真库执行过（95 条用例 + golden/xugu/ 六份由探针逐句执行）不等于本族每条文本都执行过（键回填红着按名跳过），也不覆盖"现场驱动是否交付入库的那一颗"。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + NoLimitSentinel`：改判史——上一版此处抛（"没有可抄的哨兵"），改判依据：厂商文档直接给了 LIMIT 取值范围 1..2147483647；"语法要求 LIMIT 在场"与"LIMIT 能吃多大"是两问。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + ParameterPrefix`：出处薄账——产物那枚 :XuguParam 是参数名不是 CommandText 占位符；同一份产物还为 RETURNING、IDENTITY(1,1)、CHAR(36) 与目录视图名字各签过一次（盖得最薄的一格，前缀错了红在每条语句）。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + EmitSavepoint`：保存点探针账【实测，obj/logs/v37-xg-recon-savepoint-1.log】——XuguClient.XGTransaction 只覆写无参 Rollback()，带名字三颗停在 DbTransaction 基类、调即 NotSupportedException（Q-02x/Q-03/Q-04）；文本三句同库收且语义正确（Q-08 裸名；Q-11/Q-12/Q-13 CNT=1、ID=1、TAG=before；Q-14、Q-15 剩 1 行）；Q-09 把 SAVE TRANSACTION 问死（语法错），没有"照 SQL Server 抄"的余地。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + EmitUpdateTarget`：成对覆写是测试切片抓出来的——provider 层只覆写了一半，直到 XuguDialectTests 把本族 UPDATE 与达梦那份逐字对读才显形（"UPDATE 表 AS 别名"那一形状在两家产物都没出现过）。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + EmitJoinedSources`：行集实测账——F-20 发 SELECT * FROM (VALUES (1,'a'),(2,'b')) AS t (id, nm) 交 E19260 syntax error, unexpected VALUES；F-19 发 MERGE INTO … USING (SELECT 1 AS "ID") S ON (…) WHEN MATCHED THEN UPDATE SET … 执行通过（AFFECTED=0，ON 没配上行，语法与执行两级都过）。将来接的是 MERGE INTO 不是 UPDATE…FROM；把行集翻成 USING (SELECT … UNION ALL …) 是另一版的活。
- `src/QuickSql.Providers.Xugu/XuguDialect.cs + Emit`：InsertOrUpdate 未复点账——MERGE 骨架在活库跑过的是单行手工 SELECT 1 AS "ID" 那颗；本层产出的多行 UNION ALL ∶ 目标别名 t0 ∶ InsertOrUpdateEngine 拼的整句，三处增量都没在那台库（192.168.0.228）复点过。重开条件：连那台库把 InsertOrUpdate 跑一次（集成册补一场）。
- `src/QuickSql.Providers.Xugu/XuguProvider.cs + XuguProvider`：程序集名 XuguClient 从未在一台机器上解析成功；厂商参考页写有 XGProviderFactory.Instance（工厂类名有出处，程序集名无——文档给的是驱动包名，与托管程序集名是否同名没核过）。
- `src/QuickSql.Providers.Xugu/XuguProvider.cs + XuguProvider`：能力位台账——7 位 = 7 离线文本 + 0 仅文档；五家交集仍 3 位（Schemas、Comment_On_Column、Alter_Column_Precision 三颗全有），"新增一族不必然缩小交集"的例证：交集只在共用层新增发射路径时才变。
- `src/QuickSql.Providers.Xugu/XuguProvider.cs + XuguProvider`：Boolean_Type 不声明的三条理由与读数——①形状一致（列整数/谓词整数/字面量整数）；②声明位等于把谓词推到 IS TRUE（BT-02 布尔列数到 1、BT-11 整数列数到 1——原"有没有 IS TRUE 没出处"被驳回）；③代价可数（BOOLEAN 列读回 CLR Boolean，BT-04；SELECT "B" 交 True）。v0.35 裁决：位只跟存储型走，bool 落 TINYINT 期间永不声明；CHARSET 裁成"跟着会话，工厂一生不碰 CHARSET="（非 GBK 实例上的 LENGTH∶LENGTHB 探针是唯一重开触发）；共同重开触发 = "全新虚谷库复跑 + 家族专属夹具模型"那笔欠账。
- `src/QuickSql.Providers.Xugu/XuguProvider.cs + XuguProvider`：IDENTITY_MODE——文档给名字与用途、语法没抄；形状是"改会话"不是"发一条语句"，SPI 的 IdentityInsertToggle 口子接不住，Core 也没有调用路径。Upsert_MergeInto——两份产物都发过 MERGE INTO…USING…ON 而 src 一行没发过，三颗里离"能接"最近（缺的是 (VALUES…) 行集能不能带进 USING，见 XuguDialect.EmitJoinedSources）。Json_Type 改判——类型页有 JSON 档，Json<T> 在 src 0 处（"文档推翻我们写过的话"三处之一，另两处：OFFSET 哨兵、text 守卫）。WindowFunctions 待复点——QuickFx.RowNumber 落 DialectBase.EmitRowNumber 有共用发射口，本家实例在场而那一句没发过（金标 golden/xugu/fx-rown-*）。Guid_Raw16 与 CHAR(36) 是两档出处各支持一支的二选一。
- `src/QuickSql.Providers.Xugu/XuguProvider.cs + Bulk`：本家装载面一页手册都没读到（比"读到了但口径不合"还薄一层）；驱动程序集解析成功过而 provider 文本从没在它那儿跑过；重开条件与 Oracle 同一条尺——先有能读出"行数口径"的装载 API。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + XuguExpressions`：出处四档体系——【文档】官方说法会漏会过时；【产物】"有人这么发过"不是"这台库收过"，日期加法那批就是产物被实测驳回的实例；【推断】逐位进台账"未实测"列（【推断·偏离产物】两颗：Blank 与 Coalesce，判据同一条——产物优先的前提是产物做的是与共用层同一件事）；【实测】压在前三档之上（TRIM 三形/SUBSTR/GREATEST/LEAST/REPLACE/COALESCE/NULLIF/ADD_MONTHS 印证绿；日期加法驳回红）。"血统"在模板表上站不住：日期部分/SYSDATE/DUAL/INSTR 四参落 Oracle 族，CAST(… AS date) 落 T-SQL 多数派，GET* 六颗与 CAST(… AS NVARCHAR) 是第五形；日期加法实测后归 NUMTODSINTERVAL/ADD_MONTHS——归哪一家由这台库说了算。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + XuguExpressions`：agg.average 整数截断与否仍未量——这一版问的都是逐行表达式，聚合无真库旁证；重开条件是"聚合下推那一期接它"。SupportsRegex 三档无出处（产物没发过正则函数、文档没读到对应页）。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + ConcatOperator`：读数账——F-01/F-02 交 "甲乙"；F-53 真行双向数（("S"||NULL)="S" 数到 2、IS NULL 数到 0，相加=行数），CONCAT("S",NULL)="S" 同数 2——运算符与函数同侧（MySQL 两形不同侧）；NULL 语义无共用槽位，进跨库差异表。本格没有消费方（拼接一律走 CONCAT 模板），探针跑的是探针那句不是本仓库产出的句子。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + LikeEscapeCharacter`：读数账——F-51 真行（a_b/axb 两行）交 LIT_BSLASH=1、LIT_BANG=1、LIT_WILD=2、PAR_BSLASH=1、PAR_STARTS=1；F-51g LENGTH('a\_b')=4 与 LENGTH(:p)=4 排除"反斜杠被字面量解析器吃掉"；F-50 常量比常量交六颗 0，一度被误读成"收下 ESCAPE 而不理它"——判 LIKE 语义要扫真行（探针纪律）。分布四家反斜杠 ∶ 一家波浪号（MySQL 因 sql_mode）。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（Blank）：EMPTY_STR_AS_NULL 参数无读法——all_variables/props 四形候选全报"表或视图不存在"（N-01 段）；换实例部署要重跑 E-10f/E-10g 那一对（IS NULL 与 = '' 两数相加 = 1 才说明默认档还在）。"会话/实例参数改变方言假设"实例清单三处各写一份不同名单没签过全量（EMPTY_STR_AS_NULL 只在本文件、NLS_LENGTH_SEMANTICS 只在 OracleTypes/DamengProvider）——合并成一份可数清单记 §14。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（IndexOf）：中文子串 utf8/plain 连接命中分岔（N-02 两处并排）——分岔的是 CHARSET 不是 INSTR，与 Fn.Length 同因同行。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（CastText）：空结果集读数账——BT-42d/e 裸形 40000 字不截（BT-40e/f 先在 30/60/200 三档同答）；带宽度窄于值长时五颗靶全复现空结果集（BT-43c/f/g、BT-44d/e），COUNT(*) 交 0 钉死"结果集空掉不是行被滤掉"；等宽与更宽正常（BT-44f/g、BT-43e 各 1 行）。"哪家在 CAST 上会静默丢行"是跨库差异表新的一行。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（DateSecond）：读数账——E-05a/b GETSECOND 与 EXTRACT(second…) 各交 59.456（两形都带小数秒）；E-13a SECOND 交 9（Int32）；F-15 字面量早已量到 SECOND 交 59；E-13d MINUTE/HOUR 裸名同值（GET* 与裸名两形都有，仍跟有出处的 GET*）；E-13e SECOND 打在 DATE 上给 0 不报错。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（Now）：S-06 交 2026-09-24 21:35:26.4440000——SYSDATE 带毫秒与 DATETIME(3) 对齐，"SYSDATE 精度"缺口在本家闭合，仍开在 Oracle 血统那一族（一处实测只结一家一格，别跨族推广）。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（UtcNow）：E-06a/b 证实两颗同时成立，交回 DateTime 且与本机差正好是会话时区 GMT+08:00；SYSTIMESTAMP 单发红在驱动侧 System.OutTypeException"未支持的列转化类型"——库侧那型在场、缺的是绑定读回路，给 DateTimeOffset 的抛补独立旁证。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（AddYears…AddSeconds）：E-07 读数账——六颗单位逐行交 E19260 syntax error, unexpected _YEAR, expecting ',' or ')'（解析器在 INTERVAL 后不接受括号）；DATE_ADD(x, INTERVAL 2 DAY) 与 INTERVAL '2' DAY 绿而乘数为表达式/参数/列名就红；NUMTODSINTERVAL 四档全绿（E-11b…f），HOUR/MINUTE/SECOND 掩码各自单独绿；ADD_MONTHS 三档全绿、真 DATETIME 列时分秒原样保留（E-12d）。产物那两本写法今天分不开是"兼容模式档位不同"还是"本来就是坏的"——想问出兼容档，五颗候选（all_variables/props/all_props/all_db_info/CHARSET()，N-01c…f）全报"不存在"，all_databases 32 颗字段里没有兼容位——标"问法没有"而不是"没问"。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（Round）：E-01h 包装形绿但换值——ROUND("AMOUNT",2) 交 "12.35"、ROUND(CAST(… AS numeric),2) 交 "12"：裸 numeric 把标度折到 0，包装是静默改值；与金仓"不包就报错"同形而后果相反。ROUND/ABS/CEIL/FLOOR 结果列经驱动读回是 String（"12.35"），POWER/SQRT 是 Double——集成层今天没有链把 decimal 函数结果直接读进 DTO 所以不红。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（Greatest）：F-07 交 G=2/L=1；F-07b GREATEST(NULL,2)=NULL——NULL 传染与金仓/Oracle 同侧、与 MySQL 相反；敢用常量折叠是因同库同路常量折叠有旁证（F-43d/e、F-18），若翻的后果是"两形给不同的数"能一眼看出，不是静默少行。
- `src/QuickSql.Providers.Xugu/XuguExpressions.cs + Build`（Concat）：F-02 交 CONCAT('甲','乙')="甲乙"——"最不可能错"兑现的是函数在场；同段路 || 也在场且 NULL 语义同侧，本格无重开岔。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + XuguTypes`：四档出处体系与限度——【文档】第一次可被产物推翻或印证；【产物】两家适配器互相矛盾处比一致处多；【推断】已清零（DATETIME(3) 精度限定词被目录读出）；【实测】只覆盖本族自己发出去的句子，"跑过"不等于"已适配"。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + XuguTypes`：宽度量纲实测——"S" VARCHAR(4) 灌 'abcde' 交 E19230 字段(col_no:0,col_name:S)值错误(数据异常/字串超长)，灌 '甲公司' 在 CHARSET=UTF8 连接上存进去（同一串两种字符数）。"报不出第二个数"收窄成"报不出某一列的字节预算"：all_databases.CHAR_SET 真库答 "GBK"（四库名逐行），LENGTHB('甲公司')=6 量得到——有字节这把尺，只是量值不量列。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + MaxVarcharChars`：列级闸门实测——VARCHAR(60000) 建得成、VARCHAR(60001) 被 E19258…60000 拒（BT-40a/b）；建成列目录报 TYPE_NAME="CHAR" SCALE=60000，60000 小于 65536 基所以不与"精度×65536+标度"撞车。行级 64K 预算无槽位承接（与 MySQL 并排记 §14）。TEXT 绑定侧另一道闸：40000 进、60000 被 E17111 拒（BT-41b 梯子 1000/8000/32767/40000 绿、60000 红），边界在 40000 与 60000 之间，与列闸无关。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + GetStoreType`（bool）：BT 族读数——WHERE "B" = 1 数到 1（BT-03）；IS TRUE 在 BOOLEAN 列与整数列上都合法（BT-02/BT-11）；SELECT "B" 交 True（BT-04）。"改 BOOLEAN 列型零行为收益、造新旧库分叉"也是裁决之一。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + GetStoreType`（DateTime）：BT-20/BT-30 读数——同串 …123456 灌 (3)/(6)/裸三列，谓词判据下 (6) 与裸列各数 1、(3) 数 0——(3) 是真的截；DATETIME(7)/(8) 建表被拒同句 E2051…6，裸写默认 6，目录 SCALE 是限定词回声（3→3、6→6、裸→6）；TIME(3) 那颗仍是文档级（没被任何真发过的 DDL 带进去）。驱动读回只到毫秒（BT-20h：三列 GetValue 全交 …1230000）——与建表那格无关，两档分开记。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + GetStoreType`（DateTime）：TIMESTAMP 同型读数——建表收下、目录 TYPE_NAME 报 DATETIME 走同一标度槽（BT-08），同串并读一字不差（BT-07），2040/1960 都进且不挪小时（BT-06/09/10）——"避开 MySQL 包袱"那句降级，留下的只是"与目录同名"。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + GetStoreType`（DateTimeOffset）：BT-05b 把 TIMESTAMP WITH TIME ZONE 建出来了【实测】——只核到"建表接受"，读写语义没问；缺的确实是绑定路。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + GetStoreType`（Guid）：两家 shipped 适配器一致发 char(36)【产物】，厂商说 GUID 是 16 字节 uuid【文档】；取 CHAR(36) 后想退回文本形仍够得到（[Column(TypeName="GUID")] 在第一行短路）。
- `src/QuickSql.Providers.Xugu/XuguTypes.cs + StoreToClr`：驱动表没有 VARCHAR 这一行——是"表不全"（同样没有 VARCHAR 却给了 CLOB）还是"库把 VARCHAR(n) 改名后存目录"（MySQL tinyint(1) 先例），两解释今天分不开，归"有真库第一天要重读的清单"。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + XuguSchemaReader`：ALL_COLUMNS 有 27 颗字段（S-09 数的）只读六颗；IS_SERIAL 与 COMMENTS 两颗取值形已量到（M2-03 19 行：布尔 IS_SERIAL=False、COMMENTS=NULL）而 SchemaColumn 无槽位——doctor 读不出"是不是自增"与"列注释"，后者是对账里注释唯一能落地的地方；"库侧读得出、我们侧没槽位"。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + XuguSchemaReader`：COMPATIBLE_MODE 折叠层——POSTGRESQL 兼容档目录小写，本台问不出（五候选读法 N-01c…f 全报"不存在"，all_databases 32 颗字段无兼容位、有 CHAR_SET="GBK"、TIME_ZONE="GMT+08:00"，N-01b）；那一档上读成"这张表不存在"是读不到不是读错；要问得换厂商工具或配置文件，不在只读授权内。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + XuguSchemaReader`：字符三型同存 "CHAR" 的三分账——VARCHAR(9)=CHAR+True+9、CHAR(7)=CHAR+False+7、TEXT=CHAR+True+-1；VARYING 不带引号读不出（E19260 unexpected VARYING，M-02）；目录里只有一个数而有三个身份（宽度/打包精度标度/时刻精度），判身份靠型名。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + XuguSchemaReader`：同义字账——INT 与 TIMESTAMP(3) 建出去目录回 INTEGER 与 DATETIME（M2-01 第 2/16 行）；INTEGER 折回 INT，TIMESTAMP 不折（同义只有一颗样本说话），后果是手工建的 TIMESTAMP 列 doctor 报成 DATETIME——可指认、方向已知的差。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + XuguSchemaReader`：LENGTHB('甲公司')=9 对 LENGTH('甲公司')=5（F-10/F-11）——有字节尺子、量值不量列。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + PackedBase`：拆包读数——NUMERIC(18,4)→SCALE=1179652、(20,0)→1310720、(5,2)→327682；初稿 Unpack 把商余对调（给 NUMERIC(18,4) 还回 NUMERIC(4,18)），"60000 与 65536 相邻可能只是巧合"一并改判：65536 是基，空档是构造的必然。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + ProbeLengthSemanticsAsync`：旧写法 width ?? packed.Declared ?? -1 的假读数——DATETIME(3) 探测回 DECLARED=3、INTEGER 回 -1；改后 CREATEDAT 回 -1/-1、CUSTOMERNAME 仍 64/64。CharSemanticsProven 不许在改判时偷偷升"实测"档（金仓那格说的也是同一件事，按类型名给）。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + StoreType`：目录 COLLATOR 字段（27 颗之内、实测 NULL）不拿填 CollationName——null 与空串同一次读里同信息，哪列会有值没量过。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + Fold`：目录大写实测——不带引号建的 QuickSqlRecon33 存 "QUICKSQLRECON33"（K-07）、带引号小写存小写（K-10）；模式 'sysdba' 0 行 'SYSDBA' 19 行（M2-07 对 M2-05）；默认支与建侧核过（QUICKSQLORDER 落 SCHEMA_NAME='SYSDBA'=USER，M-05b）。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + Parameter`：E10036 未绑定变量 TBL——带冒号的名字红、摘掉同句绿（M-01 绿/M-03 红；catalog-2 的 K-16/K-18/K-19 与 D-14 四红同因）；名字不能叫 table（E19260 unexpected TABLE，K-02b/K-03）；带 @ 的写法连文本都不认（B-03/B-04）。主链路摘前缀在驱动边界（AdoExecutor.Bind），目录读侧绕不开必须再写一遍——改一处不改另一处留"主链路绿、doctor 红"。
- `src/QuickSql.Providers.Xugu/XuguSchemaReader.cs + ReadTableAsync`：COL_NO 从 0 起数（M-01 八行 0…7，另四族 1 基）——折 1 基是造假。
- `src/QuickSql.Providers.Xugu/XuguValues.cs + XuguValues`：转义字符悬空句——文档说"或用转义字符"没指明是哪颗，与 MySqlValues 那格同一重开条件（谁接了会话模式建模两边一起重看）。布尔 TRUE 改判史：原设计稿取 TRUE（"驱动登记了 bool 型"），按三处一致性改 1/0。EnumAsIntegral 的 4∶1 账：SqlServerValues 内联 Convert.ChangeType 是判据反例，抄判据的是 PG/Oracle/MySQL/本家——"抄来源连例外一起抄"。HEXTORAW 判据：入参只有十六进制一种解释才可内联；'\x' 前缀在 EMPTY_STR_AS_NULL 与未指明转义符下各读成什么两边都没说。
- `src/QuickSql.Providers.Xugu/XuguValues.cs + InlineNumber`：1E-08.0 是语法错——修复后形状被抄进 T-SQL 与金仓两族时各撞出来一次（"第二个方言族才把第一族的错照抄出来"）；本家 FLOAT/DOUBLE 列上 3 与 3.0 是否同档隐式转换未实测。
- `src/QuickSql.Providers.Xugu/XuguCodeFirst.cs + XuguCodeFirst`：表尾选项实测——ENGINE = INNODB 交 E19260 unexpected IDENT, expecting end of file；DEFAULT CHARSET = UTF8 与 CHARACTER SET UTF8 同句式（N-08 三颗，v32-recon-cjk-plain-3.log）——建表语法在列清单后收尾，表级选项整段不存在；"只有一个引擎"仍是厂商侧解释、留在文档档之外。字符集替代在连接串（CHAR_SET/CHARSET=），"默认映射到宽的一侧"不由 DDL 保证——同一份建表文本在 UTF8/GBK 连接上建出字节宽度不同的列。
- `src/QuickSql.Providers.Xugu/XuguCodeFirst.cs + XuguCodeFirst`：ALTER COLUMN 出处最薄——文档没读到语法页、产物没有改列文本；取最保守形（重述可空性两种库行为都不出错，不重述在 T-SQL 形是静默改约束）；真库第一天要跑的第一条就是它。模式为 null 不写限定：产物只证"没人写过限定"不是"限定不许"；落在哪个模式由连接串决定。
- `src/QuickSql.Providers.Xugu/XuguCodeFirst.cs + XuguCodeFirst`：IdentityClause 遗留账——MySqlCodeFirst.cs:61 与 PostgreSqlCodeFirst.cs:30 两颗 virtual 无覆写者（v0.36 复点：MySQL 系挂 TiDB/OceanBase、PG 系挂 openGauss 后三颗裁"不覆写"；PG 那颗连覆写入口都没有——PostgreSqlProviderBase 构造直 new，CreateCodeFirst 缝只有 Oracle 留了；"零覆写者"要读成两种）。
- `src/QuickSql.Providers.Xugu/XuguCodeFirst.cs + XuguCodeFirst`：索引账——BITMAP 在契约 IndexKind 没有那颗（"有第四种索引而契约没格子"与 WM_CONCAT 同批）；FullText 是三份"抛"里第一家有文档正面依据（另两家是 PG 访问方法、Oracle CTXSYS.CONTEXT 域索引——那一族有全文检索只是不长在这颗关键字上）；ASC/DESC 无独立出处而产物都带方向。
- `src/QuickSql.Providers.Xugu/XuguCodeFirst.cs + EmitColumnDefinition`：rowversion 判据全部读数——RV-01a/b/c 建得成目录读 ROWVERSION、RV-11d→f 会自增（v32-recon-rvbool-5.log）、RV-08b/08d/09e 与集成层 A-06/A-06c 读即 DataOutOfBindException（v32-recon-mat-4.log）；抛点选 Definition 不选 GetStoreType（后者有非 DDL 读者）。

### QuickSql.Providers.Gbase8s

- `src/QuickSql.Providers.Gbase8s/Gbase8sChangeGuard.cs + Gbase8sChangeGuard`：三段式两个口径——按调用点 5∶1（T-SQL、Oracle 血统基类、MySQL 血统基类、虚谷、本家 对 PG 的 IS DISTINCT FROM）；按 provider 8∶5（IS DISTINCT FROM 被 PG 血统五颗叶子共用）。第一次能按 provider 数这一判；只报一个数会被下次改动读成"分布变了"。本家是负向理由（语句清单没有一句形候选），布尔是不是一等值没问过（未逐页回读）——少数"换一台库就可能改判"的档位，重开时与一句形同批问。
- `src/QuickSql.Providers.Gbase8s/Gbase8sChangeGuard.cs + Gbase8sChangeGuard`：TEXT 读了另一档——与 LVARCHAR 同族带上限（手册未逐字回读，上限数字不写）；可比方向与虚谷一致而出处薄一层（那里两档产物，这里一句手册）。本家关三颗是"文档没写、产物没有、连活库都没有"，比虚谷再降半格。
- `src/QuickSql.Providers.Gbase8s/Gbase8sChangeGuard.cs + Gbase8sChangeGuard`：CHAR 去尾的反向风险无从谈起——没读到"存入去尾"那条（不是更安全，是没读到）；拿到库时两颗（去尾与一句形）同一批问。守卫 false 的后果为零（第六次，方言闸门代跑）；本家是第一颗翻不回 true 的闸门——语句清单里没有 UPDATE…FROM 这一形，接它要发明把行集接进 UPDATE 的写法，那是 WHERE EXISTS 等价路不是这颗开关。
- `src/QuickSql.Providers.Gbase8s/Gbase8sConcurrency.cs + Gbase8sConcurrency`：无 rowversion 的论证——存储型可按名字穷举（无扩展型、无 CREATE TYPE 之外的隐藏档），"没读到"与"没有"的差距比虚谷小但仍是差距；六家分布 1 库给值 ∶ 5 应用给值。列定义能挂的子句读到 DEFAULT/CONSTRAINT（非空唯一引用 CHECK）/COMMENT IS，无"每次 UPDATE 自动重写"子句；DEFAULT 只在插入未给值时生效。
- `src/QuickSql.Providers.Gbase8s/Gbase8sConcurrency.cs + Gbase8sConcurrency`："库给值"两颗表达式（CURRENT 时刻、DBINFO('utc_current') 整数秒）各有缺口——StoreFillsValue 只配"值随 INSERT 进 RETURNING"那条路而本家那条路不存在（EmitReturning 抛）。位置形顺序耦合与 EmitFunction 的"0,1,2… 各一次"闸门（v0.37，Core）量的是同一件事的另一半。
- `src/QuickSql.Providers.Gbase8s/Gbase8sConcurrency.cs + Gbase8sConcurrency`：精度缺口——版本列落 DATETIME YEAR TO FRACTION(3)（毫秒）而 CLR 带 100 纳秒刻度，同毫秒两次保存冲突判"没发生"；另五家同样成立（T-SQL datetime2 精度由列给、MySQL 要显式写小数位）——共用层缺口，契约没有"版本列精度"格。超出的位数舍入还是截断没读到那一页，只说"读回可能与写入不同档"。
- `src/QuickSql.Providers.Gbase8s/Gbase8sConnectorFactory.cs + Gbase8sConnectorFactory`：程序集名 GBase.DataDB 只来自内建表一行第四列，从未解析成功；连"官方发布名两档出处"都没有（XuguClient 那格已被实测翻成同名，本行没有那半格旁证）。Informix .NET 提供者历史上要 ifxglu.dll（IfxDateTime 族类型，未逐页回读），GBase.DataDB 是移植还是重写无读数——TryEnsureLoaded 只是探测口。
- `src/QuickSql.Providers.Gbase8s/Gbase8sKeys.cs + Gbase8sKeys`：RETURNING 只活在 ESQL/C 的 INSERT…RETURNING…INTO :宿主变量 与 SPL 局部变量两支【文档·IBM】；无 INTO 形在 CLI 侧收不到结果集；六家分布 2 发 ∶ 4 抛，本家是抛的四家里离"能接"最近的（表达式与语法都有、缺的是绑定方向）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sKeys.cs + EmitPostStatementKeyRead`：DBINFO('sqlca.sqlerrd1') 交回本会话最近一条 INSERT 由 SERIAL 生成的值【文档·IBM】；sqlerrd1 是 sqlca 结构里 32 位槽，读 SERIAL8/BIGSERIAL 会静默丢高位——本文件唯一"选错不会红而会错"的格。systables WHERE tabid=1 是事实上的 SELECT-1 表源（无 DUAL）；目录表名不带引号与"不带引号折小写"恰好一致。
- `src/QuickSql.Providers.Gbase8s/Gbase8sKeys.cs + SupportsPostStatementKeyRead`：前提未证账——Core 把回读句接在同一条命令文本尾巴上（一个分号），"一条命令文本放两句 SQL"本家收不收没读到（MySQL 血统与虚谷能发、各有真库见证，虚谷是 T-03）；错的话红在每一条带回读的开销上，不红在少回填键。第一台活库第一件事问这一句。
- `src/QuickSql.Providers.Gbase8s/Gbase8sKeys.cs + SupportsExplicitIdentityInsert`：SERIAL 写显式值默认允许（写进去的值落序列之下、随后从它上面续）【文档·IBM】；真缺口是"写显式值后序列下一颗要不要跟着挪、由哪格最大值还是本会话决定"没有读数——IDENTITY_ExplicitInsert 那档在别家都要面对（T-SQL 明确会挪），本家连"会不会撞"都答不出，整格记 §14。
- `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs + Gbase8sDialect`：分页参数序耦合——位置形 ? 下限定词在句首使 FIRST/SKIP 两颗参数比投影与谓词先登记，编号序与文本序恰好同向是"槽位选在句首"顺手保住的，不是设计出来又验证过的；反过来（尾巴形分页+位置形）已不成立（ORDER BY 里的参数先登记）。第一处"分页形状与参数绑定次序有耦合"的记录。
- `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs + ParameterPrefix`：位置形唯一一支（预编译语法里 @p0/:p0 都不存在，主机变量是 ESQL/SPL 层的 :var）；前缀/配对错了红在"值绑定到隔壁列"（位置形没有第二道校验）。DriverAcceptsPrefixedParameterNames 保持 false 而今天惰性——给驱动的名字（p0）在位置形绑定里不参与配对，取值不影响任何行为。
- `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs + Gbase8sDialect`：无 N' 是"那一形压根不存在"（codeset 建库时定）——与前几家"有而不用/有且用"含义不同。表别名无 AS 与列别名不通用（SELECT x AS y 合法常见，只省 FROM 支）；三家各按一档出处写（文档∶文档∶产物）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs + Emit`：upsert 三处未证——①多行即分号批而 SupportsMergedStatements 默认 false 无读数（WHERE EXISTS 形状天生要两条一起走，退不开，除非引擎改两条往返）；②目标不起别名；③同键三处（UPDATE WHERE、INSERT 的 SELECT 清单、NOT EXISTS）各落一颗 ? 各登记一颗同值参数，语义对而参数偏多——没 CTE 可共享行集的代价。重开触发：连一台活 GBase 8s 跑绿这一对（尤其分号批）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs + EmitPagingQualifier`：不补哨兵的分布——2 家补哨兵（MySQL 2^64−1、虚谷 2147483647）∶ 4 家不需要（PG 血统是"语法允许光写 OFFSET"，本家是"缺省支本身就是无上限"，同判而来路不同）；两数反着写（SKIP 前 FIRST 后）是语法错。
- `src/QuickSql.Providers.Gbase8s/Gbase8sExpressions.cs + Gbase8sExpressions`：位置形硬判据的两类牺牲品——str.blank（TRIM 形同一实参出现两次：文本两颗、表里一颗，绑到隔壁列；不注册则 IsNullOrWhiteSpace 交 Untranslatable；干净路"按文本序把同一参数登记两次"SPI 没有——"能力缺在形状不在库"的第一个实例）；反序模板（T-SQL DATEADD(day,{1},{0})）写不出。str.indexOf：INSTR 不是 Informix 内建（Oracle 支），MATCHES 找得到"匹配不上"给不出位置。cast.text：CAST 要带型名目标，"无宽度的文本型"不存在（VARCHAR/LVARCHAR 宽度可配也有上限、CLOB 不是 CAST 目标），SPI 没有宽度格——与 Gbase8sTypes.String"不写列级上限"是同一件事两半。date.utcNow：DBINFO('utc_current') 交整数秒，变 DATETIME 要 UNITS SECOND 族表达式再叠基准，两级拼装无库见证不发。math.ceiling/floor/power/greatest/least 与 str.replace：手册未逐页回读，"像 Oracle（那里四颗全有）"不是出处，整批不注册；Replace 另五家全有，本家唯一，那条链在这里会红。
- `src/QuickSql.Providers.Gbase8s/Gbase8sExpressions.cs + Build`（Concat）：|| 优先级在比较算子之间要靠括号钉住，共用层拼进 WHERE 时不自带括号（那条优先级表在五家的形状不通用）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sExpressions.cs + Build`（DateHour）：HOURS/MINUTES/SECONDS 要求带相应字段的 DATETIME——本家 DDL 发 YEAR TO FRACTION(3) 所以"能取到秒"齐；库里别人建的 DATE 列上三颗会红（契约与库不一致，不是翻译错）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + Gbase8sTypes`：出处档——只有 IBM 手册一档，"Informix 的即 GBase 8s 的"是借来的前提（同源到什么程度没材料）；本层是第一层"整层正确性挂在一颗未验证等价关系上"的 provider。重开条件：拿到一份 GBase 8s 手册，或一台库。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + Gbase8sTypes`：SERIAL 改判史——最初写在 GetStoreType 按 IsIdentity 分岔 INTEGER/SERIAL，被 golden/gbase8s/ddl-order.sql 抓出（第一次生成的样本里那一列是 "Id" INTEGER NOT NULL）：共用层先定型（EntityContractFactory.Complete 第 130 行 ApplyStorageDefaults）、第 133 行才判 IsIdentity，定型那一刻恒假且型名 echo 进契约定死——provider 侧看不到约定层的结论，任何"型名取决于身份属性"的设计在这一层写不出来。SERIAL(n) 只起点无步长（BY n 那支没有），步长契约有格子、本家无处发。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + Gbase8sTypes`：64 位取 ANSI 名——BIGINT/INT64 与 SERIAL8/BIGSERIAL 是同义字；SERIAL8 是 Informix 特有名，取 BIGSERIAL 与另五家同源；"取向不是能力"：目录读回哪颗名字没有读数（syscolumns/systypes 那格存什么见 SchemaReader）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + GetStoreType`（bool）：库里 BOOLEAN 存储形状两档版本不一致——早期在目录与驱动侧读作单字符 'T'/'F'，后期报 TRUE/FALSE，两种都不是 CLR 的 bool，SPI 没有"读法随版本"格；"位只跟存储型走"第一次被新落地的家主动套用。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + GetStoreType`（float）：FLOAT 是 8 字节、4 字节叫 SMALLFLOAT——唯一一处"照抄型名会得到错的宽度"且不报错（每行多 4 字节没人发现）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + GetStoreType`（decimal）：DECIMAL 精度上限 32 位（比 Oracle 血统四家小）——"默认必须映射到宽的一侧"第一次不成立的档，契约没有那一问。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + GetStoreType`（DateTime）：FRACTION 可写档位没读到确切最高档——若上限其实是 2（百分之一秒），CLR 第 3 位在库里进不去，今天没有库能量得出；与虚谷"读回只到毫秒"并排记。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + GetStoreType`（TimeOnly/DateTime）：DATETIME YEAR TO SECOND 与 HOUR TO SECOND 目录都读作 datetime——"是时刻还是日期时刻"目录答不出，只能靠 precision 那格（型名不足分档的第一个实例）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + String`：VARCHAR 上限是服务端 ONCONFIG 的 SQL_VARCHAR 配置（取值范围与默认值没逐页回读）——本家连列级上限都不是常数，不写一颗数比写错一档诚实，后果是超现场上限的声明在库里被拒。
- `src/QuickSql.Providers.Gbase8s/Gbase8sTypes.cs + ApplyStorageDefaults`：Unicode=false 的含义——量纲已由库定，取 true 会让"nvarchar 才是 Unicode 列"那套读法在本家变假话；MaxBytes 与 DDL 的 n 必须同数，否则闸门量的与建表写的差 4 倍。
- `src/QuickSql.Providers.Gbase8s/Gbase8sValues.cs + Gbase8sValues`：按名回填断路——ParameterBinding 钩子按列对号靠名字+hint，名字与文本脱钩后序是唯一依据；ToLogSql 恒等（替换按 prefix+字母 找占位符而文本只有裸 ?）——"参数内联给 DBA"发不出带值文本，保留的比 :p0 还少一格信息；不改代码也不声明"不支持"位。
- `src/QuickSql.Providers.Gbase8s/Gbase8sProvider.cs + Gbase8sProvider`：驱动路径第一次有键"纯按内建表存在"——内建表 → AssemblyLoadContext → 扫工厂类，连工厂类名字都没读到（虚谷至少有 XGProviderFactory.Instance）；解析不开是恒真，离线只钉得到"内建表有这一行"（BuiltinDriverTests），钉不到"解析得开"。
- `src/QuickSql.Providers.Gbase8s/Gbase8sProvider.cs + Gbase8sProvider`：交集第一次真掉——六家交集 3→2 位（Schemas ∶ Alter_Column_Precision），掉的是 Comment_On_Column（语句清单没有 COMMENT ON）与报不出的分页位；"交集量的是 src 覆盖面"仍然成立而不再充分——覆盖面变了是因为发不出那条语句。前七次不掉是因为新家声明的位都是已出现过的位；这次是共用发射路径第一次走到一家就断。
- `src/QuickSql.Providers.Gbase8s/Gbase8sProvider.cs + Gbase8sProvider`：不声明位补账——分页三颗（形状都不是它的、第四颗没有位，报成任何现有颗都是撒谎）；MultiStatement 与 SupportsPostStatementKeyRead 两处必须一起改（DialectSql 那条分句路径）；SEQUENCE 与 SKIP LOCKED 读到名字而差一份语法页；WindowFunctions 的读数是"发过文本、没有库可证"（金标 golden/gbase8s/fx-rown-* 与整层同档）；Guid_Raw16 是"另一支候选压根不存在"的第四种情形。
- `src/QuickSql.Providers.Gbase8s/Gbase8sProvider.cs + Bulk`：批量两档都不接——ESO 装载是 C 接口不是托管 API，托管面连一颗可反射的类型名都没有；重开条件排在"改回厂商程序集名"之后（那颗键到场第一件事是按厂商读数改名）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + Gbase8sSchemaReader`：零走到账——本文件是这一层十一个文件里唯一"既没有黄金样本、也没有离线用例、也没有活库"的代码（必须真连接才执行）；改这一格第一次要的是这台库，不是一份新日志。AdoExecutor.Bind 摘前缀那手在本文件用不上——发的参数从来就不带名字。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + Gbase8sSchemaReader`：COALESCE(?, CURRENT USER) 推不出型【推断】——位置形 ? 在文本里没有类型而绑的是 DBNull；改发两条文本（每格一对只差那颗谓词），是位置形带进来的第三件后果（前两件：恒等日志与顺序契约）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + Fold`：FoldToUpperCase 是布尔表达不了"折小写"——本家策略取 false（发射层恒加引号用不到折叠），真折叠规则只能写在读侧；重开条件"哪一家需要不靠引号活着"。加引号与不加引号在目录侧同解（库里存小写、未引号折出来也小写）——"建表与查表同一张"在本家对未引号建表也成立。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + NullableBit`：coltype 位编码（= 基本型号 + 256×可空 + 512×有默认）没逐页回读——拿到库时第一位就问它；读反的后果可指认（可空报非空，doctor 报"契约写得比库宽"）不像"表不存在"静默。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + ReadOrdinal`：colno 0 基还是 1 基没读定——取值不进判据只原样带出。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + ProbeLengthSemanticsAsync`：CharSemanticsProven=false 是唯一诚实取值——填的是"字节数、第一个数压根不存在"，与金仓/虚谷"字符数、第二个数问不出"同码异义（不是"没证实"，是"证实的是反方向"）；本家撞上的会是 DECIMAL（那颗是位数）与 DATETIME/INTERVAL 两族（全名带字段范围）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sSchemaReader.cs + IsText`：CLOB 不认的方向——本家大对象没有可报的声明宽度（width 在 CLOB 上是库块上限），报出来是看着像答案的假数；与虚谷 IsText 认 CLOB 在同一判据上第一次取反。守卫"可比"与这里"可量"两判在 LVARCHAR（可比可量）与 TEXT（可比不可量）上分岔，两处不共用名单。
- `src/QuickSql.Providers.Gbase8s/Gbase8sCodeFirst.cs + Gbase8sCodeFirst`：模式改判史——初稿写"这一家没有 CREATE SCHEMA"被 IBM 手册驳回（形是 CREATE SCHEMA AUTHORIZATION user，"模式"即所有者），改判后结论没变、理由整条换了。
- `src/QuickSql.Providers.Gbase8s/Gbase8sCodeFirst.cs + Gbase8sCodeFirst`：多语句账——尾部用分号串索引与五家同形，而本家能不能收带分号的命令文本没有读数（与 Gbase8sKeys 那颗同一问同一档，两处一起留给第一台库）；六家都躲不掉脚本路径，本家第六家。
- `src/QuickSql.Providers.Gbase8s/Gbase8sCodeFirst.cs + TryEmitColumnComment`：列注释在那边是目录表 syscomments 的事不是语句的事，SPI 没有"往目录表写一行"的形状；造列内 COMMENT 子句会得到一句库拒的建表。
- `src/QuickSql.Providers.Gbase8s/Gbase8sCodeFirst.cs + EmitCreateIndex`：索引种类按手册是普通、EXCLUDE（排除索引）与聚簇族；EXCLUDE 在契约 IndexKind 里没有对应（与虚谷 BITMAP 同一格）；全文检索是 Blend 扩展件不是语句级子句，没有可引的那一页。只写 DESC 不写 ASC 的不对称论证（缺省即升序；万一不认 ASC 多写让整句红）。
- `src/QuickSql.Providers.Gbase8s/Gbase8sCodeFirst.cs + EmitColumnDefinition`：rowversion 拒收按缺席读——类型清单没有那一型，没有"建得成"那一半读数（收不收那一串词未问），判据只有"发一句库会拒的建表不如不发"；与虚谷（三格实测）来路差两档，下一版别误读本家也有实测。
- `src/QuickSql.Providers.Gbase8s/Gbase8sCodeFirst.cs + EmitAlterColumnPrecision`：MODIFY (完整列定义) 的代价——把可空性等一起重写，契约没写的格子被库按这句给的形状定下来（与虚谷记过的是同一类风险）。

---

# Part G：注释精简时摘出的坑位、边界、实测读数与重开条件（Oracle 血统族 ∶ OracleBase / Oracle / Dameng / ShenTong）

> 2026-11-03 对六个 provider 项目（QuickSql.Providers.OracleBase / Oracle / Dameng / ShenTong / ClickHouse / DuckDb）做"简短 ///"精简时，
> 从注释里删掉的"展开才说得清"的部分：坑、边界、已知限制、实测错误码与读数、未实测台账、重开条件与历史账。
> 锚格式：`文件相对路径 + 成员名`。代码里只留一两句能自立的说明。

### Oracle 血统基座（QuickSql.Providers.OracleBase）

- `src/QuickSql.Providers.OracleBase/OracleChangeGuard.cs + 类`：这一族 LOB 四颗（CLOB/NCLOB/BLOB/BFILE）连等号都不接受，比较式会撞 "invalid use of large object type" 一档报错；同一份实体在 T-SQL 产出的守卫文本进得了合并档（NVARCHAR(MAX) 可以比），在这一族连承载它的那一句都发不出去——但"发不出去"不是 Compares 判出来的：EntityTracker.Mergeable 先问 Dialect.SupportsJoinedWrites（EntityTracker.cs:860）、逐列问 Compares 在它之后（:869），这一家开关 false，Compares 在生产路径上问不到；上一版"第一次真正决定一条语句的形状"按发射顺序重数推翻——后果对、归因错，它决定的是离线手工构造 AST 的样本形状。
- `src/QuickSql.Providers.OracleBase/OracleChangeGuard.cs + 类`：'' 即 NULL 这条不对称是这一族独一份（第四、五家出处里都没有），属跨库差异表不是缺陷清单；三段式"五家里四家"的多数派账（v0.30 第四家、v0.31 第五家先后换边、三条理由各不同）记在 ChangeGuardShapes 类注释末段与 XuguChangeGuard 第一条——历史账。
- `src/QuickSql.Providers.OracleBase/OracleConcurrency.cs + 类`：ORA_ROWSCN 细节——不在 ALL_TAB_COLUMNS（SchemaReader 读不到）、不能建表时声明、默认按块共享（要 INITTRANS 100 才逐行）、是 SCN 数而不是可映射的 CLR 槽位；触发器生成未接线，重开条件与金仓那一格同条；四族落 TimestampColumn 的分账：金仓（语法里没有这一档）、本族同条、MySQL 血统（语法有而没接）、虚谷（连语法都没读到）——只有金仓与本族共用"触发器生成"重开条件，别涨成"四家共用"。
- `src/QuickSql.Providers.OracleBase/OracleConnectorFactory.cs + 类 / TryEnsureLoaded`：历史账——第五家驱动 3.3.4.5 带原生同伴（xugusql.dll / libxugusql.so），"托管程序集加载成功"在那一家不等于"能用"；指纹在厂商 4.0.0.8 上抹掉，实测 drivers/ 不摆原生同伴时 Open 照样成功（订正与出处见 XuguConnectorFactory 与 BuiltinDrivers 虚谷格）。"这一格五家都有"的清点账：五个 *ConnectorFactory.cs 全命中（T-SQL 那处还点了 sni.dll），"更容易撞"实为"两族把原生同伴文件名写进了注释、三族只写'原生依赖'"——差别在写没写。
- `src/QuickSql.Providers.OracleBase/OracleProviderBase.cs + 类`：不声明格的台账——Upsert_MergeInto：MERGE INTO 语法存在但一行都没发过（声明=当成已完成）；五家"SupportsJoinedWrites 为 false 的退路"分账是两颗位 ∶ 三家，XuguProvider 那条两份产物都发过。MultiStatement/MultiResultSets：一次发送塞 "; " 分隔多条不成立，EmitCreateTable 尾部 "; " 串联只能走脚本路径。Cte/WindowFunctions/Count_Over_Window：库里都有但没有按位分派的发射口（CountMethod.CountOverWindow 在 QueryEngine 是"未接入"的抛）；窗口函数有发射口（QuickFx.RowNumber → DialectBase.EmitRowNumber）后对声明与不声明的家发同一句，判据未松动；PG 档把这三位声明了，两档不一致等一次统一清账。本家族每一位的出处都是"厂商文档或离线文本 + 未实测"——授权实例里没有一台是这个血统。
- `src/QuickSql.Providers.OracleBase/OracleProviderBase.cs + CodeFirst`：零覆写叶读法历史——v0.36 神通第一次让"零覆写"成为裁决（有缝而没走），v0.37 本尊第二颗理由相反（默认即自家语法）；"另七颗零覆写叶"重数为九颗（PG 六 ∶ MySQL 三），差额两颗没回来复点，记差额不悄悄改；要害是"覆写者数与住户数脱钩"。
- `src/QuickSql.Providers.OracleBase/OracleProviderBase.cs + Bulk`：预算位台账——绑定变量上限库侧与驱动侧各一个数，没有活库能证哪个先生效；ODP.NET 数组绑定（L1 候选）的回包给不出"这一批真进去几行"可数的数（ExecuteArray 一族返回口径一页厂商文档都没读到）；这颗程序集（内建表上 Oracle / DmProvider / Oscar 三份名）从未在任何机器解析成功；本机无授权实例。重开条件三缺一不行：先有实例，再有能读出"行数口径"的厂商页。XuguProvider.Bulk 同交 null（"没有可报数字"档两族，DialectParityTests 块数账两个 50）。
- `src/QuickSql.Providers.OracleBase/OracleDialect.cs + 类`：未实测台账——本类产出文本没有在一台真库上跑过，逐位出处记在 §13.5 台账，第一次真连库要逐位改判。
- `src/QuickSql.Providers.OracleBase/OracleDialect.cs + ParameterPrefix`：空白账——金仓那一族那颗 @ 在它自己的文件里没有注释出处（PostgreSqlDialect.cs:40 是一行光秃秃的覆写）；前缀分布 2 家 "：" ∶ 3 家 "@"，两边的证据强度不同条（本族有厂商写法本身，虚谷只是一枚产物里的 :XuguParam）。
- `src/QuickSql.Providers.OracleBase/OracleDialect.cs + EmitWrappedPaging`：带 Skip 分页外层多带一颗 ROWNUM 列且不进任何投影的依据——全仓库物化按序号取数（FieldCount 与 GetOrdinal 在 src/ 里各 0 处），那一列没有槽位可占。
- `src/QuickSql.Providers.OracleBase/OracleDialect.cs + Emit(UpsertStatement)`：MERGE 文本未实测——本血统第一条产出的 MERGE，达梦 ∶ 神通同档同因（共用本类产出）；重开条件一句话：连上一台 Oracle 血统库跑绿这一句。
- `src/QuickSql.Providers.OracleBase/OracleCodeFirst.cs + 类`：历史账——"没有 MultiStatement 的库要按 ; 拆开逐条发"那句"唯一"活得最短：五家 CodeFirst 全都产出过 "; " 串而只有 T-SQL 声明 MultiStatement（SqlServerProvider.cs:43，src/ 下 grep 只命中那一处声明 + 枚举自己）；五家里只有 MySQL 血统的建表句语法上带得上 INDEX (...) 形而代码没选（PostgreSqlCodeFirst.cs:54 金仓同一处同一形）；"把索引并入建表句"五家里零家，手册里"不能像 PG 系那样并入建表句"是本仓库一处存量错账（PG 的产出与这一族同形）。
- `src/QuickSql.Providers.OracleBase/OracleCodeFirst.cs + IdentityClause / 类`：按版本分岔的两格读数——标识符上限 12.1 之前是 30 字节，PK_/IX_ 前缀名由契约生成、长表名会超；判它要先知道库版本而 SPI 没有那一格。推断侧：12.1 之前同一句 CREATE TABLE 是两处语法错、12.1 起两句一起满足【推断，分段是本笔算的】，没有在 12.1 之前的库上跑过，不升档。第五家发的也是 IDENTITY(1,1)（XuguCodeFirst.cs:69 的 const）但内核自研、没有"语法表取自 SQL Server"的归因；神通与本尊零覆写的三档读法（没缝可走 / 有缝没有读数 / 默认即自家语法）与"覆写者数 ≠ 住户数"账见两颗叶子文件。
- `src/QuickSql.Providers.OracleBase/OracleExpressions.cs + 类`：cast.date 的代价——DATE 列上 TRUNC 是空操作而不是错，这一格与 T-SQL 的差异在文本上看不出来，只能靠模板表本身核；str.length 在这一族计字符数而 T-SQL 的 LEN 不计尾部空格、agg.average 在 NUMBER 上不截断而 T-SQL 的 AVG(int) 截断——跨库对账要显式处理的差，不是缺陷；SupportsRegex 的分账（落地几家里谁没有 REGEXP_LIKE）是多数派记账，无行为后果。
- `src/QuickSql.Providers.OracleBase/OracleExpressions.cs + LikeEscapeCharacter`：五家 4 ∶ 1 分账（第五家也取反斜杠；唯一分岔 MySQL 血统 ESCAPE '~'）；判据可重数：grep LikeEscapeCharacter 交 5 行（文档里箭头写成实体才不会被数进去）——历史账。
- `src/QuickSql.Providers.OracleBase/OracleKeys.cs + 类`：批量回读分账历史——3 发 ∶ 2 抛（发：T-SQL OUTPUT、金仓句尾 RETURNING、第五家带 [BULK COLLECT] 出处的 RETURNING；抛：本族与 MySQL 血统）；五份 IKeyStrategy 实现没有一处把 LAST_INSERT_ID / IDENTITYCURRVAL / @@IDENTITY 发进 SQL 文本，那三颗字符串只出现在六处（两处异常文案、四处注释）；第五家的一份产物真的这么发（XuguKeys 类注释第三条）——位数变了而缺口原地不动。
- `src/QuickSql.Providers.OracleBase/OracleKeys.cs + SupportsPostStatementKeyRead`：未实测台账——授权清单里没有第三族的真库（v0.23 到本版"这台库不存在"），IDENTITYCURRVAL 回读【推断】自厂商文档；重开条件：真库问出那颗表达式且单行插入后回读对上号。
- `src/QuickSql.Providers.OracleBase/OracleSchemaReader.cs + 类`：未实测台账——整份读法没有在一台活库上跑过，定位是 doctor 只读探测按厂商文档写出的样子。grep 账："整词的 NVL( 命中 7 处"与"这一句（NVL(:owner, USER)）命中两份文件"是两个数，别互相搬（本文件 CHAR_USED 那条 SQL 里另有两颗 NVL 兜别的 null）；第五家把"折叠绑定值"与"NVL(:owner, USER)"两样都抄了去，分布 2 ∶ 2（判据 grep NVL(:owner, USER) 交两份文件）。量纲分账：MySQL 血统把两颗数放同一行（CHARACTER_MAXIMUM_LENGTH 与 CHARACTER_OCTET_LENGTH），但那一行给的是"现在最多几字节"、算不出"当初按什么声明"（同一颗 VARCHAR(50) 在 utf8mb4 与 latin1 表上两个八度）——分布 2 ∶ 1 ∶ 2 见 XuguSchemaReader 末段。
- `src/QuickSql.Providers.OracleBase/OracleTypes.cs + 类`：DateTime 精度分账——五份 Types 的 DateTime 臂各带 (3)（DATETIME2(3) 一家、TIMESTAMP(3) 两家、DATETIME(3) 两家），一颗数五家一样而型名三样；grep 判据要带右引号（省了数到 12，混进 Values 的 DbType.DateTime2 臂）。bool 分账——整数型上比整数是四家共同形状（MySQL 血统 TINYINT(1)、第五家 TINYINT），可空布尔列写 "= 0" 的错法能在四家上静默漏行、只在金仓报错；Guid_Char36 从 v0.10 立项起第一次有 provider 真的走到它。
- `src/QuickSql.Providers.OracleBase/OracleValues.cs + 类`：分账——布尔内联 3 ∶ 2（第五家有真 BOOLEAN 不取、为列/谓词/字面量三处同形）；二进制拒内联三家理由（金仓 bytea_output 会话参数、MySQL 血统包体上限管不着内联、第五家 '\x' 前缀两种解释），内联两家共同点是"入参只有一种解释"；日期字面量五家三样（带 T 裸串两家、TO_TIMESTAMP 一家、空格分隔裸串两家，第五家与这一族同格式串不同包装）。枚举转整数走 IConvertible 的分布 4 ∶ 1：SqlServerValues.cs:34 用的正是 Convert.ChangeType，是 §12.8 那颗 Convert. 计数里的一处反例——历史账。
- `src/QuickSql.Providers.OracleBase/OracleValues.cs + InlineNumber`：T-SQL 那一格是 2026-09-22 真库实测（real/float8 列上 3 会被当整数常量、等值比较要走隐式转换，所以纯整数形式补 .0；1E-08.0 是语法错所以指数式不许补）；五家分布补点四家 ∶ 不补这一族一家（grep 'text += "' 五份 Values 命中四份），"三样"从 v0.30 起就是"两样"——漏数按 grep 钉回来的自纠账。

### 血统叶：Oracle 本尊 / Dameng / ShenTong

- `src/QuickSql.Providers.Oracle/OracleProvider.cs + 类`：零覆写计数与口径账——第十一颗零覆写叶、第十二层文本层壳、src/ 下第十八颗 provider 程序集，"壳/程序集"两读的差额与不裁理由记在 PostgreSqlProvider 类注释的【v0.37 第三笔自纠】段；探针自辩作废——DamengDialectTests 的血统基类此前靠影子探针（OracleFamilyProbe，唯一传"有枚举值没有 provider"库种的那颗）量空壳基线，本尊到场后影子有了本体，两者只差 CLR 类型名与内建表键，连异常文案都相同（EmitJoinedSources 带 {Kind}，两侧同为 Oracle）；两格版本读数同向的推断账——"＜ 12.1 两处语法错、≥ 12.1 两句一起满足"两个界版本取自仓库既有读数、分段是本笔算的。
- `src/QuickSql.Providers.Oracle/OracleProvider.cs + DriverKey`：内建表最薄的来路——Oracle.ManagedDataAccess 是内建表里第一颗"名字的唯一出处在我们自己注释里"的键（另三种来路都指向库外权威：厂商文档、邻家兼容臂、血统协议实现）；从未解析成功过、连一颗有文档出处的工厂类名都没有（虚谷至少读到过 XGProviderFactory.Instance）；golden/dameng 那 18 份黄金基线是派生叶的产物，本尊到场不新起目录也不改一份。
- `src/QuickSql.Providers.Dameng/DamengProvider.cs + 类`：分页历史账——原稿写"从 Oracle 分支拿 Paging_RowNum 再叠 OffsetFetch"，而叠 OffsetFetch 意味着换一套发射（不是加一位掩码）；今天只有 ROWNUM 三层，等第一次真库跑批看执行计划再决定要不要那一格。第五家发的也是同一串 IDENTITY(1,1) 但内核自研、没有"语法表取自 SQL Server"的归因——"两家同形不是巧合"只覆盖达梦一家。
- `src/QuickSql.Providers.ShenTong/ShenTongProvider.cs + 类`：兄弟叶第一次异形对读——此前仓库所有跨叶对读都是恒等或只差 Kind 那颗词，本家与达梦同族不同形（CREATE TABLE 差在缝所 carry 的那一句），可跑证据在 ShenTongDialectTests；"已适配"三句与 TiDB/OceanBase/openGauss/GaussDB/瀚高同档（只有文本层），且本家同族的达梦也没有一台活库；内建表 Oscar 名是文档口径原词、从未解析成功。真实例到场第一问：这台库认不认 GENERATED BY DEFAULT AS IDENTITY——缝的答案第一次要由叶子来验。

### ClickHouse（单层 provider）

- `src/QuickSql.Providers.ClickHouse/ClickHouseProvider.cs + 类`：已知缺口——内核 InsertEngine 把自增列剔出 INSERT 列清单那一手（InsertEngine.cs:566）在本家会把缺省 0 静默嵌进库（送值要动内核 SPI，整格记缺口）；WindowFunctions 半格——共用层发射口发大写 ROW_NUMBER() OVER (…)，本家型名按大小写敏感记着而函数名大小写两档出处都没读过，这一句在本家收不收是下一次真库复点的第一问；GBase 8s 对照——那家"分页有产出而一位都报不出"是因为句首限定词在枚举里没有位，本家分页形状恰好是枚举现成那颗（LIMIT/OFFSET 两子句独立、不要求 ORDER BY）；Comment_On_Column 的发射与读回都在活库 26.9.21.9 上逐字回放过（replay-5）。Transactions 取"没有"的诚实条款：多条不原子，公开 BeginTransaction 路径按位缺位当场抛。
- `src/QuickSql.Providers.ClickHouse/ClickHouseConnectorFactory.cs + 类`：【实测·2026-10-20】NuGet 拉包（一手 7.14.0）后由兜底扫描收走 ClickHouse.Client.ADO.ClickHouseConnectionFactory——7.x 把工厂从 5.x 时代的 ClickHouseClientFactory 更了名，导出类型里 DbProviderFactory 子类只有它一颗，兜底扫描对改名不敏感（内建表不写 factoryType 的取舍当场兑现一次）；那一包纯托管、装上即连，手工探针连 26.9.21.9 成功、无须任何拷 dll 的 csproj Target。
- `src/QuickSql.Providers.ClickHouse/ClickHouseDialect.cs + ParameterPrefix`：占位符三派分布 @ 五份 ∶ "：" 两份 ∶ ? 一份；实测反证在场：ParameterName="@x" 时文本里的 @x 原样过网被服务端当语法错报（读数出自活库，正文保留）。
- `src/QuickSql.Providers.ClickHouse/ClickHouseDialect.cs + 类`：第一台引擎到过场——golden 十份里九份逐字执行过（分页四式、UUID 主键、"app" 限定库、布尔三值、空集、insert-plain、select-entity）；改列那一份 golden 的 " ; " 拼接形是测试端为存档拼的、一句一执行才对（台账回写的读法）。
- `src/QuickSql.Providers.ClickHouse/ClickHouseExpressions.cs + 类`：substring 的已知偏差——参数序换算只对"索引一不偏"正确，多字节里中段换挡本来就不等价，未实测；未注册清单的逐批理由——math-functions/datetime-functions/conditional-functions 三张页抓了档案没逐页回读（Math.Round、CreatedAt.Year、聚合链在本家会红而另几族绿过，"这一家更窄"是取向不是本事）；blank 是 Nullable(String) 列上"空串与 NULL 在谓词两侧各自怎么算"没回读完；replace/indexOf 候选名读到（replaceOne/replaceAll、position/locate 一族）但语义格没回读；greatest/least 多实参展开与 NULL 折向没读。ConcatOperator 在 src/ 零消费者，填的是台账。
- `src/QuickSql.Providers.ClickHouse/ClickHouseSchemaReader.cs + 类`：0 行之策未走到（system.columns 那一问在探针里走通、0 行那问先不翻）；LowCardinality(Nullable(T)) 嵌套不在形状里——本族建表从发 LowCardinality，现场手工建出嵌套时按裸型读回、判据上假红；Param 的"对象名要不要带前缀"没有读数【推断·与前缀三方族同形】。
- `src/QuickSql.Providers.ClickHouse/ClickHouseTypes.cs + 类`：DateTimeOffset 的 § 退路（降级 DateTime UTC + 列标记 + 读取端还原）两半本仓库都不存在，宁缺不错；decimal 缺省 (18,4) 是推断（厂商上限 precision 76 比 18 宽）；本文件产出的每一条型名都没有在活库上跑过（出处 URL 挂 §13.5 第十七本台账）。
- `src/QuickSql.Providers.ClickHouse/ClickHouseValues.cs + Normalize / DbTypeOf`：【实测·2026-10-20 活库 26.9.21.9 ∶ 驱动 7.14.0】DateTime 参数三种 DbType 声明（DateTime/DateTime2/DateTimeOffset）在驱动序列化下都不带小数秒——绑 .123（Id=2）进 DateTime64(3) 落库 .000、重排 .456（Id=3）落库仍 .000，毫秒写侧静默丢失；字符串形（"yyyy-MM-dd HH:mm:ss.fff" 不变式）毫秒保真：.222/.555 两手原样落库、读回原样（DateTime Kind=Utc）。原稿把 DateTime 绑 DbType.DateTime2【推断·未实测】已被实测覆盖，本臂正常路径不可达、只剩"null 值带 DateTime hint"那条路。Float32/Float64 列上 3 与 3.0 是不是同一档隐式转换未实测。
- `src/QuickSql.Providers.ClickHouse/ClickHouseCodeFirst.cs + 类`：发射形活库判据汇总（写断言逐字对，出处 replay-5 与 beta.25 十份 golden）——建表骨架内联 INDEX "名" "列" TYPE minmax GRANULARITY 1 ∶ ALTER TABLE … ADD INDEX … ∶ COMMENT COLUMN ∶ MODIFY COMMENT ∶ MODIFY COLUMN 完整列定义，全部逐字回放过；PRIMARY KEY 括号形按官方示例带出【推断：多列括号形没有独立示例页】；rowversion 是"按缺席读"（官方数据类型清单没有该型、也没有"由库自动递增的列"档）。

### DuckDb（PG 血统第七叶）

- `src/QuickSql.Providers.DuckDb/DuckDbProvider.cs + 类`：出处 URL 清单——方言与 PG 兼容见 duckdb.org/docs/current/sql/dialect/postgresql_compatibility.html；未覆写格对读 insert.html 与 meta/information_schema.html；驱动页 dev/dotnet.html。"方言紧随 PG 但不是 PG"的差额半边：未覆写格的代码实际来自血统，库侧收不收全部归未实测。血统前六颗零覆写叶名单：金仓/openGauss/GaussDB/瀚高/GBase 8c/PG 本尊。驱动 DuckDB.NET.Data 按原生伴件分档、从未在任何机器解析成功；本机没有引擎也没有驱动包，"已适配"三句一句都够不上。
- `src/QuickSql.Providers.DuckDb/DuckDbProvider.cs + ClassifyIntegrityError`：翻格条件原文——集成册那条 IntegrityErrorTranslationPathTests 在本家跑绿（它今天跑在 SQL Server 与 PG 上），就把覆写删掉跟着血统走；23505/23502/23503/23514 那套是服务端行为不是线协议规定，本家没有一条真库用例撞过其中任何一颗。
- `src/QuickSql.Providers.DuckDb/DuckDbTypes.cs + 类 / StoreToClr`：出处 URL——overview.html（TIMESTAMP 别名 DATETIME）、time.html（TIME 不吃精度）、text.html（宽度被引擎忽略）、postgresql_compatibility.html（double precision 不在兼容清单）；读侧深度目录怎么报、报大写还是小写本仓库没有读数，所以 StoreToClr 按"血统认什么"的最大集保留不往两边删；PG 的 name、double precision 留在索引里不等于声明它们会出现在本家产出。
- `src/QuickSql.Providers.DuckDb/DuckDbCodeFirst.cs + 类 / EmitCreateSequence`：官方建身份表出处 URL（create_table.html，CREATE SEQUENCE 先行）；带单引号的表/列名在本家未建模【推断：没有读数，也没有调用方点名这一档】，撞上时按字面写、留给现场来拒。

### TDengine（单层 provider）

- `src/QuickSql.Providers.TDengine/TDengineProvider.cs + 类`：出处 URL 清单——命名与转义 docs.taosdata.com/tdengine-sql/appendix/limit（反引号是转义符、双引号不是）与 appendix/escape；数据类型 docs.tdengine.com/tdengine-sql/datatype；建表 docs/tdengine-sql/ddl/table（column_definition 文法、首列 TIMESTAMP 系统自动设主键、表注释在 table_options）；DELETE docs/tdengine-sql/08-delete（只认首列时间窗）；系统目录 INS_COLUMNS/INS_TABLES 逐颗列名对过官方 01-meta.md；函数 docs.taosdata.com/tdengine-sql/data-query/function（CHAR_LENGTH/LENGTH/CONCAT/TRIM 族/SUBSTRING 1 基/CAST）。已知缺口：InsertOrUpdate 没接——INSERT 同 ts 覆盖像天然 upsert，但 3.x 重复时间戳的确切语义（整行覆盖还是部分列、UPDATE 库参数在 3.x 的存废）没有读数，重开条件=活库钉死语义；超表（STABLE）/TAGS/INTERVAL 时窗/stmt2 批量全部留作可选面，第一笔不碰；COMPOSITE KEY（时间戳之外再加一颗键列）契约里没有"时间戳+复合"那一形可表达，复合主键整格拒。
- `src/QuickSql.Providers.TDengine/TDengineProvider.cs + CanCreateDatabase`：CREATE DATABASE 语句存在，但"连接串不带 db 直连再补建"那条锚点路没有读数（连接串键是 `db=`、ws 驱动能不能空库连接只有引擎答得了）——false 起笔，重开=活库。连接串另一颗坑：`protocol` 缺省是 native（要 taos.dll），纯托管档要显式 `protocol=websocket`（taosAdapter 6041）。
- `src/QuickSql.Core/DriverLoading/BuiltinDrivers.cs + tdengine 行`：**驱动程序集名是 `TDengine` 不是包名 `TDengine.Connector`**——官方源仓 src/TDengine.csproj 没设 AssemblyName、SDK 默认取项目文件名；这是本行最贵的坑，照包名写会把能成的解析变成"找不到程序集"。TDengineFactory 没有静态 Instance 字段，factoryType 刻意不写走兜底扫描（程序集里 DbProviderFactory 子类恰好一颗）。NuGet TDengine.Connector 3.2.1 的依赖只有 Newtonsoft.Json，WebSocket 路纯托管。
- `src/QuickSql.Providers.TDengine/TDengineValues.cs + CreateParameter`：驱动侧参数名必须以 $/@/# 打头——stmt2 按 ParameterName 前缀给值分类（$→tags、@→data、#→表名，非这三档当场 ArgumentException；VendorDoc·TDengine·驱动源码 taos-connector-dotnet 3.0 src/Data/Client/TDengineCommand.cs 的 Statement()），所以 binder 把驱动名翻成 @pN（AdoParameter.DriverName 那颗自带字段，AdoExecutor.Bind:656 优先取它）；文本、编号、日志都还是位置形 ?。多行 VALUES 的疑点：stmt2 的 BindRow+AddBatch 是单行习语，多值参数化 INSERT 在这条 ADO 路上的形状没有读数——L3 批量链发出的多行 INSERT 撞不撞得通只有活库答得了（记为重开面）。DateTime 直绑原值（ClickHouse"参数化丢毫秒"是那一家驱动的序列化问题，不预支）；Guid 归一成 'D' 形字符串（列型 VARCHAR(36)，驱动对 Guid 对象认不认没有读数）。
- `src/QuickSql.Providers.TDengine/TDengineDialect.cs + 类`：恒假/恒真保持基类 1 = 0/1 = 1（布尔字面量在 SELECT 骨架里收不收没有读数，与 ClickHouse 那边"有原生字面量"同形不同因）；布尔判定等式落 = true/= false（BOOL 原生型，VendorDoc）；参数序 = 编号序 = 绑定序这条耦合比 GBase 8s 更硬（stmt2 按 ParameterCollection 顺序 BindRow，没有第二道名字校验）。
- `src/QuickSql.Providers.TDengine/TDengineCodeFirst.cs + ValidateKeyShape / RequireTimestampFirst`：键形状校验面（本 provider 独有的格）——主键恰一颗、是契约首列、CLR 型映射 TIMESTAMP，三条件任一不满足当场红；无主键放行（首列仍必须 TIMESTAMP，insert-only 日志表的合法形状）；int Id 血统的模型在这一家建不出表（TableNamingEmissionTests 的 TDengine_refuses_mutation_and_family_shaped_ddl 点名）。列定义只有 "名" 型名一段：官方 column_definition 文法里没有 NULL/NOT NULL（可空性在 DDL 上没有落点，契约 NOT NULL 与库侧 col_nullable 的比对会不会产生"永远判差"的循环，只有活库答得了——记为重开面）；没有 DEFAULT（有 DefaultValue 的契约在发射点拒收）；没有列注释（TryEmitColumnComment 恒 false）。EmitAlterColumnPrecision 发 MODIFY COLUMN 完整列定义但能力位刻意不声明（改型的行为边界没有读数）。
- `src/QuickSql.Providers.TDengine/TDengineTypes.cs + 类`：TIMESTAMP 不带精度后缀——精度由建库 PRECISION 参数定（ms/us/ns，缺省档下 CLR 刻度是否保真只有引擎答得了，列级后缀会把它钉死在列上）；DECIMAL 是 3.3.6.0 才进场的型（旧引擎上这一格当场红）；string 落 VARCHAR(n) 且 n 量字节（chars×4，GBase 8s 同一把尺；NCHAR 量字符但内部 UTF-32 固定 4 字节/字符，宽窄取舍留活库复核）；契约没说宽度的 string/byte[] 取型上限 65,517（厂商类型页口径；可变长存储下上限不预占空间）；本家没有 DATE/TIME 独立型（DateOnly/TimeOnly/TimeSpan 抛）、没有 UUID 型（Guid→VARCHAR(36)）。
- `src/QuickSql.Providers.TDengine/TDengineSchemaReader.cs + 类`：INS_COLUMNS 没有备注列（列备注读不回，SchemaColumn.Comment 恒 null）；col_nullable 是 INT 不是布尔；col_id 是 SMALLINT（OrdinalPosition 从它来）；列备注的读数缺口与建侧"文法里没有 COMMENT 项"同果闭合。参数名以 @ 打头（stmt2 data 分类，与 binder 同一纪律）；DATABASE() 兜缺省库。

### TDengine 超级表（v0.39 第六笔）

- `Abstractions/Metadata/ProviderAttributes.cs + TDengineStableAttribute / TDengineTagAttribute / StableFacts`：声明流照 ClickHouseSkipIndex 全形复制（两侧管线只抄声明进 StableFacts，换算与占位符校验收在 EntityContractFactory.MapStable 一份）。与 SkipIndex 不同档的一格：G1 编译期诊断没有立（TAG 引用了不是列的成员在两条路径上都是收口那句话，运行期同句红）——编译期拦那一格是重开条件，等第二颗 provider 专属声明需要同款诊断时一起立。
- `Abstractions/Metadata/EntityContractFactory.cs + MapStable / ValidateSubtableTemplate`：占位符比对大小写不敏感（{device} 命中 Device）——模板是写给人看的面，而发射侧（TDengineDialect.EmitSubtableName）用同一把尺解析，两处不同尺就是"契约期放行、插入期红"的分叉；改任何一处必须两处一起看。TAG 数上限 128 颗不在这里判（发射侧的事），下限一颗是厂商硬性事实在收口判。
- `src/QuickSql.Providers.TDengine/TDengineCodeFirst.cs + EmitCreateStable / TagStoreType / EmitCreateSubtable`：TAGS 子句在列定义之后、表选项之前【VendorDoc·TDengine：超级表页】；TAG 门禁两颗——DECIMAL 仅支持普通列【数据类型页明文】、变长 TAG 上限 16 382 字节（ApplyStorageDefaults 给数据列的 65 517 缺省上限在 TAG 侧收窄）。补差那一档（EmitAddColumn/EmitAlterColumnPrecision/TryEmitTableComment）发的是 ALTER TABLE 文本——对稳定表服务端会拒（那句要的是 ALTER STABLE，SPI 只给表名不给契约拿不到稳定这一格），与接口注释"没接这一颗的家以库的拒绝收尾"同档；重开=SPI 拿到表级上下文或活库定口径。EmitCreateSubtable 是宿主手工口（照 ClickHouse EmitAddSkipIndex 先例），Core 不同步、走 orm.Ado 发。
- `src/QuickSql.Providers.TDengine/TDengineDialect.cs + Emit(in InsertStatement) / EmitStableInsert / EmitTagsClause`：TAG 值一律内联不走参数——STMT2 的参数化自动建表有明文行为缺口（子表不存在时报错，不像 shell 按 TAGS 建表【stmt 页】），文本形把自动建表留在 shell 同侧；不可内联档（byte[]）当场指路。NULL 的 TAG 走部分标签列形式（插入页推荐的 NULL 表达法，"TAGS(null) 字面量"没有明文）；全空时发全部名清单 + NULL 字面量（两种都没读数，取 fuller 那形，重开=活库）。数据列的 null 格发 NULL 不发 DEFAULT（本家没有 DEFAULT 那格，现行取值链不产 null 格，这半是形状保真）。同批行 TAG 值一致性当场校验——对已存在子表厂商会忽略语句里的 TAG 值（不报错），混 TAG 的批必须先红。多行 VALUES 参数化撞 stmt2 单行 BindRow 的疑点是另一格（普通表那份），与稳定面互不背书。
- `INFORMATION_SCHEMA 读面`：INS_COLUMNS 对稳定表含不含 TAG 列官方无明文（INS_TAGS 覆盖子表不含稳定表自身；INS_STABLES 的 columns/tags 两列分开计数是"分开记"的旁证）——SchemaReader 照旧按名读，返回什么引擎说了算；稳定实体的结构同步"尽力而为"，TAG 列差异判不了，重开=活库先答 INS_COLUMNS 那一格。TBNAME 伪列、SELECT TAGS …、ALTER STABLE ADD/DROP TAG 的同步面、多子表一条语句、Into() 式显式点名：全部未接，重开面各自记账。
