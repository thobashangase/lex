using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Shop.Api;

public record OrderStatusOption(string Name, bool IsFinal, string[] AllowedTransitions);
public class ReferenceDataVersion
{
    public string Name { get; set; } = "";
    public long Revision { get; set; }
}

public interface IReferenceCacheStore
{
    Task<string?> Get(string key);
    Task Set(string key, string value, TimeSpan lifetime);
}

public class RedisReferenceCacheStore(IConnectionMultiplexer redis) : IReferenceCacheStore
{
    public async Task<string?> Get(string key) => await redis.GetDatabase().StringGetAsync(key);
    public async Task Set(string key, string value, TimeSpan lifetime) =>
        await redis.GetDatabase().StringSetAsync(key, value, lifetime);
}

public interface IReferenceDataSource
{
    Task<List<Product>> Products();
    Task<List<OrderStatusOption>> Statuses();
    Task<Dictionary<string, long>> Versions();
}

public class ReferenceDataSource(IServiceScopeFactory scopes) : IReferenceDataSource
{
    public async Task<List<Product>> Products()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDb>().Products.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }
    public async Task<List<OrderStatusOption>> Statuses()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        var statuses = await db.OrderStatuses.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync();
        var transitions = await db.OrderStatusTransitions.AsNoTracking().ToListAsync();
        return statuses.Select(status => new OrderStatusOption(status.Name, status.IsFinal,
            transitions.Where(x => x.FromStatus == status.Name)
                .OrderBy(x => statuses.FindIndex(s => s.Name == x.ToStatus)).Select(x => x.ToStatus).ToArray())).ToList();
    }
    public async Task<Dictionary<string, long>> Versions()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDb>().ReferenceDataVersions.AsNoTracking()
            .ToDictionaryAsync(x => x.Name, x => x.Revision);
    }
}

public class ReferenceDataCache(IReferenceCacheStore store, IReferenceDataSource source,
    TimeProvider clock, ILogger<ReferenceDataCache> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private readonly Entry products = new("products");
    private readonly Entry statuses = new("statuses");
    private readonly SemaphoreSlim checking = new(1, 1);
    private sealed record Envelope<T>(DateTimeOffset ExpiresAt, T Value);
    private sealed class Entry(string name)
    {
        public string Name { get; } = name;
        public string Generation { get; set; } = Guid.NewGuid().ToString("N");
        public long Revision { get; set; } = -1;
        public bool Warm { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public Task<List<Product>> Products() => Get(products, source.Products);
    public Task<List<OrderStatusOption>> Statuses() => Get(statuses, source.Statuses);

    private async Task<T> Get<T>(Entry entry, Func<Task<T>> load)
    {
        await entry.Gate.WaitAsync();
        try
        {
            try
            {
                var json = await store.Get(Key(entry));
                if (json is not null && JsonSerializer.Deserialize<Envelope<T>>(json) is { } cached
                    && cached.Value is not null && cached.ExpiresAt > clock.GetUtcNow()) return cached.Value;
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException or JsonException)
            {
                logger.LogWarning(exception, "Reference cache read failed for {Dataset}; reading SQLite", entry.Name);
            }
            return await Load(entry, load);
        }
        finally { entry.Gate.Release(); }
    }

    // Called while holding the dataset gate. A new generation also prevents an old
    // Redis entry resurfacing after failed invalidation or a Redis outage.
    private async Task<T> Load<T>(Entry entry, Func<Task<T>> load)
    {
        entry.Generation = Guid.NewGuid().ToString("N");
        var value = await load();
        entry.LoadedAt = clock.GetUtcNow();
        entry.Warm = true;
        try
        {
            await store.Set(Key(entry), JsonSerializer.Serialize(new Envelope<T>(entry.LoadedAt + Lifetime, value)), Lifetime);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            logger.LogWarning(exception, "Reference cache fill failed for {Dataset}; returning SQLite data", entry.Name);
        }
        return value;
    }
    private string Key(Entry entry) => $"shop:reference:v1:{epoch}:{entry.Name}:{entry.Generation}";

    public async Task CheckForChanges()
    {
        await checking.WaitAsync();
        try
        {
            var versions = await source.Versions();
            await Check(products, versions.GetValueOrDefault("products"), source.Products);
            await Check(statuses, versions.GetValueOrDefault("statuses"), source.Statuses);
        }
        finally { checking.Release(); }
    }
    private async Task Check<T>(Entry entry, long revision, Func<Task<T>> load)
    {
        await entry.Gate.WaitAsync();
        try
        {
            if (revision == entry.Revision) return;
            entry.Revision = revision;
            entry.Generation = Guid.NewGuid().ToString("N");
            if (entry.Warm) { entry.LoadedAt = DateTimeOffset.MinValue; await Load(entry, load); }
        }
        finally { entry.Gate.Release(); }
    }
    public async Task RefreshExpiredWarmEntries()
    {
        await Refresh(products, source.Products);
        await Refresh(statuses, source.Statuses);
    }
    private async Task Refresh<T>(Entry entry, Func<Task<T>> load)
    {
        await entry.Gate.WaitAsync();
        try
        {
            if (entry.Warm && clock.GetUtcNow() - entry.LoadedAt >= Lifetime) await Load(entry, load);
        }
        finally { entry.Gate.Release(); }
    }
    public async Task RefreshAfterWrite()
    {
        try { await CheckForChanges(); }
        catch (Exception exception)
        {
            // A cache outage must never turn a committed write into an error response.
            // SQLite's durable revision counters let the background check retry later.
            logger.LogWarning(exception, "Reference cache refresh after commit failed; background check will retry");
        }
    }
}

public class CachedProductCatalog(ReferenceDataCache cache) : IProductCatalog
{
    public async Task<List<Product>> GetProducts(IEnumerable<int> ids)
    {
        var products = await cache.Products();
        var requested = ids.ToHashSet();
        return products.Where(x => requested.Contains(x.Id)).ToList();
    }
}

public class ReferenceCacheMaintenance(ReferenceDataCache cache, TimeProvider clock,
    ILogger<ReferenceCacheMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await cache.CheckForChanges();
                    await cache.RefreshExpiredWarmEntries();
                }
                catch (Exception exception) { logger.LogWarning(exception, "Reference cache maintenance will retry"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
