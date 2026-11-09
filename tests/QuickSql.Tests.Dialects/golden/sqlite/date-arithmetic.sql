
SELECT RTRIM(RTRIM(STRFTIME('%Y-%m-%d %H:%M:%f', "t0"."CreatedAt", printf('%+f days', @p0)), '0'), '.') AS "Tomorrow", RTRIM(RTRIM(STRFTIME('%Y-%m-%d %H:%M:%f', "t0"."CreatedAt", printf('%+d months', @p1)), '0'), '.') AS "Next", CAST(STRFTIME('%Y', "t0"."CreatedAt") AS INTEGER) AS "Year" FROM "QuickSqlOrder" AS "t0"
