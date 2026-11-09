
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note] FROM [QuickSqlOrder] AS [t0] WHERE [t0].[State] = @p0 ORDER BY [t0].[CreatedAt] DESC, [t0].[Id] ASC OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY
