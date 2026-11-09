
UPDATE "QuickSqlOrder" SET "Amount" = ? WHERE "QuickSqlOrder"."PaidAt" <> "t1"."PaidAt" OR ("QuickSqlOrder"."PaidAt" IS NULL AND "t1"."PaidAt" IS NOT NULL) OR ("QuickSqlOrder"."PaidAt" IS NOT NULL AND "t1"."PaidAt" IS NULL)
