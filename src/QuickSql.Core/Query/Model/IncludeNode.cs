using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Query.Model;

/// <summary>急切加载（Include）的树节点。每个节点代表一条导航属性的加载意图，
/// 子节点表示 ThenInclude 链。编译器按深度优先遍历生成 LEFT JOIN 与扩展列，
/// 物化器按同一顺序读取并设置导航属性。</summary>
public sealed class IncludeNode
{
    /// <summary>导航属性名（如 "Customer"、"OrderDetails"）。</summary>
    public required string NavigationMemberName { get; init; }

    /// <summary>已解析的关系事实（FK 列名、目标类型）。</summary>
    public required RelationshipContract Relationship { get; init; }

    /// <summary>目标表位（已分配别名 tN）。</summary>
    public required TableRef TargetTable { get; init; }

    /// <summary>JOIN 条件（FK = PK 的等值连接）。</summary>
    public required SqlExpression OnCondition { get; init; }

    /// <summary>子节点（ThenInclude 链）。</summary>
    public List<IncludeNode> Children { get; } = [];

    /// <summary>目标实体的列数（从 TargetContract.Columns.Length 取）。</summary>
    public int ColumnCount => TargetTable.Contract!.Columns.Length;

    /// <summary>在结果集中的列偏移（编译器计算，物化器读取）。根实体的列从 0 开始，
    /// 每个 Include 的列紧随其后。深度优先顺序。</summary>
    public int ColumnOffset { get; set; }
}
