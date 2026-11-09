
UPDATE "QuickSqlOrder" AS "t0" SET "PaidAt" = "t1"."PaidAt" FROM (VALUES (@p0, @p1), (@p2, @p3)) AS "t1" ("Id", "PaidAt") WHERE "t0"."Id" = "t1"."Id" AND "t0"."PaidAt" IS DISTINCT FROM "t1"."PaidAt"
