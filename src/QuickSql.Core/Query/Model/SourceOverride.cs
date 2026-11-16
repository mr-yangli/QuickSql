using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Query.Model;

/// <summary>FROM 覆盖口的两种形状收成一个 closed union（C# 15，2026-11-11）：AST 的 TableSource 同日对外
/// closed，而这一格的两种来路都是模型层自己的东西，不再借 AST 的基座 —— 分派（QueryCompiler）与打印
/// （QueryModelPrinter）因此各拿一份能穷尽的形状清单。AST 那侧的 SourceRawText 仍在：它是发射层的形状，
/// 编译口把 RawTextSource 端成它。</summary>
public closed record OverriddenSource(TableRef Ref);

/// <summary>WithSql 交回的一段用户文本 + 参数清单。与 AST 的 SourceRawText 同形不同座：那一颗活在
/// 发射层，这一颗活在模型层。</summary>
public sealed record RawTextSource(TableRef Ref, string Sql, IReadOnlyList<AdoParameter> Parameters) : OverriddenSource(Ref);

/// <summary>链式合并（UnionAll/Union/Intersect/Except）的 FROM 位形状：两条同型的腿按
/// SetOperationKind 指的关键字串起来，整串占一个表位 —— 与分表读侧同一种 SQL 形状，只是腿的
/// 来源不同：分表的腿由选片机器造，这里的腿是调用方各自链出来的两条完整查询。腿是模型不是
/// AST：编译口走到 FROM 位时才把两条腿各自编译成 SelectStatement，腿里的谓词、过滤器、集群
/// 分派按腿自己的路走完一遍；形状守卫（同型整行、无序无页、无急切加载、不套娃）住在链式
/// 入口，这里只管编译。（2026-11-11 前它直接挂 AST 的 TableSource 底下，同日随 AST closed 收进本 union。）</summary>
public sealed record SourceSetOps(TableRef Ref, QueryModel First, QueryModel Second, Sql.Ast.SetOperationKind Operation) : OverriddenSource(Ref);
