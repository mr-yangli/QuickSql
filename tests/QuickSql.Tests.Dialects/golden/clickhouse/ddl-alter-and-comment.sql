
ALTER TABLE "QuickSqlSessionToken" ADD COLUMN "Value" Nullable(String) ; ALTER TABLE "QuickSqlSessionToken" MODIFY COLUMN "ExpiresAt" DateTime64(3) NOT NULL
