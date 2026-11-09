
UPDATE [t0] SET [Amount] = [t1].[Amount], [PaidAt] = [t1].[PaidAt] FROM [QuickSqlOrder] AS [t0] INNER JOIN (VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)) AS [t1] ([Id], [Amount], [PaidAt]) ON [t0].[Id] = [t1].[Id] WHERE [t0].[Amount] <> [t1].[Amount] OR [t0].[PaidAt] <> [t1].[PaidAt] OR ([t0].[PaidAt] IS NULL AND [t1].[PaidAt] IS NOT NULL) OR ([t0].[PaidAt] IS NOT NULL AND [t1].[PaidAt] IS NULL)
