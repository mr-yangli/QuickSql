
SELECT "t0"."Id", "t0"."CustomerName", "t0"."CustomerId", "t0"."Amount", "t0"."State", "t0"."CreatedAt", "t0"."PaidAt", "t0"."Note" FROM "QuickSqlOrder" AS "t0" WHERE @p0 IS TRUE AND "t0"."Amount" > @p1
