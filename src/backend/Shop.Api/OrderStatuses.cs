using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

public static class OrderStatusNames
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Fulfilled = "Fulfilled";
    public const string Cancelled = "Cancelled";
}

public class OrderStatus
{
    public string Name { get; set; } = "";
    public bool IsFinal { get; set; }
    public int SortOrder { get; set; }
}

public class OrderStatusTransition
{
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
}

public record StatusChangeRequest(string Status, string? ExpectedStatus = null);

public static class OrderStatusData
{
    public static readonly OrderStatus[] Statuses = [
        new() { Name = OrderStatusNames.Pending, SortOrder = 1 },
        new() { Name = OrderStatusNames.Confirmed, SortOrder = 2 },
        new() { Name = OrderStatusNames.Fulfilled, SortOrder = 3, IsFinal = true },
        new() { Name = OrderStatusNames.Cancelled, SortOrder = 4, IsFinal = true }
    ];
    public static readonly OrderStatusTransition[] Transitions = [
        new() { FromStatus = OrderStatusNames.Pending, ToStatus = OrderStatusNames.Confirmed },
        new() { FromStatus = OrderStatusNames.Pending, ToStatus = OrderStatusNames.Cancelled },
        new() { FromStatus = OrderStatusNames.Confirmed, ToStatus = OrderStatusNames.Fulfilled },
        new() { FromStatus = OrderStatusNames.Confirmed, ToStatus = OrderStatusNames.Cancelled }
    ];

    // Earlier demo databases were created with EnsureCreated. Add lookup tables in place,
    // keeping existing orders and their text status values without resetting the volume.
    public static async Task UpgradeExistingDatabase(ShopDb db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "OrderStatuses" (
                "Name" TEXT NOT NULL PRIMARY KEY,
                "IsFinal" INTEGER NOT NULL,
                "SortOrder" INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "OrderStatusTransitions" (
                "FromStatus" TEXT NOT NULL REFERENCES "OrderStatuses" ("Name") ON DELETE RESTRICT,
                "ToStatus" TEXT NOT NULL REFERENCES "OrderStatuses" ("Name") ON DELETE RESTRICT,
                PRIMARY KEY ("FromStatus", "ToStatus")
            );
            """);
        foreach (var status in Statuses)
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO OrderStatuses (Name, IsFinal, SortOrder) VALUES ({status.Name}, {status.IsFinal}, {status.SortOrder})");
        foreach (var transition in Transitions)
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO OrderStatusTransitions (FromStatus, ToStatus) VALUES ({transition.FromStatus}, {transition.ToStatus})");

        // SQLite cannot add a foreign key to an existing column. These guards give legacy
        // Orders tables the same lookup integrity as the FK created for new databases.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS "Orders_Status_Insert"
            BEFORE INSERT ON "Orders" WHEN NOT EXISTS (SELECT 1 FROM "OrderStatuses" WHERE "Name" = NEW."Status")
            BEGIN SELECT RAISE(ABORT, 'Unknown order status'); END;
            CREATE TRIGGER IF NOT EXISTS "Orders_Status_Update"
            BEFORE UPDATE OF "Status" ON "Orders" WHEN NOT EXISTS (SELECT 1 FROM "OrderStatuses" WHERE "Name" = NEW."Status")
            BEGIN SELECT RAISE(ABORT, 'Unknown order status'); END;
            CREATE TRIGGER IF NOT EXISTS "OrderStatuses_Delete"
            BEFORE DELETE ON "OrderStatuses" WHEN EXISTS (SELECT 1 FROM "Orders" WHERE "Status" = OLD."Name")
            BEGIN SELECT RAISE(ABORT, 'Order status is in use'); END;
            CREATE TRIGGER IF NOT EXISTS "OrderStatuses_Rename"
            BEFORE UPDATE OF "Name" ON "OrderStatuses" WHEN EXISTS (SELECT 1 FROM "Orders" WHERE "Status" = OLD."Name")
            BEGIN SELECT RAISE(ABORT, 'Order status is in use'); END;
            """);
        await transaction.CommitAsync();
    }
}
