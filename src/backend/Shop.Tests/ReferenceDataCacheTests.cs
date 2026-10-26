using AutoFixture;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Shop.Api;
using Xunit;

namespace Shop.Tests;

public class ReferenceDataCacheTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private readonly Fixture fixture = new();
    private readonly Clock clock = new();
    private readonly MemoryCache memory = new(new MemoryCacheOptions());
    private readonly Mock<IReferenceDataSource> source = new();
    private long productVersion;
    private long statusVersion;

    public ReferenceDataCacheTests()
    {
        source.Setup(x => x.Products()).ReturnsAsync(fixture.CreateMany<Product>(2).ToList());
        source.Setup(x => x.Statuses()).ReturnsAsync([new OrderStatusOption("Pending", false, ["Confirmed"])]);
        source.Setup(x => x.Versions()).Returns(() => Task.FromResult(new Dictionary<string, long> {
            ["products"] = productVersion, ["statuses"] = statusVersion
        }));
    }
    private ReferenceDataCache Cache() => new(memory, source.Object, clock, NullLogger<ReferenceDataCache>.Instance);

    [Fact]
    public async Task LoadsOnFirstCallAndReadsMemoryOnSubsequentCalls()
    {
        var cache = Cache();
        await cache.CheckForChanges();
        await cache.RefreshExpiredWarmEntries();
        source.Verify(x => x.Products(), Times.Never);
        var first = await cache.Products();
        var second = await cache.Products();
        Assert.Equal(first.Select(x => x.Id), second.Select(x => x.Id));
        source.Verify(x => x.Products(), Times.Once);
        source.Verify(x => x.Statuses(), Times.Never);
        Assert.Equal(1, memory.Count);
    }
    [Fact]
    public async Task ConcurrentColdCallsLoadOnlyOnce()
    {
        var cache = Cache();
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => cache.Products()));
        source.Verify(x => x.Products(), Times.Once);
    }
    [Fact]
    public async Task ExpiredEntryReloadsOnNextRead()
    {
        var cache = Cache();
        await cache.Products();
        clock.Now += TimeSpan.FromHours(1);
        await cache.Products();
        source.Verify(x => x.Products(), Times.Exactly(2));
    }
    [Fact]
    public async Task HourlyMaintenanceRefreshesOnlyWarmDatasets()
    {
        var cache = Cache();
        await cache.Products();
        clock.Now += TimeSpan.FromHours(1);
        await cache.RefreshExpiredWarmEntries();
        await cache.Products();
        source.Verify(x => x.Products(), Times.Exactly(2));
        source.Verify(x => x.Statuses(), Times.Never);
    }
    [Fact]
    public async Task ChangedRevisionImmediatelyRefreshesAffectedDataset()
    {
        var cache = Cache();
        await cache.CheckForChanges();
        await cache.Products(); await cache.Statuses();
        var changed = fixture.CreateMany<Product>(3).ToList();
        source.Setup(x => x.Products()).ReturnsAsync(changed);
        productVersion++;
        await cache.RefreshAfterWrite();
        Assert.Equal(changed.Select(x => x.Id), (await cache.Products()).Select(x => x.Id));
        source.Verify(x => x.Products(), Times.Exactly(2));
        source.Verify(x => x.Statuses(), Times.Once);
        statusVersion++;
        await cache.CheckForChanges();
        source.Verify(x => x.Statuses(), Times.Exactly(2));
    }
    [Fact]
    public async Task NewApplicationCacheDoesNotReusePreviousStartupEntries()
    {
        await Cache().Products();
        await Cache().Products();
        source.Verify(x => x.Products(), Times.Exactly(2));
        Assert.Equal(2, memory.Count);
    }
    [Fact]
    public async Task EvictedEntryReloadsDatabase()
    {
        var cache = Cache();
        await cache.Products();
        memory.Clear();
        await cache.Products();
        source.Verify(x => x.Products(), Times.Exactly(2));
    }
    [Fact]
    public async Task CallersCannotMutateCachedData()
    {
        var cache = Cache();
        var products = await cache.Products();
        var originalPrice = products[0].PriceCents;
        products[0].PriceCents = -100;
        products.Clear();
        Assert.Equal(originalPrice, (await cache.Products())[0].PriceCents);
        var statuses = await cache.Statuses();
        statuses[0].AllowedTransitions[0] = "Invalid";
        Assert.Equal("Confirmed", (await cache.Statuses())[0].AllowedTransitions[0]);
        source.Verify(x => x.Products(), Times.Once);
        source.Verify(x => x.Statuses(), Times.Once);
    }
    [Fact]
    public async Task FailedRefreshDoesNotResurrectStaleDataAndCanRecover()
    {
        var cache = Cache();
        await cache.CheckForChanges();
        await cache.Products();
        productVersion++;
        source.Setup(x => x.Products()).ThrowsAsync(new InvalidOperationException("Database unavailable"));
        await cache.RefreshAfterWrite(); // Committed writes must not fail because refresh failed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.Products());
        var changed = fixture.CreateMany<Product>(3).ToList();
        source.Setup(x => x.Products()).ReturnsAsync(changed);
        Assert.Equal(3, (await cache.Products()).Count);
    }
    [Fact]
    public async Task SqlTriggersDetectRawAndEfChangesButIgnoreOrderWritesAndRollbacks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
        await SeedData.Initialize(db);
        async Task<long> Version(string name) => await db.ReferenceDataVersions.AsNoTracking().Where(x => x.Name == name).Select(x => x.Revision).SingleAsync();
        var beforeProducts = await Version("products");
        var beforeStatuses = await Version("statuses");
        await db.Products.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "New description"));
        Assert.Equal(beforeProducts + 1, await Version("products"));
        await db.Database.ExecuteSqlRawAsync("UPDATE OrderStatuses SET SortOrder = SortOrder + 1 WHERE Name = 'Pending'");
        Assert.Equal(beforeStatuses + 1, await Version("statuses"));
        await db.Database.ExecuteSqlRawAsync("UPDATE Orders SET Status = 'Confirmed' WHERE Status = 'Pending'");
        Assert.Equal(beforeStatuses + 1, await Version("statuses"));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE Products SET PriceCents = PriceCents + 1");
            await transaction.RollbackAsync();
        }
        Assert.Equal(beforeProducts + 1, await Version("products"));
        await ReferenceDataSchema.Initialize(db);
        Assert.Equal(beforeProducts + 1, await Version("products"));
    }
    public void Dispose() => memory.Dispose();

}
