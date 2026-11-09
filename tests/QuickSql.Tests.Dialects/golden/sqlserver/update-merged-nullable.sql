
UPDATE [t0] SET [PaidAt] = [t1].[PaidAt] FROM [QuickSqlOrder] AS [t0] INNER JOIN (VALUES (@p0, @p1), (@p2, @p3)) AS [t1] ([Id], [PaidAt]) ON [t0].[Id] = [t1].[Id] WHERE [t0].[PaidAt] <> [t1].[PaidAt] OR ([t0].[PaidAt] IS NULL AND [t1].[PaidAt] IS NOT NULL) OR ([t0].[PaidAt] IS NOT NULL AND [t1].[PaidAt] IS NULL)
