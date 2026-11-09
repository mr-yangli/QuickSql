
SELECT [t0].[Id], [t1].[Name], [t2].[Sku] FROM [QuickSqlOrder] AS [t0] INNER JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] LEFT JOIN [QuickSqlOrderItem] AS [t2] ON [t2].[OrderId] = [t0].[Id] WHERE [t1].[Name] IS NOT NULL
