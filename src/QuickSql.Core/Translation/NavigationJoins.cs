using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Query;
using QuickSql.Query.Model;
using QuickSql.Sql.Ast;

namespace QuickSql.Translation;

/// <summary>一条「按引用导航补进 FROM 的那张表」的事实（自动联表）。HostIndex 用宿主表位在 FROM 序列里的
/// 位置当身份，而不是 TableRef 实例：同一个表位在派生出来的每段模型里确实是同一个对象，但两句模型之间
/// "位置相同"才是那句 SQL 里读得出来的判据（别名由位置生成），拿实例相等当判据会把"位置对上了但对象是
/// 新造的那一份"这一形误判成没联过。Member 是宿主契约上那根引用导航的属性名——判同要它：
/// BillingCustomer 与 ShippingCustomer 的宿主同一张、目标类型同一个，只差就是这个名字。</summary>
internal readonly record struct NavigationJoin(int HostIndex, string Member, TableRef Table, JoinClause Clause);

/// <summary>引用导航读成员时补的那一条 LEFT JOIN（自动联表那一档）。这一颗是每颗 lambda 一份的翻译现场：
/// _joined 是这条链上已经进 FROM 的那些（只读，来自 QueryEngine），_pending 是这一颗 lambda 里新补的，
/// 等算子把它们落进模型。发 LEFT 而不是 INNER：这一档的来路是"读那条边的某一列"，o.Customer.City 在
/// Customer 空着时读出来是 NULL（与 Include 交回 null 导航同一件事），INNER 会让同一句投影在外键为 NULL
/// 或指向已删行的两种数据上少行，而少行在投影结果里读不出来。ON 直接用 Include 那一份 BuildJoinCondition：
/// 一条导航怎么联全仓库只许有一个答案（外键列序对目标主键列序那一格尤其脆，复合键错一位就是语法正确、
/// 结果错列的 SQL）。集合导航不在这档：IEnumerable 边联进 FROM 会让主表行按明细行数重复，归 IncludeMany
/// 的两条查询。RUC/RDC 标注与 ExpressionTranslator 同一份理由：它落在表达式翻译的调用链上。</summary>
[RequiresUnreferencedCode("按运行期 Type 向注册表取目标实体的契约。")]
[RequiresDynamicCode("目标实体的契约由注册表按 Type 解析（G2）。")]
internal sealed class NavigationJoinScout(IReadOnlyList<NavigationJoin> joined, int indexBase, Func<Type, int, TableRef> newTableRef,
    QueryModel model)
{
    /// <summary>欠着的这几张，第一次补出联表时才新建：绝大多数 lambda 一个导航都不读，而这一颗是
    /// 每颗 lambda 一份的，List 的价钱会乘在整个读路径的算子次数上。</summary>
    List<NavigationJoin>? _pending;

    /// <summary>这一颗 lambda 里补出来、还没进模型的那些。落模型由算子做，不是这里——QueryModel 是不可变的，
    /// 只有手里握着 Derive 的那一层能决定"这一份新模型去哪儿"。</summary>
    internal IReadOnlyList<NavigationJoin> Pending => _pending ?? [];

    /// <summary>有没有欠账 = 有没有人读过引用导航，所以这一判不另付钱。</summary>
    internal bool HasPending => _pending is not null;

    /// <summary>把 host 上那根叫 member 的引用导航落成一张表位：已经补过就交回同一张（链级 joined 与本颗
    /// _pending 两处都要问，后者是同一颗 lambda 里读了两次 o.Customer.City 那一形），没补过才新建表位 +
    /// 一条 LEFT JOIN。找不到那条边时返回 null 而不是抛：调用方手里有三种"认不出"各自的理由（集合导航、
    /// 未映射的计算成员、目标实体没注册），那些话在 ExpressionTranslator.Navigation 那一句里已按类型分好了档。</summary>
    internal TableRef? Resolve(TableRef host, string member)
    {
        foreach (var join in joined)
            if (join.HostIndex == host.Index && join.Member == member)
                return join.Table;

        if (_pending is { } pending)
        {
            foreach (var join in pending)
                if (join.HostIndex == host.Index && join.Member == member)
                    return join.Table;
        }

        if (host.Contract is not { } contract
            || Relationship(contract, member) is not { } relationship)
            return null;

        // Include 已登记过这根边就收养它那一颗表位（同一根边上只留一张 JOIN）。收养的是表位、不是照抄
        // Include 那条 JOIN：这里落进 _pending 的是模型里的真 JOIN，而 Include 那条只在"实体投影且没拆查"时
        // 才进这一句——照抄过去，投影/拆查/count 内句会把这颗表位从 FROM 摘掉，欠着它的谓词就引用一张
        // 不存在的表。落成模型里的一张，三处派生句子都带着它。
        if (AdoptedInclude(host, member) is { } adopted)
        {
            (_pending ??= []).Add(adopted);

            return adopted.Table;
        }

        // 位置往后长：joined 那一份已经在 Tables 里，_pending 这一颗里的每一张也都将在，
        // 所以下一张的位置 = 进来时的表位数 + 已经欠着的那几张。
        var table = newTableRef(relationship.TargetType, indexBase + (_pending?.Count ?? 0));
        var clause = new JoinClause(JoinKind.Left, new SourceTable(table),
            QueryEngine.BuildJoinCondition(host, relationship, table));

        (_pending ??= []).Add(new NavigationJoin(host.Index, member, table, clause));

        return table;
    }

    /// <summary>宿主这一格的这根边是不是 Include 树里已经登记过的一根，是就把它折成一条待落地的
    /// NavigationJoin。按宿主表位的位置配而不是按实例，与 NavigationJoin.HostIndex 那条纪律同一把尺
    /// （Shard() 会把根表位换成一个新实例，按实例配在这一档会漏认）。ThenInclude 的宿主是它父节点那一颗
    /// 表位，所以树是一层一层往下走的。只问 QueryModel.Includes：IncludeMany 那家挂在 IncludeManyNode.References
    /// 上、两侧索引天生是 0，它不进这一句的 FROM，收养不得。</summary>
    NavigationJoin? AdoptedInclude(TableRef host, string member)
        => model.Includes.Count == 0 || model.Plan is not EntityProjection entity
            ? null
            : FindIncludeEdge(model.Includes, model.Tables[entity.TableIndex], host, member);

    static NavigationJoin? FindIncludeEdge(IReadOnlyList<IncludeNode> nodes, TableRef parent, TableRef host, string member)
    {
        foreach (var node in nodes)
        {
            if (parent.Index == host.Index && node.NavigationMemberName == member)
                return new NavigationJoin(host.Index, member, node.TargetTable,
                    new JoinClause(JoinKind.Left, new SourceTable(node.TargetTable), node.OnCondition));

            if (FindIncludeEdge(node.Children, node.TargetTable, host, member) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>把欠着的那几条联表落进模型。顺序 = 补出来的顺序（AddJoin 一颗一颗追加），同一颗 lambda 里
    /// o.Customer.City 与 o.Manager.City 谁先出现，FROM 后面就谁先站。收养来的那张只加 JOIN、不再加表位：
    /// 那颗 TableRef 是 AddInclude 早就放进 Tables 的同一颗，再放一遍 Tables 里就有两个同 Index 的槽位，
    /// 而按位置绑形参的 Scope.Bound 与往后长的别名编号读的都是这一份清单。</summary>
    internal QueryModel Flush(QueryModel model)
    {
        if (_pending is { } pending)
            foreach (var join in pending)
                model = Has(model.Tables, join.Table.Index)
                    ? model with { Joins = [.. model.Joins, join.Clause] }
                    : model.AddJoin(join.Clause, join.Table);

        return model;
    }

    static bool Has(IReadOnlyList<TableRef> tables, int index)
    {
        foreach (var table in tables)
            if (table.Index == index)
                return true;

        return false;
    }

    static RelationshipContract? Relationship(EntityContract contract, string member)
    {
        foreach (var relationship in contract.Relationships)
            if (relationship.MemberName == member)
                return relationship;

        return null;
    }
}
