
SELECT "t0"."Id", "t0"."CustomerName", "t0"."CustomerId", "t0"."Amount", "t0"."State", "t0"."CreatedAt", "t0"."PaidAt", "t0"."Note" FROM "QuickSqlOrder" AS "t0" WHERE LENGTH("t0"."CustomerName") > @p0 AND EXTRACT(year FROM "t0"."CreatedAt") = @p1 AND ROUND(CAST("t0"."Amount" AS numeric), @p2) > @p3
