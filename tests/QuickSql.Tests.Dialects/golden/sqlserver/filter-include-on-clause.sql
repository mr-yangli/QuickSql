
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note], [t1].[Id], [t1].[Name], [t1].[City], [t1].[Remark] FROM [QuickSqlOrder] AS [t0] LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] AND [t1].[City] IS NOT NULL
