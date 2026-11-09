
SELECT ("t0"."CreatedAt" + ? UNITS DAY) AS "Tomorrow", ADD_MONTHS("t0"."CreatedAt", ?) AS "Next", ABS("t0"."Amount") AS "Ceiling" FROM "QuickSqlOrder" "t0"
