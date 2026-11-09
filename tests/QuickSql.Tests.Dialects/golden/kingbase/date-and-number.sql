
SELECT ("t0"."CreatedAt" + (@p0) * INTERVAL '1 day') AS "Tomorrow", GREATEST("t0"."Amount", @p1) AS "Ceiling" FROM "QuickSqlOrder" AS "t0"
