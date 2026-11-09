
UPDATE [t0] SET [Amount] = [t1].[Amount] FROM [QuickSqlOrder] AS [t0] INNER JOIN (VALUES (@p0, @p1), (@p2, @p3)) AS [t1] ([Id], [Amount]) ON [t0].[Id] = [t1].[Id] WHERE [t0].[Amount] <> [t1].[Amount]
