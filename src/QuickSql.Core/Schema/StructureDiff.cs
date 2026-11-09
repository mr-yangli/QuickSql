using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Core.Schema;

/// <summary>一张表这一趟打算对库做的改动（列级同步）。纯函数产物：契约与库里读回的读数进、动作清单出，
/// 不连库、不发 DDL、不认 provider——只有"这一型在我这儿算不算同一型"那一问交回
/// ISchemaReader.ColumnShapeMatches，因为那一问只有本家答得起。把它与执行分开是因为这一格必须能被离线穷举：
/// 真库上"改一颗标度"只发生一次，而"库里多一列、主键少一列、同名索引换了两列"在测试库里可以随手摆出来；
/// 幂等裁判（第二趟零动作）量的也是这一格的归一，不是 DDL 文本。拒则在计划里就落地、不留给执行层判断
/// （不删列、不改主键与自增那一族；删索引要调用方显式开 dropIndexes），执行层拿到的清单因此不含任何
/// 会丢数据的那一句，Refused 是给人点名的那几格。</summary>
public sealed record StructurePlan(TableInfo Table)
{
    /// <summary>库里没有、这一趟要 ADD 的列，顺序 = 契约列序。</summary>
    public IReadOnlyList<ColumnContract> AddColumns { get; init; } = [];

    /// <summary>库里已经有、形状或可空性对不上的列，要 ALTER COLUMN。主键列与"值归库给"的那几列
    /// 不会出现在这一格（它们进 Refused）。</summary>
    public IReadOnlyList<ColumnContract> AlterColumns { get; init; } = [];

    /// <summary>要发（或重发）列备注的那些列：新增的、被改的、备注本身变了的三类的并集，顺序 = 契约列序。
    /// 并集而不是三张清单，是因为 T-SQL 的 ALTER COLUMN 会连带删掉这一列的扩展属性：改完精度不顺手把
    /// 备注发回去，下一趟读到的备注就是空的，而"备注同步"会以静默丢备注的形态过掉幂等裁判——
    /// 它确实零动作，因为要发的东西已经不在库里了。</summary>
    public IReadOnlyList<ColumnContract> ColumnComments { get; init; } = [];

    /// <summary>AlterColumns 里那些"这一趟会把库里的可空列收紧成 NOT NULL"的列，顺序 = 契约列序。
    /// 它是子集而不是第三件事：放宽方向与只改形状的那几列都不在这里，它们的 ALTER 撞不出 23502。
    /// 单独列出来是因为收紧那一档的判据缺一半——"这一列有没有存量 NULL"只有库答得起，所以归一在这里判
    /// （离线可穷举），探量在 SchemaSyncer 那格做（它手里有连接），两边共读这一份子集，判据不另立第二处。</summary>
    public IReadOnlyList<ColumnContract> NullabilityTightenings { get; init; } = [];

    /// <summary>要否重发表备注。只在契约说过（TableInfo.Comment 非 null）而库里那份与它不同才为真：
    /// null 说的是"没写过备注"，不是"要求库里没有备注"。</summary>
    public bool EmitTableComment { get; init; }

    /// <summary>契约上有、这张表上没有的索引，要 CREATE INDEX。"有没有"看的是列的序列而不是索引名
    /// （人给索引换过名而列没换，不该再建一条）。</summary>
    public IReadOnlyList<IndexContract> CreateIndexes { get; init; } = [];

    /// <summary>库里有、契约上没有、而这一趟要 DROP 的那几条，交回的是库里读回来的那颗
    /// （名字与列序列都是目录的原文）。只在调用方显式开了"允许删索引"那一档时才可能非空：
    /// 默认关，删一条正在被人用的索引会当场拖慢查询，而契约里没有它可能只是"这一册实体没覆盖那张表的
    /// 全部查询"。唯一约束不进这一格（DROP INDEX 删不动它），它进 Refused。</summary>
    public IReadOnlyList<SchemaIndex> DropIndexes { get; init; } = [];

    /// <summary>这一家读不出所以这一趟判不了的那些档，按档汇成一行一行。它与 Refused 是两件事：
    /// 拒是"读得出、判得出、而这一趟故意不做"，判不了是"连判据都没有"；并成一格会让"换一家库"
    /// 与"被规则挡住了"在报告里长同一个样。</summary>
    public IReadOnlyList<string> NotJudged { get; init; } = [];

    /// <summary>读得出而不执行的那些格，逐条指名并给出路（"要人工"清单）。</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];

    /// <summary>这张表这一趟一个字节都不发。注意 NotJudged 与 Refused 不算动作：它们是给人看的两串名字，不发 SQL。</summary>
    public bool IsEmpty
        => AddColumns.Count == 0 && AlterColumns.Count == 0 && ColumnComments.Count == 0
           && !EmitTableComment && CreateIndexes.Count == 0 && DropIndexes.Count == 0;
}

/// <summary>契约 ↔ 库里读数 的比对（列级 diff 那一把尺）。比的是归一后的结构事实、不是类型文本：
/// 发射侧的类型名一律大写而目录视图一律小写，DATETIME2(3) 发出去目录回 datetime2 加标度，
/// 手工建的表上 DECIMAL 读回 numeric——同一型而文本不同，逐字符比文本的尺子第一天就假红。
/// 所以这一格只问两件事：可空性（Core 自己比，且比的是 NullableInStore 而不是 IsNullable）与形状
/// （只有本家知道怎么折，交回 ISchemaReader.ColumnShapeMatches）。名字怎么配走这一家的 IdentifierPolicy：
/// 不敏感的家用忽略大小写的比较器，敏感的家用逐字符，折大写的家先把两侧一起折上去再比；
/// 判据与发射层共用同一颗字段，"建出来的那一列"与"读回来的那一列"不可能配上两个名字。</summary>
public static class StructureDiff
{
    /// <summary>比对并产出计划。dropIndexes 默认 false：库里多出的索引要不要 DROP 由调用方显式开
    /// （SchemaSyncDropIndexes），关掉时那条只进"要人工"清单——它不是"更严格的 diff"，是风险档不同：
    /// 补一列不丢数据，删一条索引会当场拖慢正在跑的查询。</summary>
    public static StructurePlan Build(
        EntityContract contract,
        SchemaTable actual,
        ISchemaReader reader,
        IdentifierPolicy identifiers,
        bool dropIndexes = false)
    {
        var names = new Names(identifiers);
        var notJudged = new List<string>();
        var refused = new List<string>();
        var adds = new List<ColumnContract>();
        var alters = new List<ColumnContract>();
        var tightenings = new List<ColumnContract>();
        var comments = new List<ColumnContract>();
        var indexes = new List<IndexContract>();
        var drops = new List<SchemaIndex>();
        var shapeBlind = new List<string>();
        var commentBlind = new List<string>();

        var byName = new Dictionary<string, SchemaColumn>(names.Comparer);

        foreach (var column in actual.Columns)
            byName[names.Key(column.Name)] = column;

        var declaredNames = new HashSet<string>(names.Comparer);

        foreach (var column in contract.Columns)
            declaredNames.Add(names.Key(column.ColumnName));

        ComparePrimaryKey(contract, actual, names, refused, notJudged);

        foreach (var expected in contract.Columns)
        {
            if (!byName.TryGetValue(names.Key(expected.ColumnName), out var stored))
            {
                AddOrRefuse(expected, adds, comments, refused);

                continue;
            }

            var shape = reader.ColumnShapeMatches(expected, stored);

            if (shape is null)
                shapeBlind.Add(expected.ColumnName);

            var nullabilityDiffers = expected.NullableInStore != stored.IsNullable;
            var differs = nullabilityDiffers || shape == false;
            var storeOwned = expected.IsIdentity || expected.ValueFromStore;

            if (differs)
            {
                if (expected.IsPrimary)
                    refused.Add($"列 {expected.ColumnName} 的形状与契约不同，而它是主键的一员：ALTER 它会连主键约束一起塌，这一趟不做（要人工）");
                else if (storeOwned)
                    refused.Add($"列 {expected.ColumnName} 的形状与契约不同，而它的值归库给（自增 / 库自己递增的那一型）：ALTER 它会抹掉取值机制，这一趟不做（要人工）");
                else
                {
                    alters.Add(expected);

                    // 收紧方向单独记一笔（判据与上面那一格同一颗，不另立第二处）：这一句 ALTER 发下去
                    // 会要库对存量行担保 NOT NULL，而存量里有没有 NULL 只有库知道。
                    if (nullabilityDiffers && !expected.NullableInStore)
                        tightenings.Add(expected);
                }
            }

            // 备注那一格只在契约说过时才问：null 说的是"用户没写备注"，不是"要求库里清掉备注"。
            var commentDiffers = expected.Comment is { } wanted
                                  && stored.Comment is { } read
                                  && !string.Equals(wanted, read, StringComparison.Ordinal);

            if (expected.Comment is not null && stored.Comment is null)
                commentBlind.Add(expected.ColumnName);

            // 被 ALTER 过的那一列连带重发备注（判据写在 StructurePlan.ColumnComments 上），
            // 而被拒的那几列根本没被 ALTER，所以只有"备注本身变了"这一条把它们带进清单。
            if (expected.Comment is not null && (commentDiffers || (differs && !expected.IsPrimary && !storeOwned)))
                comments.Add(expected);
        }

        foreach (var stored in actual.Columns)
            if (!declaredNames.Contains(names.Key(stored.Name)))
                refused.Add($"库里多出一列 {stored.Name}（{stored.StoreType}）：结构同步不删列，删列会带走那一列的数据（要人工）");

        var tableComment = false;

        if (contract.Table.Comment is { } declared)
        {
            if (actual.Comment is null)
                notJudged.Add("表备注：这一家读不回来，所以这一趟判不出它变没变（写侧照发，下一趟仍判不出）");
            else
                tableComment = !string.Equals(declared, actual.Comment, StringComparison.Ordinal);
        }

        CompareIndexes(contract, actual, names, dropIndexes, indexes, drops, refused, notJudged);

        if (shapeBlind.Count > 0)
            notJudged.Add($"列形状这一档本家判不了，共 {shapeBlind.Count} 列：{string.Join(", ", shapeBlind)}");

        if (commentBlind.Count > 0)
            notJudged.Add($"列备注这一档本家读不回来，共 {commentBlind.Count} 列：{string.Join(", ", commentBlind)}（写了判不出下一趟的差别）");

        return new StructurePlan(contract.Table)
        {
            AddColumns = [.. adds],
            AlterColumns = [.. alters],
            NullabilityTightenings = [.. tightenings],
            ColumnComments = [.. comments],
            EmitTableComment = tableComment,
            CreateIndexes = [.. indexes],
            DropIndexes = [.. drops],
            NotJudged = [.. notJudged],
            Refused = [.. refused],
        };
    }

    /// <summary>缺列那一档：能加的就加，加不了的点名。三种拒都在这里判，因为三种都是
    /// "发出去要么撞库要么留下半套状态"：主键列要重建约束；自增列 T-SQL 压根不允许加到已有表上；
    /// NOT NULL 而库里已有行没有值可填——除非契约配了默认值，
    /// ADD COLUMN … DEFAULT … NOT NULL 让库自己把存量行填上。</summary>
    static void AddOrRefuse(
        ColumnContract expected,
        List<ColumnContract> adds,
        List<ColumnContract> comments,
        List<string> refused)
    {
        if (expected.IsPrimary)
        {
            refused.Add($"主键列 {expected.ColumnName} 在库里没有：补一列主键要连约束一起重建，这一趟不做（要人工）");
            return;
        }

        if (expected.IsIdentity)
        {
            refused.Add($"列 {expected.ColumnName} 在库里没有，而它是自增列：往一张已有的表上加自增列，这一家不允许（要人工）");
            return;
        }

        // 值归库给的那一型（rowversion）在这里例外：库里已有的行由库自己填上值，NOT NULL 撞不出错。
        // NOT NULL 且没配默认值照旧拒；配了默认值就走 ADD —— DefaultValue 的值域门禁在契约收尾那一格
        // （G1 ∶ G2 共用），能走到这里的默认值一定是常量字面量档。
        if (!expected.NullableInStore && !expected.ValueFromStore && expected.DefaultValue is null)
        {
            refused.Add($"列 {expected.ColumnName} 在库里没有，而契约要它 NOT NULL：库里已有的行没有值可填。" +
                        "出路两选一：给这一列配默认值（Fluent HasDefaultValue / [DefaultValue]，发 ADD COLUMN … DEFAULT … NOT NULL），" +
                        "或先按可空建、回填那一列，再把 NOT NULL 交给人");
            return;
        }

        adds.Add(expected);

        if (expected.Comment is not null)
            comments.Add(expected);
    }

    /// <summary>主键只判、只报，永远不改：那一档要连外键与索引一起重建。
    /// 读不出主键列的那些家在这一格交回"判不了"而不是"没问题"——两侧都没读过主键就没人能声称它们一致。</summary>
    static void ComparePrimaryKey(
        EntityContract contract,
        SchemaTable actual,
        Names names,
        List<string> refused,
        List<string> notJudged)
    {
        if (actual.Columns.Count == 0 || actual.Columns.Any(static c => c.IsPrimaryKey is null))
        {
            notJudged.Add("主键列：这一家读不回来，判不了");
            return;
        }

        var declared = string.Join(",", contract.PrimaryKey.Select(c => names.Key(c.ColumnName)));
        var read = string.Join(",", actual.Columns.Where(static c => c.IsPrimaryKey == true).OrderBy(static c => c.OrdinalPosition).Select(c => names.Key(c.Name)));

        if (string.Equals(declared, read, StringComparison.Ordinal))
            return;

        refused.Add($"主键不一致：契约 ({declared}) / 库里 ({read})。改主键要重建约束与引用它的每一张表，这一趟不做（要人工）");
    }

    /// <summary>索引那一档：缺省只补建、不 DROP、不改名；开了 dropIndexes 才把库里多出的那一条进删除清单。
    /// 契约里没有那条索引可能只是"这一册实体没覆盖到那张表的所有查询"，而删掉一条正在被人用的索引会
    /// 当场拖慢查询，所以那一档要调用方显式开。唯一约束即使开了那一档也不删：它不是索引，DROP INDEX
    /// 删不动约束，发出去只会以报错收尾，拆它要走 ALTER TABLE … DROP CONSTRAINT，与改主键同一处境，
    /// 所以它进 Refused 并指名它是约束。同列序列换名算同一条（人改的多半是名字不是形状）。</summary>
    static void CompareIndexes(
        EntityContract contract,
        SchemaTable actual,
        Names names,
        bool dropIndexes,
        List<IndexContract> indexes,
        List<SchemaIndex> drops,
        List<string> refused,
        List<string> notJudged)
    {
        if (actual.Indexes is not { } read)
        {
            notJudged.Add("索引清单：这一家读不回来，补建索引那一档判不了");
            return;
        }

        var readByKey = new Dictionary<string, SchemaIndex>(StringComparer.Ordinal);
        var readByName = new Dictionary<string, SchemaIndex>(names.Comparer);

        foreach (var index in read)
        {
            // 匹配键带唯一性：只比列序列的话，契约里的非唯一索引会与库里同列序的唯一索引判成"已存在"，
            // 唯一性差异静默放过（反向同理）。
            readByKey[IndexKey(names.Join(index.Columns), index.IsUnique)] = index;
            readByName[names.Key(index.Name)] = index;
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wanted in contract.Indexes)
        {
            var key = names.Join(wanted.ColumnNames);

            if (readByKey.TryGetValue(IndexKey(key, Unique(wanted)), out var same))
            {
                matched.Add(names.Key(same.Name));
                continue;
            }

            // 列序列相同、唯一性相反：既不判"已存在"也不盲建一条同列索引（同一组列两颗索引只是双倍写放大），
            // 改唯一性要走 DROP + CREATE，点名要人工。
            if (readByKey.TryGetValue(IndexKey(key, !Unique(wanted)), out var flipped))
            {
                matched.Add(names.Key(flipped.Name));
                refused.Add($"索引 {wanted.Name} 在库里已有同列序但唯一性相反的那一条（{flipped.Name}）：改唯一性不是补一条索引能解决的（要人工）");
                continue;
            }

            if (readByName.TryGetValue(wanted.Name, out var clash))
            {
                // 撞名的那一条在这里已经点过名，不能再算一次"库里多出一条索引"：同一件事报两遍，
                // 看报告的人会以为要人工处理两格，而库里只有那一颗同名索引。
                matched.Add(names.Key(clash.Name));
                refused.Add($"索引 {wanted.Name} 在库里已经指向别的列序列：既不改它也不建撞名的那一条（要人工）");
                continue;
            }

            indexes.Add(wanted);
        }

        foreach (var index in read)
        {
            if (matched.Contains(names.Key(index.Name)))
                continue;

            if (index.IsConstraint)
            {
                refused.Add($"库里多出一条唯一约束 {index.Name}（{string.Join(",", index.Columns)}）：它不是索引，" +
                            "DROP INDEX 删不动，要拆得走 ALTER TABLE … DROP CONSTRAINT（要人工）");
                continue;
            }

            if (dropIndexes)
                drops.Add(index);
            else
                refused.Add($"库里多出一条索引 {index.Name}（{string.Join(",", index.Columns)}）：结构同步不 DROP 索引，" +
                            "那会拖慢正在跑的查询（要人工。出路：开 SchemaSyncDropIndexes）");
        }
    }

    /// <summary>索引匹配键：列序列 + 唯一性。唯一性进键——同列序的唯一/非唯一两条索引不是同一个约束形状。</summary>
    static string IndexKey(string columns, bool unique) => unique ? "u:" + columns : "n:" + columns;

    /// <summary>契约侧的唯一性读法：IndexKind.Unique 即唯一；唯一约束在 SchemaIndex.IsConstraint
    /// 那一侧记账，不进这里。</summary>
    static bool Unique(IndexContract index) => index.Kind == IndexKind.Unique;
}

/// <summary>名字在这家库里的配法（大小写那一格在 diff 侧的读数）。</summary>
readonly struct Names(IdentifierPolicy policy)
{
    public StringComparer Comparer => policy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public string Key(string name) => policy.FoldToUpperCase ? name.ToUpperInvariant() : name;

    public string Join(IReadOnlyList<string> columnNames) => string.Join(',', columnNames.Select(Key));
}
