using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

public static class ReferenceDataSchema
{
    // Transactional revision counters cover EF, ExecuteUpdate and external SQLite edits.
    public static async Task Initialize(ShopDb db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ReferenceDataVersions" ("Name" TEXT NOT NULL PRIMARY KEY, "Revision" INTEGER NOT NULL);
            INSERT OR IGNORE INTO "ReferenceDataVersions" ("Name", "Revision") VALUES ('products', 0), ('statuses', 0);
            CREATE TRIGGER IF NOT EXISTS "Cache_Products_INSERT" AFTER INSERT ON "Products"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'products'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_Products_UPDATE" AFTER UPDATE ON "Products"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'products'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_Products_DELETE" AFTER DELETE ON "Products"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'products'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_OrderStatuses_INSERT" AFTER INSERT ON "OrderStatuses"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'statuses'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_OrderStatuses_UPDATE" AFTER UPDATE ON "OrderStatuses"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'statuses'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_OrderStatuses_DELETE" AFTER DELETE ON "OrderStatuses"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'statuses'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_OrderStatusTransitions_INSERT" AFTER INSERT ON "OrderStatusTransitions"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'statuses'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_OrderStatusTransitions_UPDATE" AFTER UPDATE ON "OrderStatusTransitions"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'statuses'; END;
            CREATE TRIGGER IF NOT EXISTS "Cache_OrderStatusTransitions_DELETE" AFTER DELETE ON "OrderStatusTransitions"
            BEGIN UPDATE "ReferenceDataVersions" SET "Revision" = "Revision" + 1 WHERE "Name" = 'statuses'; END;
            """);
        await transaction.CommitAsync();
    }
}
