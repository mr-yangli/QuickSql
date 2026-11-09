using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的乐观并发。比较式与另六家逐字同形（"Version" = @p），分岔只在谁负责让那一格变，
/// 本家落"应用给值"那一侧。本家没有 rowversion 那一档：这一族的型名不参与存储，写
/// [Column(TypeName = "rowversion")] 得到的是一列普通列、不会自增（"由库给值"只有 AUTOINCREMENT
/// 与触发器两个入口，前者只作用 rowid、本仓库没有触发器形状），所以也不为它立拒收闸门——
/// 这里没有"DDL 绿而读即红"的形状可挡。时间戳在本家是文本，并发列比较的是那个时刻的写法
/// 而不是时刻本身：别的写入方留的行打不中，那是假冲突（每次保存都撞），不是丢写；
/// 任何"与非本仓库写入方共享一张表"的场景第一天就会撞它。本家没有行级锁（SELECT … FOR UPDATE
/// 是语法错），"冲突"的真实形状是两个写者撞在同一颗文件上时立刻抛错。</summary>
public sealed class SqliteConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另六家同一个调用点（发射之前合取进 AST 的 WHERE）。
    /// 空值那一档由调用方先折成 IS NULL 再下来："col" = :p 在 NULL 上恒为 UNKNOWN——
    /// 不折的话那一行每次保存都成一次"冲突"。本家多一处要小心的：版本列是文本时，
    /// 比较的是写法而不是时刻，所以"这一列能不能当版本列"取决于它是否只由本仓库写过。</summary>
    public SqlExpression BuildPredicate(
        SqlExpression target,
        ColumnContract column,
        object? originalValue
    ) =>
        new SqlBinaryExpression(
            BinaryOperator.Equal,
            target,
            new ParameterValue(originalValue, column)
        );
}
