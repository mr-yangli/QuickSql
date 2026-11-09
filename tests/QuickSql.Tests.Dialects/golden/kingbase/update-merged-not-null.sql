
UPDATE "QuickSqlOrder" AS "t0" SET "Amount" = "t1"."Amount" FROM (VALUES (@p0, @p1), (@p2, @p3)) AS "t1" ("Id", "Amount") WHERE "t0"."Id" = "t1"."Id" AND "t0"."Amount" <> "t1"."Amount"
