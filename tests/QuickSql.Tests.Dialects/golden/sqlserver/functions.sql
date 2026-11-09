
SELECT [t0].[Id], [t0].[CustomerName], [t0].[CustomerId], [t0].[Amount], [t0].[State], [t0].[CreatedAt], [t0].[PaidAt], [t0].[Note] FROM [QuickSqlOrder] AS [t0] WHERE LEN([t0].[CustomerName]) > @p0 AND DATEPART(year, [t0].[CreatedAt]) = @p1 AND ROUND([t0].[Amount], @p2) > @p3
