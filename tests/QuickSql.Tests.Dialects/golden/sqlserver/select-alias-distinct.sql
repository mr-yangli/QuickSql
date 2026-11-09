
SELECT DISTINCT [ord].[Id], [ord].[CustomerName], [ord].[CustomerId], [ord].[Amount], [ord].[State], [ord].[CreatedAt], [ord].[PaidAt], [ord].[Note] FROM [QuickSqlOrder] AS [ord] WHERE [ord].[State] = @p0
