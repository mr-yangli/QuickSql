
SELECT [t0].[Id], [t1].[Name] FROM [QuickSqlOrder] AS [t0] INNER JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id] WHERE [t1].[City] = @p0 AND [t0].[Amount] > @p1
