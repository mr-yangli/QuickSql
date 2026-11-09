
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note] FROM [QuickSqlOrder] AS [t0] WHERE [t0].[State] = @p0 AND [t0].[Amount] > @p1 AND [t0].[PaidAt] IS NOT NULL
