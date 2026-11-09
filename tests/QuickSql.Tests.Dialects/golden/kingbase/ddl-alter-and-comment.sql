
ALTER TABLE "public"."QuickSqlOrder" ADD COLUMN "Note" TEXT NULL ; ALTER TABLE "public"."QuickSqlOrder" ALTER COLUMN "Amount" TYPE NUMERIC(18,4) ; ALTER TABLE "public"."QuickSqlOrder" ALTER COLUMN "Amount" SET NOT NULL ; COMMENT ON COLUMN "public"."QuickSqlOrder"."CustomerName" IS ''
