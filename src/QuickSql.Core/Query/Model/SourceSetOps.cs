using QuickSql.Sql.Ast;

namespace QuickSql.Query.Model;

/// <summary>链式合并（UnionAll/Union/Intersect/Except）的 FROM 位形状：两条同型的腿按
/// SetOperationKind 指的关键字串起来，整串占一个表位 —— 与分表读侧同一种 SQL 形状，只是腿的
/// 来源不同：分表的腿由选片机器造，这里的腿是调用方各自链出来的两条完整查询。腿是模型不是
/// AST：编译口走到 FROM 位时才把两条腿各自编译成 SelectStatement，腿里的谓词、过滤器、集群
/// 分派按腿自己的路走完一遍；形状守卫（同型整行、无序无页、无急切加载、不套娃）住在链式
/// 入口，这里只管编译。</summary>
public sealed record SourceSetOps(TableRef Ref, QueryModel First, QueryModel Second, Sql.Ast.SetOperationKind Operation) : Sql.Ast.TableSource(Ref);
