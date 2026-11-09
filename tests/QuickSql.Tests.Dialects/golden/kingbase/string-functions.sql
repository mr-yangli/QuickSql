
SELECT SUBSTRING("t0"."CustomerName" FROM @p0 + 1 FOR @p1) AS "Cut", SUBSTRING("t0"."CustomerName" FROM @p2 + 1) AS "Tail", (STRPOS("t0"."CustomerName", @p3) - 1) AS "At", CONCAT(CONCAT("t0"."CustomerName", @p4), "t0"."Note") AS "Joined", ("t0"."Note" IS NULL OR BTRIM("t0"."Note") = '') AS "Blank" FROM "QuickSqlOrder" AS "t0"
