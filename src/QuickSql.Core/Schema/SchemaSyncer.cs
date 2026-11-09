using System.Data.Common;
using System.Text;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;

namespace QuickSql.Core.Schema;

/// <summary>SyncStructure 的执行体。按 IEntitySchemaRegistry.RegisteredTypes 枚举全部实体，
/// 逐个比对库中是否已有对应表：缺失的用 ICodeFirstDialect.EmitCreateTable 生成 DDL，已有的按 StructureDiff
/// 算出列级改动，再按策略执行或收集。一册实体可能住在几座库里：先按每张表自己声明的那座集群分组、
/// 再一座一趟，分组判据与读写共用 DataSourceRouting.ClusterOf——"这张表建在哪座库"与"这条 INSERT 发往
/// 哪座库"不是两件事，表建在 A 库、写进 B 库的症状是运行期"表不存在"。列级 diff 只在 Execute 档判：
/// Script 档不连库、没有库里的现形状就没有差别可判，那一档只出建表脚本并在结果里写明。分表实体在两档
/// 都跳过（跳了的名字进 SkippedSplitTemplates）：模板名带占位符不是一张能建的表，
/// 那几张片由 CreateShardsAsync 按清单建。</summary>
public sealed class SchemaSyncer
{
    readonly IDbProvider _provider;
    readonly IEntitySchemaRegistry _schemas;
    readonly ConnectionSources _sources;
    readonly bool _diffColumns;
    readonly bool _dropIndexes;

    /// <summary>diffColumns 是列级同步那一档（SchemaSyncColumns），关掉时已经在的表一格都不改；
    /// dropIndexes（SchemaSyncDropIndexes）默认关——它与列级同步不是一个风险档：补一列不丢数据，
    /// 删一条正在被用的索引会当场拖慢查询，只能由调用方显式开，不会因为"开了列级同步"就顺带生效。</summary>
    public SchemaSyncer(
        IDbProvider provider,
        IEntitySchemaRegistry schemas,
        ConnectionSources sources,
        bool diffColumns = true,
        bool dropIndexes = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(sources);

        _provider = provider;
        _schemas = schemas;
        _sources = sources;
        _diffColumns = diffColumns;
        _dropIndexes = dropIndexes;
    }

    /// <summary>按策略同步全部已注册实体的表结构。一次调用 = 在册的每座集群各一趟：每张表按它自己声明的
    /// 那座集群落库，缺省集群那张顺序与原样（不声明集群的实体全在一趟里、一条连接上）。
    /// policy 取 SchemaSyncPolicy（Execute 直接执行 / Script 只生成不执行 / Disabled 返回空结果）。
    /// 交回同步结果：已创建的表、已补的列/改的列/发的备注/建的索引，以及 NotJudged 与 Refused 两份给人看的清单。</summary>
    public async Task<SchemaSyncResult> SyncAsync(SchemaSyncPolicy policy, CancellationToken ct = default)
    {
        if (policy == SchemaSyncPolicy.Disabled)
            return SchemaSyncResult.Empty;

        var types = _schemas.RegisteredTypes;

        if (types.Count == 0)
            return SchemaSyncResult.Empty;

        var tally = new Tally();
        var skipped = new List<string>();

        // 先分组再动工：一张表属于哪座库这件事只在这一处问一次（判据与读写共用 DataSourceRouting），
        // 于是"这一趟要开几条连接、脚本要分几段"是分组的结果，而不是各处再判一遍。
        var byCluster = GroupByCluster(skipped);

        // Script 模式不需要连库：只生成 DDL 文本，不检查表是否存在
        if (policy == SchemaSyncPolicy.Script)
        {
            // 只有一座集群时不加那行段头：那一份文本与这一格落地之前逐字相等，
            // 脚本金标基线因此不必为一件本来不存在的事改口径。多座才分得出段，段头才有读者。
            var segmented = byCluster.Count > 1;
            var sb = new StringBuilder();

            foreach (var cluster in byCluster.Keys.Order(StringComparer.Ordinal))
            {
                var segment = new List<string>();

                if (segmented)
                    segment.Add($"-- 集群 {cluster}：下面这几句要落在这座集群的主库上");

                foreach (var contract in byCluster[cluster])
                    foreach (var statement in CreateStatements(contract, tally.NotJudged.Add))
                    {
                        tally.Record(statement);
                        segment.Add(statement.Sql.EndsWith(';') ? statement.Sql : statement.Sql + ";");
                    }

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(string.Join(Environment.NewLine + Environment.NewLine, segment));
            }

            if (_diffColumns)
                tally.NotJudged.Add("列级同步在 Script 档不判：判据的另一半是库里的现形状，而这一档压根不连库（要列级脚本，出路是给一个只读连接走 Execute）");

            return new SchemaSyncResult(tally.Created, false, sb.ToString())
            {
                AddedColumns = [.. tally.AddedColumns],
                AlteredColumns = [.. tally.AlteredColumns],
                AppliedComments = [.. tally.Comments],
                CreatedIndexes = [.. tally.Indexes],
                DroppedIndexes = [.. tally.Dropped],
                DdlStatements = [.. tally.Sql],
                NotJudged = [.. tally.NotJudged],
                Refused = [.. tally.Refused],
                SkippedSplitTemplates = [.. skipped],
                SkippedNullabilityTightening = [.. tally.SkippedTightenings],
            };
        }

        // Execute 模式要连库检查表是否存在；一座一趟，每趟先尝试建库（如果 provider 支持）
        foreach (var cluster in byCluster.Keys.Order(StringComparer.Ordinal))
        {
            var primary = _sources.PrimaryConnectionString(cluster);

            if (_provider.CanCreateDatabase)
                await _provider.EnsureDatabaseExistsAsync(primary, ct).ConfigureAwait(false);

            await using var connection = _provider.Connectors.CreateConnection(primary);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            foreach (var contract in byCluster[cluster])
            {
                var existing = await _provider.Schema.ReadTableAsync(connection, contract.Table, ct).ConfigureAwait(false);

                if (existing is null)
                {
                    foreach (var statement in CreateStatements(contract, tally.NotJudged.Add))
                    {
                        await ExecuteDdlAsync(connection, statement, tally.Sql, ct).ConfigureAwait(false);
                        tally.Record(statement);
                    }

                    continue;
                }

                if (!_diffColumns)
                    continue;

                await ApplyAsync(tally, connection, contract, existing, ct).ConfigureAwait(false);
            }
        }

        return new SchemaSyncResult(tally.Created, tally.Changed, string.Empty)
        {
            AddedColumns = [.. tally.AddedColumns],
            AlteredColumns = [.. tally.AlteredColumns],
            AppliedComments = [.. tally.Comments],
            CreatedIndexes = [.. tally.Indexes],
            DroppedIndexes = [.. tally.Dropped],
            DdlStatements = [.. tally.Sql],
            NotJudged = [.. tally.NotJudged],
            Refused = [.. tally.Refused],
            SkippedSplitTemplates = [.. skipped],
            SkippedNullabilityTightening = [.. tally.SkippedTightenings],
        };
    }

    /// <summary>一张已经在的表：先算计划，再把计划里的每一句发出去。清单里没有的一句都不会发，
    /// 那几格拒（删列、改主键与自增；删索引要调用方显式开那一档）在计划层就已经挡掉了。
    /// 收紧 NOT NULL 的那几句要多问库一句（StructurePlan.NullabilityTightenings）：计划层看得见
    /// "契约要它非空而库里允许为空"，看不见"存量行里有没有空值"——那一问只有这一格答得起（它手里有连接）。
    /// 问出"有空值"就把整句 ALTER 留下不发、写进 SkippedNullabilityTightening；答不出（没接探针的家）照发，
    /// 并在"判不了"清单里点名。跳过整句而不是只摘掉 SET NOT NULL 那一半：同一条 ALTER COLUMN 在脏表上
    /// 本身就是一次表重写 + 全表校锁，"精度也该顺手改对"不是启动那几分钟要办的事——回填完，
    /// 下一趟两句一起发。</summary>
    async Task ApplyAsync(Tally tally, DbConnection connection, EntityContract contract, SchemaTable existing, CancellationToken ct)
    {
        var plan = StructureDiff.Build(contract, existing, _provider.Schema, _provider.CodeFirst.Identifiers, _dropIndexes);

        foreach (var note in plan.NotJudged)
            tally.NotJudged.Add($"{contract.Table.Name}：{note}");

        foreach (var note in plan.Refused)
            tally.Refused.Add($"{contract.Table.Name}：{note}");

        var blocked = await BlockedTighteningsAsync(tally, connection, contract, plan, ct).ConfigureAwait(false);

        foreach (var statement in ApplyStatements(contract, plan, tally, blocked))
        {
            await ExecuteDdlAsync(connection, statement, tally.Sql, ct).ConfigureAwait(false);
            tally.Record(statement);
        }
    }

    /// <summary>那些会把一列收紧成 NOT NULL 的 ALTER，逐列问一句"库里有没有空值"。交回的是这一趟不许发的
    /// 那几列名；探针答不出（null）的那些列不进这一份——没接探针的家保持今天的形状，只在报告里欠一行"判不了"。</summary>
    async Task<HashSet<string>> BlockedTighteningsAsync(
        Tally tally,
        DbConnection connection,
        EntityContract contract,
        StructurePlan plan,
        CancellationToken ct)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var column in plan.NullabilityTightenings)
        {
            var hasNulls = await _provider.Schema
                .ColumnHasNullsAsync(connection, contract.Table, column.ColumnName, ct).ConfigureAwait(false);

            var name = $"{contract.Table.Name}.{column.ColumnName}";

            switch (hasNulls)
            {
                case true:
                    blocked.Add(column.ColumnName);
                    tally.SkippedTightenings.Add($"{name}：契约要它 NOT NULL 而库里这一列已有空值，" +
                                                 "这一句 ALTER 整条没发（改精度与收紧可空性在同一句里）。" +
                                                 "出路：回填那一列，下一趟结构同步自己把它收紧");
                    break;
                case null:
                    tally.NotJudged.Add($"{name}：这一家没接“有没有空值”那颗探针，收紧的那一句照发——" +
                                         "存量里真有空值时它会以库的拒绝收尾（要人工：先回填）");
                    break;
            }
        }

        return blocked;
    }

    /// <summary>一句 CREATE TABLE 加上它欠的那几份备注。建表那一趟从前一个字节都不发备注：
    /// TryEmitColumnComment 在 SPI 上挂了很多版而没有调用点，于是"实体上写了备注、库里查不到"
    /// 是每一次新建表都发生的事，而不是只有改备注时才发生。</summary>
    List<Statement> CreateStatements(EntityContract contract, Action<string> notJudged)
    {
        var items = new List<Statement> { new(EmitCreateTable(contract), ChangeKind.CreateTable, contract.Table.Name) };

        foreach (var column in contract.Columns)
        {
            if (column.Comment is null)
                continue;

            var sql = ColumnCommentText(contract, column);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.ColumnComment, $"{contract.Table.Name}.{column.ColumnName}"));
            else
                notJudged($"{contract.Table.Name}.{column.ColumnName}：这一家写不出列注释，备注只落在契约里");
        }

        if (contract.Table.Comment is not null)
        {
            var sql = TableCommentText(contract.Table);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.TableComment, contract.Table.Name));
            else
                notJudged($"{contract.Table.Name}：这一家写不出表注释，备注只落在契约里");
        }

        return items;
    }

    /// <summary>把计划翻成语句清单。blockedTightenings 是这些列的那一句 ALTER 这一趟不许发：
    /// 它们要把一列收紧成 NOT NULL 而库里那一列已有空值（每列为什么被挡见 BlockedTighteningsAsync），
    /// 清单外的一句都不受影响。</summary>
    List<Statement> ApplyStatements(EntityContract contract, StructurePlan plan, Tally tally, HashSet<string> blockedTightenings)
    {
        var items = new List<Statement>();

        // 删索引排在补列与改列<em>之前</em>：T-SQL 上"这一列被索引覆盖着"会让 ALTER COLUMN 直接失败
        // （ALTER TABLE ... ALTER COLUMN ... 因为访问此列的对象而失败），而计划里那条索引正是建在这列上的
        // 那一条。顺序反过来就是"这一趟发不出去"，而不是"发得出去但慢一点"。
        foreach (var index in plan.DropIndexes)
        {
            var sql = DropIndexText(contract.Table, index);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.DropIndex, $"{contract.Table.Name}.{index.Name}"));
            else
                tally.NotJudged.Add($"{contract.Table.Name}.{index.Name}：这一家写不出删索引那一句，那条索引留在库里");
        }

        foreach (var column in plan.AddColumns)
            items.Add(new Statement(Emit(w => _provider.CodeFirst.EmitAddColumn(w, contract.Table, column)),
                ChangeKind.AddColumn, $"{contract.Table.Name}.{column.ColumnName}"));

        // 收紧被挡的那一列：整句不发（改精度与 SET NOT NULL 在同一句里，拆不开，理由见 ApplyAsync）。
        // 而"这一家压根没有改列语句"那一族（今天只有 SQLite：不声明 Alter_Column_Precision）也在这一格挡：
        // 那句 EmitAlterColumnPrecision 在本家是兜底抛，让执行层拿到这一句就是把宿主启动撞死在发射点上——
        // 与被探针挡下的 23502 是同一个家庭的两种炸法。挡在这里而不是 StructureDiff 里：
        // 那颗归一是纯函数，能力位是 provider 侧事实，不塞进它的形参表。
        var canAlterColumns = (_provider.Capabilities & DbCapabilities.Alter_Column_Precision) != 0;

        foreach (var column in plan.AlterColumns)
        {
            if (blockedTightenings.Contains(column.ColumnName))
                continue;

            if (!canAlterColumns)
            {
                tally.NotJudged.Add($"{contract.Table.Name}.{column.ColumnName}：这一列与契约不同，" +
                                    "而这一家没有改列的语句（能力位 <c>Alter_Column_Precision</c> 未声明）：一句都不发，" +
                                    "这一列原样留着（要人工：\"建新表、拷数据、换名\"那一族重建在本仓库未建模，§14。" +
                                    "可空性收紧的那一半若库里已有空值，另见跳过清单）");
                continue;
            }

            items.Add(new Statement(Emit(w => _provider.CodeFirst.EmitAlterColumnPrecision(w, contract.Table, column)),
                ChangeKind.AlterColumn, $"{contract.Table.Name}.{column.ColumnName}"));
        }

        foreach (var column in plan.ColumnComments)
        {
            var sql = ColumnCommentText(contract, column);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.ColumnComment, $"{contract.Table.Name}.{column.ColumnName}"));
            else
                tally.NotJudged.Add($"{contract.Table.Name}.{column.ColumnName}：这一家写不出列注释");
        }

        if (plan.EmitTableComment)
        {
            var sql = TableCommentText(contract.Table);

            if (sql is not null)
                items.Add(new Statement(sql, ChangeKind.TableComment, contract.Table.Name));
            else
                tally.NotJudged.Add($"{contract.Table.Name}：这一家写不出表注释");
        }

        foreach (var index in plan.CreateIndexes)
            items.Add(new Statement(Emit(w => _provider.CodeFirst.EmitCreateIndex(w, contract.Table, index)),
                ChangeKind.CreateIndex, $"{contract.Table.Name}.{index.Name}"));

        return items;
    }

    /// <summary>交回 null 说的是这一家没有这一档（TryEmit* 交回 false），
    /// 别让"没有"退化成"发了一条空句"。</summary>
    string? ColumnCommentText(EntityContract contract, ColumnContract column)
        => EmitTry(w => _provider.CodeFirst.TryEmitColumnComment(w, contract.Table, column));

    string? TableCommentText(TableInfo table)
        => EmitTry(w => _provider.CodeFirst.TryEmitTableComment(w, table, table.Comment));

    string? DropIndexText(TableInfo table, SchemaIndex index)
        => EmitTry(w => _provider.CodeFirst.TryEmitDropIndex(w, table, index));

    string? EmitTry(Func<SqlWriter, bool> emit)
    {
        var writer = NewWriter();

        return emit(writer) ? writer.ToSqlString() : null;
    }

    string Emit(Action<SqlWriter> emit)
    {
        var writer = NewWriter();

        emit(writer);

        return writer.ToSqlString();
    }

    SqlWriter NewWriter() => new(new StringBuilder(), [], _provider.Dialect.ParameterPrefix);

    /// <summary>把册子里每张表分到它自己那座集群那一格，分不进去的分表模板进 skipped。
    /// 集群不在册时这里就抛（ConnectionSources 那句带在册清单的）：实体写了一个配置里没有的集群名，
    /// 症状本来就是"表建不出、语句发不出"，而这一格是它最便宜的现场——连库还没连，
    /// 报错里能同时看见那张表与那几座库的名字。</summary>
    Dictionary<string, List<EntityContract>> GroupByCluster(List<string> skipped)
    {
        var byCluster = new Dictionary<string, List<EntityContract>>(StringComparer.Ordinal);

        foreach (var clrType in _schemas.RegisteredTypes)
        {
            var contract = _schemas.Contract(clrType);

            if (contract.Table.Split is { } split)
            {
                skipped.Add($"{contract.Table.Name}（{split.Kind} 档，分片键列 {split.ColumnName}）");
                continue;
            }

            var cluster = DataSourceRouting.ClusterOf(contract.Table);
            AssertKnown(cluster, contract.Table.Name);

            if (!byCluster.TryGetValue(cluster, out var tables))
                byCluster[cluster] = tables = [];

            tables.Add(contract);
        }

        return byCluster;
    }

    void AssertKnown(string cluster, string tableName)
    {
        if (_sources.Knows(cluster))
            return;

        // 借 PrimaryConnectionString 那一颗把消息整个复用（它已经写着"在册的是哪几座"），不另造一份口径。
        _ = _sources.PrimaryConnectionString(cluster);
    }

    /// <summary>分表实体的一次建片：清单是调用方给的，这里不猜范围——现存的分片该有哪几段只有业务知道，
    /// 而"默认建全部"在一颗日档的表上等于凭空造几千张。逐张先问存在再发 DDL，所以这一颗是幂等的：
    /// 同一个范围跑两次，第二次一张也不建。存在判据与 SyncAsync 同一颗（ISchemaReader.ReadTableAsync），
    /// 因此这一格不需要新的方言能力位：CREATE TABLE IF NOT EXISTS 那一族会让 DDL 在各家分成几种文本，
    /// 正是要避开的那类分岔。索引不随片发：ForShardDdl 交回的契约是空的索引清单，
    /// SplitTablesResult.IndexesNotCreated 把模板上那几条的名字点出来——这不是"实现一半"，
    /// 是"两条出路都不肯悄悄走"（改名会多出第六份命名口径，只发第一张是一句假绿）。</summary>
    public async Task<SplitTablesResult> CreateShardsAsync(
        EntityContract template, IReadOnlyList<TableInfo> shards, CancellationToken ct = default)
    {
        // 片落在模板自己声明的那座集群上：分片只改表名，不改住在哪座库。
        var primary = _sources.PrimaryConnectionString(DataSourceRouting.ClusterOf(template.Table));

        if (_provider.CanCreateDatabase)
            await _provider.EnsureDatabaseExistsAsync(primary, ct).ConfigureAwait(false);

        await using var connection = _provider.Connectors.CreateConnection(primary);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var created = new List<string>();
        var existing = new List<string>();

        // 这一趟自己的"已生效"清单：分片不走 Tally（那一份是 SyncAsync 那趟的账），而失败时要报的
        // "前面建好了哪几张片"恰恰只有这一趟知道。
        var sent = new List<string>();

        foreach (var shard in shards)
        {
            if (await _provider.Schema.ReadTableAsync(connection, shard, ct).ConfigureAwait(false) is not null)
            {
                existing.Add(shard.Name);
                continue;
            }

            var statement = new Statement(EmitCreateTable(template.ForShardDdl(shard)), ChangeKind.CreateTable, shard.Name);
            created.Add(shard.Name);
            await ExecuteDdlAsync(connection, statement, sent, ct).ConfigureAwait(false);
            sent.Add(statement.Sql);
        }

        var requested = new List<string>(shards.Count);

        foreach (var shard in shards)
            requested.Add(shard.Name);

        return new SplitTablesResult([.. requested], created, existing) { IndexesNotCreated = IndexNames(template) };
    }

    /// <summary>CreateShardsAsync 的那一份文本，不连库（Script 模式同一条路）：给 DBA 审批、
    /// 也给离线用例钉"同一个范围算出哪几张表"。脚本里同样没有 CREATE INDEX，
    /// 审批的人看见的就是执行时会发出去的那几句，一处不多一处不少。</summary>
    public string ShardsScript(EntityContract template, IReadOnlyList<TableInfo> shards)
    {
        var sb = new StringBuilder();

        // 只有不在缺省集群上时才写这一行段头：那一座才是"这段脚本要在哪个库里跑"的唯一读者信息，
        // 缺省集群那一份文本与这一格落地之前逐字相等。
        var cluster = DataSourceRouting.ClusterOf(template.Table);

        if (!string.Equals(cluster, DataSourceRouting.DefaultCluster, StringComparison.Ordinal))
            sb.AppendLine($"-- 集群 {cluster}：这几句要落在这座集群的主库上").AppendLine();

        foreach (var shard in shards)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            var ddl = EmitCreateTable(template.ForShardDdl(shard));
            sb.Append(ddl);

            if (!ddl.EndsWith(';'))
                sb.Append(';');
        }

        return sb.ToString();
    }

    static IReadOnlyList<string> IndexNames(EntityContract template)
    {
        if (template.Indexes.Count == 0)
            return [];

        var names = new List<string>(template.Indexes.Count);

        foreach (var index in template.Indexes)
            names.Add(index.Name);

        return [.. names];
    }

    string EmitCreateTable(EntityContract contract)
    {
        var builder = new StringBuilder();
        var parameters = new List<AdoParameter>();
        var writer = new SqlWriter(builder, parameters, _provider.Dialect.ParameterPrefix);

        _provider.CodeFirst.EmitCreateTable(writer, contract);

        return writer.ToSqlString();
    }

    /// <summary>发出一句 DDL，失败时把"哪一句"与"前面已经落库的哪几句"一起报出去（SchemaSyncFailedException）。
    /// 这一格是唯一的抛出点：三个调用点都只交回 Statement 与那份已经成功的清单，没有一处能漏掉这个包装。
    /// applied 是这一句之前真发出去过的那些句（Tally.Sql 或分片那一趟的局部清单），传的是活清单，
    /// 异常在抛出那一刻取快照。</summary>
    async Task ExecuteDdlAsync(DbConnection connection, Statement statement, IReadOnlyList<string> applied, CancellationToken ct)
    {
        await using var command = _provider.Connectors.CreateCommand(connection);
        command.CommandText = statement.Sql;

        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SchemaSyncFailedException(
                statement.Sql,
                statement.Name,
                statement.Kind.ToString(),
                applied,
                connection.Database,
                ex);
        }
    }
}

/// <summary>一条要发出去的 DDL，连"它改的是哪一格"的那句话。文本与名字同行是因为两档策略读的是同一份清单：
/// Execute 发文本、把名字记进结果；Script 把文本拼进脚本、同样把名字记进结果。少了名字那一格，
/// 结果里就只能说"发了几句"，而要答的是"哪一列被改了"。</summary>
readonly record struct Statement(string Sql, ChangeKind Kind, string Name);

/// <summary>一句 DDL 改的是哪一档。它决定这句话落进 SchemaSyncResult 的哪一份清单。</summary>
enum ChangeKind
{
    CreateTable,
    AddColumn,
    AlterColumn,
    ColumnComment,
    TableComment,
    CreateIndex,
    DropIndex,
}

/// <summary>一趟同步的八份点名清单。内部类型，因为它只活在这一格里：两档策略共用一个累加点（Record），
/// 于是"哪些算动作、哪些只是给人的话"这一判据也只有一份。</summary>
sealed class Tally
{
    public List<string> Created { get; } = [];
    public List<string> AddedColumns { get; } = [];
    public List<string> AlteredColumns { get; } = [];
    public List<string> Comments { get; } = [];
    public List<string> Indexes { get; } = [];
    public List<string> Dropped { get; } = [];
    public List<string> NotJudged { get; } = [];
    public List<string> Refused { get; } = [];
    public List<string> SkippedTightenings { get; } = [];

    /// <summary>这一趟发出去（Script 档则是准备发出去）的每一句原文，顺序 = 发送顺序。它与
    /// SchemaSyncResult.DdlStatements 是同一份清单，只是两档的语义不同：Execute 档里这一格是"真发过"，
    /// Script 档里是"会发"。收在这一格是因为 DDL 不走 Aop.Executing（那条钩子只管带参数的语句，
    /// 而这一趟连的是同步自己那条连接），调用方要看见发了什么只有这一处。</summary>
    public List<string> Sql { get; } = [];

    /// <summary>这一趟真发出去过至少一句。SchemaSyncResult.Executed 读的是这一格，而它说的不是"连了库"
    /// ——一趟只读的同步连了库、判了所有档、一个字节没发，那一格必须是 false。</summary>
    public bool Changed { get; private set; }

    public void Record(Statement statement)
    {
        Changed = true;

        Sql.Add(statement.Sql);

        switch (statement.Kind)
        {
            case ChangeKind.CreateTable:
                Created.Add(statement.Name);
                break;
            case ChangeKind.AddColumn:
                AddedColumns.Add(statement.Name);
                break;
            case ChangeKind.AlterColumn:
                AlteredColumns.Add(statement.Name);
                break;
            case ChangeKind.ColumnComment:
            case ChangeKind.TableComment:
                Comments.Add(statement.Name);
                break;
            case ChangeKind.CreateIndex:
                Indexes.Add(statement.Name);
                break;
            case ChangeKind.DropIndex:
                Dropped.Add(statement.Name);
                break;
        }
    }
}

/// <summary>SyncStructure 的产物：这一趟发了什么、判不了什么、拒了什么，以及（Script 模式下的）完整 DDL 文本。
/// Ddl 在 Execute 模式下为空字符串（那一档的原文在 DdlStatements 里，逐句一条）；在 Script 模式下包含全部
/// CREATE TABLE 与备注语句，可直接交给 DBA 审批后手动执行。</summary>
public sealed record SchemaSyncResult(
    IReadOnlyList<string> CreatedTables,
    bool Executed,
    string Ddl)
{
    /// <summary>这一趟补上的列，逐项写「表.列」。</summary>
    public IReadOnlyList<string> AddedColumns { get; init; } = [];

    /// <summary>这一趟 ALTER COLUMN 过的列，逐项写「表.列」。改的可能是长度、标度，也可能是可空性——
    /// 那一格在 T-SQL 里必须整列定义重写，所以两者在同一句里，报告里也在同一格里。</summary>
    public IReadOnlyList<string> AlteredColumns { get; init; } = [];

    /// <summary>这一趟发过（或重发过）的备注，逐项写「表.列」，表自身那一条只写表名。
    /// 新增与被改的列都在这一格：ALTER 会连带抹掉那一列的备注，不发回去就是"同步成功而备注没了"。</summary>
    public IReadOnlyList<string> AppliedComments { get; init; } = [];

    /// <summary>这一趟建出来的索引，逐项写「表.索引名」。</summary>
    public IReadOnlyList<string> CreatedIndexes { get; init; } = [];

    /// <summary>这一趟 DROP 掉的索引，逐项写「表.索引名」，名字是库里读回来的那一个。
    /// 这一格在默认配置下永远是空的：删索引要 SchemaSyncDropIndexes 显式开，而唯一约束即便开了也不在这里
    /// （它进 Refused，因为 DROP INDEX 删不动约束）。非空就是"库里现在少了几条索引"——
    /// 那些查询从这一刻起走全表扫，这一格要有人看。</summary>
    public IReadOnlyList<string> DroppedIndexes { get; init; } = [];

    /// <summary>这一趟的 DDL 原文，逐句一条，顺序 = 发送顺序。Execute 档里它是"真发过的那几句"
    /// （Ddl 在那一档是空串，而 DDL 又不经过 Aop.Executing 那条日志钩子——同步自己连的是另一条连接，
    /// 所以调用方要在日志里看见发了什么，只有这一格是给得出的）；Script 档里它是"会发的那几句"，
    /// 与 Ddl 同内容、只是没拼成一整篇。</summary>
    public IReadOnlyList<string> DdlStatements { get; init; } = [];

    /// <summary>这一家读不出所以判不了的档，逐条指名。它非空就是"这一趟有几档根本没同步过"：
    /// 家家在补齐读侧之前各有一串这样的名字，一句"同步完成"盖不住它们。</summary>
    public IReadOnlyList<string> NotJudged { get; init; } = [];

    /// <summary>读得出而故意不发的那几格（删列、改主键与自增、DROP 索引），逐条指名并给出路。
    /// 与 NotJudged 的分工见 StructurePlan。</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];

    /// <summary>这一趟一个字节都没发。只看五份动作清单、不看两份话单：判不了与要人工是那几档本来就
    /// 发不出东西的格，把它们算进"有变化"会让幂等裁判永远红。</summary>
    public bool IsEmpty
        => CreatedTables.Count == 0 && AddedColumns.Count == 0 && AlteredColumns.Count == 0
           && AppliedComments.Count == 0 && CreatedIndexes.Count == 0 && DroppedIndexes.Count == 0;

    /// <summary>被这一趟跳过的分表模板，每项写着模板名、分片档与分片键列。跳过不是漏掉：
    /// 整库同步拿不到"要建哪几段"的范围，而默认建全部片是一张日档表上凭空造几千张——范围由
    /// orm.CreateSplitTables 那一格给。它非空时说明库里那些片还不存在，写侧要到目标片上撞库错才看得见，
    /// 所以这一格要有人看。</summary>
    public IReadOnlyList<string> SkippedSplitTemplates { get; init; } = [];

    /// <summary>这一趟没有发的那几句收紧 NOT NULL 的 ALTER，逐项写「表.列」与原因。它与 Refused 不是一格：
    /// 拒是"这条规则永远不做"（删列、改主键），这一格是"库里现在有挡着它的东西"——回填完那一列，
    /// 下一趟结构同步自己就把它收紧，不需要谁改契约。被挡的那一句是整条 ALTER，所以那一列的精度/长度差
    /// 也一并还挂着：AlteredColumns 里没有它，不是漏记。这一格非空就是"库里有几列还比契约松"，
    /// 读侧不会因此出错，写侧要等回填。</summary>
    public IReadOnlyList<string> SkippedNullabilityTightening { get; init; } = [];

    public static readonly SchemaSyncResult Empty = new(Array.Empty<string>(), false, string.Empty);
}

/// <summary>一次分表建片的产物：Requested 是换算出来的片清单（顺序就是清单顺序），
/// Created 与 AlreadyThere 是它的两个子集。三格合起来才是完整答案："只建了几张"与"另外几张本来就在"
/// 要能分得开，否则重复跑这一颗看起来像什么都没做。</summary>
public sealed record SplitTablesResult(
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> AlreadyThere)
{
    /// <summary>模板上声明了、而这一趟没有随任何一张片建出去的索引名。它们一条都没发，不是"发了一部分"：
    /// 索引名在 PG 与 Oracle 那一族是模式内唯一的，同一个模板的 N 张片各发同一颗名字到第二张就撞。
    /// 这一格非空就是"片上现在没有这些索引"，读侧到那一张上是全表扫，不是慢一点的索引扫。</summary>
    public IReadOnlyList<string> IndexesNotCreated { get; init; } = [];
}
