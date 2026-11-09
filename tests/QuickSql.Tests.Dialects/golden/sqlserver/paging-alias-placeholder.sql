
SELECT [o].[Id], [o].[CustomerName], [o].[CustomerId], [o].[Amount], [o].[State], [o].[CreatedAt], [o].[PaidAt], [o].[Note] FROM [QuickSqlOrder] AS [o] ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT @p0 ROWS ONLY
