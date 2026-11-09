
CREATE TABLE [dbo].[QuickSqlInventoryItem] ([Id] INT IDENTITY(1,1) NOT NULL, [Sku] NVARCHAR(32) NULL, [Quantity] INT NOT NULL, [RowVersion] rowversion NOT NULL, [CheckedAt] DATETIME2(3) NULL, CONSTRAINT [PK_QuickSqlInventoryItem] PRIMARY KEY CLUSTERED ([Id]))
