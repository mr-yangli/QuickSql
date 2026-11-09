
SELECT ROW_NUMBER() OVER (PARTITION BY "t0"."BeginDate" ORDER BY "t0"."Id" ASC) AS "Rn" FROM "QuickSqlSchedule" "t0"
