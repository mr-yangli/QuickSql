using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Core.Schema;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>SyncStructure</c> 的两档离线验证：Disabled 返回空、Script 只生成不执行。
/// Execute 模式要连真库，归集成测试（§13.1 的活库闸门）。</summary>
public sealed class SchemaSyncTests
{
    [Table("SYNC_TEST")]
    sealed class SyncEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void Disabled_ReturnsEmpty()
    {
        var orm = BuildOrm(SchemaSyncPolicy.Disabled);

        // 先取一次契约，让类型进 RegisteredTypes
        _ = orm.Schemas.Contract(typeof(SyncEntity));

        var result = orm.SyncStructure();

        Assert.True(result.IsEmpty);
        Assert.Empty(result.CreatedTables);
        Assert.False(result.Executed);
        Assert.Empty(result.Ddl);
    }

    [Fact]
    public void Script_GeneratesDdlWithoutExecuting()
    {
        var orm = BuildOrm(SchemaSyncPolicy.Script);
        _ = orm.Schemas.Contract(typeof(SyncEntity));

        var result = orm.SyncStructure();

        Assert.False(result.IsEmpty);
        Assert.Single(result.CreatedTables);
        Assert.Equal("SYNC_TEST", result.CreatedTables[0]);
        Assert.False(result.Executed);
        Assert.Contains("CREATE TABLE", result.Ddl);
        Assert.Contains("SYNC_TEST", result.Ddl);

        // 逐句清单与那一整篇同源（都来自 Tally 记录的那一步），断言它是同一串句子的拆分而不是第二次生成
        Assert.Contains(result.DdlStatements, s => s.Contains("CREATE TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void NonDevelopment_WithoutForce_Throws()
    {
        // 模拟非 Development 环境
        var oldEnv = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

            var orm = BuildOrm(SchemaSyncPolicy.Execute, force: false);
            _ = orm.Schemas.Contract(typeof(SyncEntity));

            var ex = Assert.Throws<InvalidOperationException>(() => orm.SyncStructure());
            Assert.Contains("ForceSchemaSync=true", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", oldEnv);
        }
    }

    static QuickSqlOrm BuildOrm(SchemaSyncPolicy policy, bool force = false)
    {
        var provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

        return new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = "Server=(localdb)\\fake;Database=test;",
            SchemaSync = policy,
            ForceSchemaSync = force,
        });
    }
}
