
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note] FROM [QuickSqlOrder] AS [t0] LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] WHERE [t1].[City] IS NOT NULL
