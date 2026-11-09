using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Providers.Sqlite;
using Xunit;

namespace QuickSql.Tests.Integration;

/// <summary>本轮（SQLite 家读侧两档）的临时侦察：只打印读数，不做断言。跑完即删。</summary>
public sealed class ScratchSqliteReconTests
{
    const string Parent = "QuickSqlReconSParent";

    const string Child = "QuickSqlReconSChild";

    static readonly ISchemaReader Reader = new SqliteSchemaReader();

    [Fact]
    public async Task ReconAsync()
    {
        if (Database.Family != Database.Target.Sqlite)
            Assert.Skip("只有 sqlite 一支要量");

        Line("== Q 段：SQLite 家索引侧的读数 ==");

        Drop();

        Database.Execute($"""
            CREATE TABLE "{Parent}"(
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Code" TEXT NOT NULL UNIQUE,
                "Wide" TEXT NULL)
            """);

        Database.Execute($"""
            CREATE TABLE "{Child}"(
                "Id" INTEGER NOT NULL,
                "Parent" INTEGER NOT NULL,
                "Title" VARCHAR(32) NULL,
                "Amount" NUMERIC(10,2) NULL,
                "Blob" BLOB NULL,
                "Tiny" TINYINT NULL,
                CONSTRAINT "PK_QuickSqlReconSChild" PRIMARY KEY ("Parent", "Id"))
            """);

        Database.Execute($"""CREATE INDEX "IX_recon_child_title" ON "{Child}" ( "Title" ASC )""");
        Database.Execute($"""CREATE UNIQUE INDEX "IX_recon_child_blob" ON "{Child}" ( "Blob" DESC )""");
        Database.Execute($"""CREATE INDEX "IX_recon_child_partial" ON "{Child}" ( "Title" ASC ) WHERE "Title" IS NOT NULL""");
        Database.Execute($"""CREATE INDEX "IX_recon_child_expr" ON "{Child}" ( length( "Title" ) )""");
        Database.Execute($"""CREATE INDEX "IX_recon_parent_code" ON "{Parent}" ( "Code" ASC )""");

        Line("Q-01 两张靶表 + 五条索引建=OK");

        Fields("Q-02", $"""SELECT * FROM pragma_index_list('{Child}')""");
        Fields("Q-03", $"""SELECT * FROM pragma_index_list('{Child}', 'main')""");
        Fields("Q-04", $"""SELECT * FROM pragma_index_list('{Parent}')""");

        foreach (var index in new[] { "IX_recon_child_title", "IX_recon_child_blob", "IX_recon_child_partial", "IX_recon_child_expr" })
        {
            Fields("Q-05", $"""SELECT * FROM pragma_index_info('{index}')""");
            Fields("Q-06", $"""SELECT * FROM pragma_index_xinfo('{index}')""");
        }

        try
        {
            Fields("Q-07", $"""SELECT * FROM pragma_index_xinfo('IX_recon_child_title', 'main')""");
        }
        catch (Exception e)
        {
            Line($"Q-07 ERR :: index_xinfo 二参 ⟶ {e.GetType().Name}: {e.Message}");
        }

        try
        {
            Fields("Q-08", $"""SELECT * FROM pragma_index_info('sqlite_autoindex_{Parent}_1')""");
        }
        catch (Exception e)
        {
            Line($"Q-08 ERR :: autoindex 的 index_info ⟶ {e.GetType().Name}: {e.Message}");
        }

        var reader = Reader;

        using (var connection = Open())
        {
            foreach (var name in new[] { Parent, Child })
            {
                var read = (await reader.ReadTableAsync(connection, new TableInfo(null, name), TestContext.Current.CancellationToken))!;

                Line($"Q-09 table={name} :: comment={read.Comment ?? "null"} :: indexes=" +
                    (read.Indexes is null ? "null" : string.Join(" ∶ ", read.Indexes.Select(i =>
                        $"{i.Name}|unique={i.IsUnique}|constraint={i.IsConstraint}|[{string.Join(",", i.Columns)}]"))));

                foreach (var column in read.Columns)
                    Line($"Q-10 col {column.Name} :: store=[{column.StoreType}] :: chars={column.MaxChars?.ToString() ?? "-"} :: " +
                        $"bytes={column.MaxBytes?.ToString() ?? "-"} :: null={column.IsNullable} :: pk={column.IsPrimaryKey?.ToString() ?? "-"} :: " +
                        $"identity={column.IsIdentity?.ToString() ?? "-"}");
            }
        }

        foreach (var (emitted, fromCatalog) in new (string, string)[]
        {
            ("VARCHAR(32)", "VARCHAR(32)"),
            ("VARCHAR(32)", "varchar(32)"),
            ("VARCHAR(32)", "varchar( 32 )"),
            ("TEXT", "TEXT"),
            ("TEXT", "text"),
            ("INTEGER", "INT"),
            ("INTEGER", "integer"),
            ("BIGINT", "INTEGER"),
            ("NUMERIC(10,2)", "NUMERIC(10, 2)"),
            ("DATETIME", "TEXT"),
        })
            Line($"Q-11 fold {emitted} vs [{fromCatalog}] => {reader.ColumnShapeMatches(Declared(emitted), Col(fromCatalog))?.ToString() ?? "-"}");

        Try("Q-12", $"""DROP INDEX "IX_recon_parent_code" """);
        Try("Q-13", $"""DROP INDEX "main"."IX_recon_child_title" """);
        Try("Q-14", $"""DROP INDEX "IX_recon_child_partial" """);
        Try("Q-15", $"""DROP INDEX "IX_recon_child_expr" """);
        Try("Q-16", $"""DROP INDEX "sqlite_autoindex_{Parent}_1" """);
        Try("Q-17", $"""DROP INDEX "ix_does_not_exist" """);

        // temp 那一档要问在<em>同一颗连接</em>上：temp 对象住在连接里，而驱动按连接串池化，
        // 换一颗连接去问就是"这张表不存在"。
        using (var connection = Open())
        {
            Same(connection, "Q-18", """CREATE TEMP TABLE "sQtemp"("A" INTEGER, "B" TEXT)""");
            Same(connection, "Q-18b", """CREATE INDEX "IX_sQtemp_A" ON "sQtemp" ( "A" ASC )""");

            Bound(connection, "Q-20", "SELECT \"seqno\", \"cid\", \"name\" FROM pragma_index_info(@ix, @sch) ORDER BY \"seqno\"",
                ("@ix", "IX_recon_child_blob"), ("@sch", null));

            Bound(connection, "Q-21", "SELECT \"seq\", \"name\", \"unique\", \"origin\", \"partial\" FROM pragma_index_list(@tbl, @sch) ORDER BY \"seq\"",
                ("@tbl", "sQtemp"), ("@sch", null));

            Bound(connection, "Q-22", "SELECT \"seq\", \"name\", \"unique\", \"origin\", \"partial\" FROM pragma_index_list(@tbl, @sch) ORDER BY \"seq\"",
                ("@tbl", "sQtemp"), ("@sch", "main"));

            Bound(connection, "Q-23", "SELECT \"cid\", \"name\", \"type\", \"notnull\", \"pk\" FROM pragma_table_info(@tbl, @sch) ORDER BY \"cid\"",
                ("@tbl", "sQtemp"), ("@sch", null));
        }

        Drop();
    }

    static void Same(DbConnection connection, string tag, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.ExecuteNonQuery();

            Line($"{tag} OK :: {sql}");
        }
        catch (Exception e)
        {
            Line($"{tag} ERR :: {sql} ⟶ {e.GetType().Name}: {e.Message}");
        }
    }

    static void Bound(DbConnection connection, string tag, string sql, params (string Name, string? Value)[] parameters)
    {
        try
        {
            using var command = connection.CreateCommand();

            command.CommandText = sql;

            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();

                parameter.ParameterName = name;
                parameter.Value = (object?) value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            using var reader = command.ExecuteReader();

            Line($"{tag} cols=[{string.Join(" ∶ ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName))}]");

            while (reader.Read())
                Line($"{tag} row " + string.Join(" ∶ ", Enumerable.Range(0, reader.FieldCount)
                    .Select(i => $"{reader.GetName(i)}={reader.GetValue(i) ?? "NULL"}")));
        }
        catch (Exception e)
        {
            Line($"{tag} ERR :: {sql} ⟶ {e.GetType().Name}: {e.Message}");
        }
    }

    static void Drop()
    {
        Database.Execute($"DROP TABLE IF EXISTS \"{Child}\"");
        Database.Execute($"DROP TABLE IF EXISTS \"{Parent}\"");
    }

    /// <summary>把一行集的全部列名与逐行值打出来：本家 pragma 函数交回的字段清单本身就是这一轮的读数。</summary>
    static void Fields(string tag, string sql)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();

            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();

            Line($"{tag} cols=[{string.Join(" ∶ ", names)}]");

            while (reader.Read())
                Line($"{tag} row " + string.Join(" ∶ ", names.Select((n, i) => $"{n}={reader.GetValue(i) ?? "NULL"}({reader.GetValue(i)?.GetType().Name})")));
        }
        catch (Exception e)
        {
            Line($"{tag} ERR :: {sql} ⟶ {e.GetType().Name}: {e.Message}");
        }
    }

    static DbConnection Open()
    {
        var connection = Database.Driver.CreateConnection()!;

        connection.ConnectionString = Database.ConnectionString;
        connection.Open();

        return connection;
    }

    static void Try(string tag, string sql)
    {
        try
        {
            Database.Execute(sql);

            Line($"{tag} OK :: {sql}");
        }
        catch (Exception e)
        {
            Line($"{tag} ERR :: {sql} ⟶ {e.GetType().Name}: {e.Message}");
        }
    }

    static void Line(string text) => Console.WriteLine(text);

    static SchemaColumn Col(string storeType) => new("C", storeType, null, null, true, 1);

    static ColumnContract Declared(string storeType) => new()
    {
        MemberName = "C",
        ClrType = typeof(string),
        ColumnName = "C",
        LengthSemantics = LengthSemantics.Char,
        Unicode = true,
        IsNullable = true,
        IsPrimary = false,
        IsIdentity = false,
        MaxChars = null,
        MaxBytes = null,
        Precision = null,
        Scale = null,
        StoreTypeName = storeType,
        Origin = new OriginTrace(MetadataLayer.Convention, "侦察直给"),
    };
}
