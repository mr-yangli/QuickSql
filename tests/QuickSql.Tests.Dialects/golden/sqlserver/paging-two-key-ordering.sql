
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note] FROM [QuickSqlOrder] AS [t0] ORDER BY [t0].[CreatedAt] ASC, [t0].[Id] ASC OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY
