
SELECT [t0].[Id], [t0].[Title], [t0].[TenantId], [t0].[Deleted], [t0].[DeletedAt] FROM [QuickSqlTicket] AS [t0] WHERE [t0].[Title] = @p0 AND NOT [t0].[Deleted] = 1 AND [t0].[TenantId] = @p1
