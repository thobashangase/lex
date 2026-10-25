using AutoFixture;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shop.Api;
using Xunit;

namespace Shop.Tests;

public class OrderStatusTests
{
    private readonly Fixture fixture = new();

    private static OrderService Service(ShopDb db) =>
        new(db, new PricingService(Mock.Of<IProductCatalog>()), TimeProvider.System);

    [Theory]
    [InlineData("Pending", "Confirmed")]
    [InlineData("Pending", "Cancelled")]
    [InlineData("Confirmed", "Fulfilled")]
    [InlineData("Confirmed", "Cancelled")]
    public async Task AllowedTransitionsPreserveOrderDetailsAndSupportRetries(string from, string to)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var original = await db.Orders.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Status == from);
        var changed = await Service(db).ChangeStatus(original.Id, to, from);
        var retried = await Service(db).ChangeStatus(original.Id, to, from);
        Assert.Equal(to, changed.Status);
        Assert.Equal(to, retried.Status);
        Assert.Equal(original.TotalCents, changed.TotalCents);
        Assert.Equal(original.CreatedAt, changed.CreatedAt);
        Assert.Equal(original.Lines.Select(x => x.LineTotalCents), changed.Lines.Select(x => x.LineTotalCents));
        Assert.Equal(4, await db.Orders.CountAsync());
    }

    [Theory]
    [InlineData("Pending", "Fulfilled")]
    [InlineData("Confirmed", "Pending")]
    [InlineData("Fulfilled", "Pending")]
    [InlineData("Fulfilled", "Cancelled")]
    [InlineData("Cancelled", "Confirmed")]
    [InlineData("Cancelled", "Fulfilled")]
    public async Task InvalidTransitionsDoNotChangeStatus(string from, string to)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var order = await db.Orders.AsNoTracking().SingleAsync(x => x.Status == from);
        var error = await Assert.ThrowsAsync<ShopException>(() => Service(db).ChangeStatus(order.Id, to));
        Assert.Equal(409, error.Status);
        Assert.Equal(from, await db.Orders.Where(x => x.Id == order.Id).Select(x => x.Status).SingleAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    public async Task UnknownOrMissingTargetIsRejected(string? target)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var id = await db.Orders.Where(x => x.Status == OrderStatusNames.Pending).Select(x => x.Id).SingleAsync();
        var error = await Assert.ThrowsAsync<ShopException>(() => Service(db).ChangeStatus(id, target));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task StaleExpectedStatusCannotOverwriteAnOrder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var id = await db.Orders.Where(x => x.Status == OrderStatusNames.Confirmed).Select(x => x.Id).SingleAsync();
        var error = await Assert.ThrowsAsync<ShopException>(() => Service(db).ChangeStatus(id, "Fulfilled", "Pending"));
        Assert.Equal(409, error.Status);
        Assert.Equal("Confirmed", await db.Orders.Where(x => x.Id == id).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task StatusesAndTransitionsComeFromDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var target = fixture.Create<string>();
        db.OrderStatuses.Add(new OrderStatus { Name = target, SortOrder = 5, IsFinal = true });
        db.OrderStatusTransitions.Add(new OrderStatusTransition { FromStatus = "Confirmed", ToStatus = target });
        await db.SaveChangesAsync();
        var id = await db.Orders.Where(x => x.Status == "Confirmed").Select(x => x.Id).SingleAsync();
        Assert.Equal(target, (await Service(db).ChangeStatus(id, target)).Status);
        var error = await Assert.ThrowsAsync<ShopException>(() => Service(db).ChangeStatus(id, "Cancelled"));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task LegacyUpgradePreservesOrdersAndEnforcesLookupIntegrity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Orders (Id TEXT PRIMARY KEY, Status TEXT NOT NULL); INSERT INTO Orders VALUES ('existing-order', 'Confirmed');");
        await OrderStatusData.UpgradeExistingDatabase(db);
        await OrderStatusData.UpgradeExistingDatabase(db);
        Assert.Equal(4, await db.OrderStatuses.CountAsync());
        Assert.Equal(4, await db.OrderStatusTransitions.CountAsync());
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM Orders WHERE Id = 'existing-order'";
        Assert.Equal("Confirmed", await command.ExecuteScalarAsync());
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE Orders SET Status = 'Unknown'"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM OrderStatuses WHERE Name = 'Confirmed'"));
    }
}
