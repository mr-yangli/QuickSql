
CREATE TABLE "QuickSqlOrder" ("Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, "CustomerName" VARCHAR(64) NULL, "CustomerId" INTEGER NOT NULL, "Amount" TEXT NOT NULL, "State" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL, "PaidAt" TEXT NULL, "Note" TEXT NULL) ; CREATE INDEX "IX_QuickSqlOrder_CreatedAt" ON "QuickSqlOrder" ( "CreatedAt" ASC )
