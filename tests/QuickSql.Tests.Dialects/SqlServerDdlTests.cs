using System.Text;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary>CodeFirst 的 DDL 文本（§9.3 的类型映射 + §10.2 的约定层）。这些句子是要在库里执行的，
/// 括号多一个就整批失败，所以和查询一样进黄金样本 —— "看着差不多"在 DDL 上等于建不出表。</summary>
public sealed class SqlServerDdlTests
{
    static string Ddl<TEntity>() where TEntity : class
    {
        var orm = SqlServer.Orm();
        var contract = orm.Schemas.Contract<TEntity>();
        var writer = new SqlWriter(new StringBuilder(), [], orm.Provider.Dialect.ParameterPrefix);

        orm.Provider.CodeFirst.EmitCreateTable(writer, contract);

        return writer.ToSqlString();
    }

    [Fact]
    public void Create_table_writes_columns_in_contract_order_with_the_provider_types()
    {
        var sql = Ddl<Order>();

        Golden.Verify("ddl-order", sql);

        // 回归：左括号在开列清单那一行已经写过了，这里再写一次就得到 CREATE TABLE X ((...))。
        Checks.Absent("((", sql);

        Checks.Has("CREATE TABLE [dbo].[QuickSqlOrder] (", sql);
        Checks.Has("[Id] INT IDENTITY(1,1) NOT NULL", sql);
        Checks.Has("[CustomerName] NVARCHAR(64) NULL", sql);
        Checks.Has("[Amount] DECIMAL(18,4) NOT NULL", sql);
        Checks.Has("[CreatedAt] DATETIME2(3) NOT NULL", sql);
        Checks.Has("[PaidAt] DATETIME2(3) NULL", sql);
        Checks.Has("CONSTRAINT [PK_QuickSqlOrder] PRIMARY KEY CLUSTERED ([Id])", sql);
    }

    [Fact]
    public void Enums_take_the_int_storage_and_navigation_properties_stay_out_of_the_table()
    {
        var sql = Ddl<Order>();

        // 枚举存 INT（§9.3）：留给绑定层转换，库里没有 CLR 类型这一说。
        Checks.Has("[State] INT NOT NULL", sql);

        // 引用导航不是列（v0.20 把它折成契约里的一根边，而不是表里的一格），集合导航更不是。
        // 这句问的是"导航有没有漏进列清单"，所以整段文本里都不该出现这两个成员名。
        Checks.Absent("[Customer]", sql);
        Checks.Absent("[Items]", sql);

        // 关系这一格今天<em>不</em>落进 DDL：CodeFirst 不发 FOREIGN KEY 约束（§1.1 那张取舍表、§14 记账）。
        // 这不是漏 —— 集成 fixture 故意留了一行没有父行的孤儿子（§13.6 的 LEFT JOIN 见证），
        // 真加了约束它就插不进去。约束那一头的证据在库里手工加（集成层 OrderingPathTests）。
        // 「边没漏成列」不必在这里数：上面那条 Golden.Verify("ddl-order") 把整段文本逐字符钉着。
        Checks.Absent("FOREIGN KEY", sql);
        Checks.Absent("REFERENCES", sql);
    }

    [Fact]
    public void Declared_indexes_become_a_separate_t_sql_statement_in_the_same_batch()
    {
        var sql = Ddl<Order>();

        // T-SQL 的 CREATE INDEX 不能并进 CREATE TABLE 的括号里，同批次只能用 ';' 串。
        Checks.Has(" ; CREATE INDEX [IX_QuickSqlOrder_CreatedAt] ON [dbo].[QuickSqlOrder]", sql);
        Checks.Has("[CreatedAt] ASC )", sql);

        // 没声明索引的实体不该长出第二条语句。
        Checks.Absent("CREATE INDEX", Ddl<Customer>());
    }

    [Fact]
    public void String_columns_without_a_declared_length_fall_to_max()
    {
        var order = Ddl<Order>();
        var customer = Ddl<Customer>();

        Golden.Verify("ddl-customer", customer);

        // [QuickColumn(Length = 64)] 与不写长度的两套兜底必须在同一份文本里对得上（§10.4 的三档长度）。
        Checks.Has("[Name] NVARCHAR(64) NULL", customer);
        Checks.Has("[Remark] NVARCHAR(32) NULL", customer);
        Checks.Has("[Note] NVARCHAR(MAX) NULL", order);
    }
}
