
UPDATE "QuickSqlOrder" AS "t0" SET "Amount" = "t1"."Amount", "PaidAt" = "t1"."PaidAt" FROM (VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)) AS "t1" ("Id", "Amount", "PaidAt") WHERE "t0"."Id" = "t1"."Id" AND ("t0"."Amount" <> "t1"."Amount" OR "t0"."PaidAt" IS DISTINCT FROM "t1"."PaidAt")
