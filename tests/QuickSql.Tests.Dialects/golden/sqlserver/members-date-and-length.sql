
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note] FROM [QuickSqlOrder] AS [t0] WHERE DATEPART(year, [t0].[CreatedAt]) = @p0 AND DATEPART(month, [t0].[PaidAt]) > @p1 AND LEN([t0].[CustomerName]) > @p2
