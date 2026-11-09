
SELECT CASE WHEN [t0].[State] = @p0 THEN @p1 ELSE [t0].[CustomerName] END AS [Label], COALESCE([t0].[Note], @p2) AS [Fallback] FROM [QuickSqlOrder] AS [t0]
