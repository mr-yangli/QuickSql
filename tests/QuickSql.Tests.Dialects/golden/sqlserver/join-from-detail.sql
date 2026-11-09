
SELECT [t0].[Sku], [t1].[Amount], [t2].[City] FROM [QuickSqlOrderItem] AS [t0] INNER JOIN [QuickSqlOrder] AS [t1] ON [t0].[OrderId] = [t1].[Id] INNER JOIN [QuickSqlCustomer] AS [t2] ON [t1].[CustomerId] = [t2].[Id]
