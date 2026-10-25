using AutoFixture;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shop.Api;
using Xunit;

namespace Shop.Tests;

public class WriteCommandTests
{
    private readonly Fixture fixture = new();
    private WriteCommandProcessor Processor(ShopDb db)
    {
        var product = fixture.Build<Product>().With(x => x.Id, 1).With(x => x.PriceCents, 10000L).Create();
        var catalog = new Mock<IProductCatalog>();
        catalog.Setup(x => x.GetProducts(It.IsAny<IEnumerable<int>>())).ReturnsAsync([product]);
        return new(db, new OrderService(db, new PricingService(catalog.Object), TimeProvider.System), TimeProvider.System);
    }

    [Fact]
    public async Task RedeliveredCheckoutReturnsSavedResultWithoutAnotherOrder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var command = new WriteCommand(fixture.Create<Guid>(), "checkout", new([new(1, 2)]), fixture.Create<Guid>().ToString());
        var first = await Processor(db).Process(command);
        var second = await Processor(db).Process(command);
        Assert.Equal(201, first.StatusCode);
        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Equal(5, await db.Orders.CountAsync());
        Assert.Equal(1, await db.ProcessedWriteCommands.CountAsync());
    }

    [Fact]
    public async Task OldStatusMessageCannotReapplyAfterANewerTransition()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var id = await db.Orders.Where(x => x.Status == "Pending").Select(x => x.Id).SingleAsync();
        var command = new WriteCommand(fixture.Create<Guid>(), "status", OrderId: id, Status: "Confirmed", ExpectedStatus: "Pending");
        Assert.Equal("Confirmed", (await Processor(db).Process(command)).Order!.Status);
        await Processor(db).Process(new(fixture.Create<Guid>(), "status", OrderId: id, Status: "Fulfilled", ExpectedStatus: "Confirmed"));
        Assert.Equal("Confirmed", (await Processor(db).Process(command)).Order!.Status); // Original saved reply.
        Assert.Equal("Fulfilled", await db.Orders.AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).SingleAsync());
        Assert.Equal(2, await db.ProcessedWriteCommands.CountAsync());
    }

    [Fact]
    public async Task CommandIdCannotBeReusedForAnotherPayload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var command = new WriteCommand(fixture.Create<Guid>(), "checkout", new([new(1, 2)]), fixture.Create<Guid>().ToString());
        await Processor(db).Process(command);
        var conflict = await Processor(db).Process(command with { Checkout = new([new(1, 3)]) });
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(5, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task BusinessErrorsAreSavedAsTerminalReplies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        var command = new WriteCommand(fixture.Create<Guid>(), "checkout", new([]), fixture.Create<Guid>().ToString());
        Assert.Equal(400, (await Processor(db).Process(command)).StatusCode);
        Assert.Equal(400, (await Processor(db).Process(command)).StatusCode);
        Assert.Equal(4, await db.Orders.CountAsync());
        Assert.Equal(1, await db.ProcessedWriteCommands.CountAsync());
    }

    [Fact]
    public async Task OrderAndReceiptRollBackTogetherThenRetrySucceeds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailReceipt BEFORE INSERT ON ProcessedWriteCommands BEGIN SELECT RAISE(ABORT, 'Simulated receipt failure'); END;");
        var command = new WriteCommand(fixture.Create<Guid>(), "checkout", new([new(1, 2)]), fixture.Create<Guid>().ToString());
        await Assert.ThrowsAsync<DbUpdateException>(() => Processor(db).Process(command));
        db.ChangeTracker.Clear();
        Assert.Equal(4, await db.Orders.CountAsync());
        Assert.Equal(0, await db.ProcessedWriteCommands.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailReceipt");
        Assert.Equal(201, (await Processor(db).Process(command)).StatusCode);
        Assert.Equal(5, await db.Orders.CountAsync());
        Assert.Equal(1, await db.ProcessedWriteCommands.CountAsync());
    }
}
