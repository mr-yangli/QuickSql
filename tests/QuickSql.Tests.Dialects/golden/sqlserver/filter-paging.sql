
SELECT [t0].[Id], [t0].[Title], [t0].[TenantId], [t0].[Deleted], [t0].[DeletedAt] FROM [QuickSqlTicket] AS [t0] WHERE NOT [t0].[Deleted] = 1 ORDER BY [t0].[Id] ASC OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY
