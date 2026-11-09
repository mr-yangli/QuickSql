
CREATE TABLE "QuickSqlSessionToken" ("Id" UUID NOT NULL, "Value" Nullable(String), "ExpiresAt" DateTime64(3) NOT NULL) ENGINE = MergeTree ORDER BY ("Id") PRIMARY KEY ("Id")
