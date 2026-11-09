
SELECT [t0].[Id], [t1].[Name] FROM [QuickSqlOrder] AS [t0] INNER JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] ORDER BY [t1].[Name] ASC, [t0].[CreatedAt] DESC
