
DELETE FROM "QuickSqlOrder" AS "t0" USING (VALUES (@p0)) AS "t1" ("Id"), (VALUES (@p1)) AS "t2" ("OrderId") WHERE "t0"."CustomerId" = "t1"."Id" AND "t0"."Id" = "t2"."OrderId" AND "t0"."State" = @p2
