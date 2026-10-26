using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Shop.Api;

public record OrderStatusOption(string Name, bool IsFinal, string[] AllowedTransitions);
public class ReferenceDataVersion
{
    public string Name { get; set; } = "";
    public long Revision { get; set; }
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

public class ReferenceDataCache(IMemoryCache memory, IReferenceDataSource source,
    TimeProvider clock, ILogger<ReferenceDataCache> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly Entry products = new("products");
    private readonly Entry statuses = new("statuses");
    private readonly SemaphoreSlim checking = new(1, 1);
    private sealed record Envelope<T>(DateTimeOffset ExpiresAt, T Value);
    private sealed class Entry(string name)
    {
        public string Name { get; } = name;
        public object Key { get; } = new();
        public long Revision { get; set; } = -1;
        public bool Warm { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public async Task<List<Product>> Products()
    {
        var products = await Get(this.products, source.Products);
        // Memory-cache values are shared objects. Return detached copies so callers
        // cannot accidentally change prices or other data for subsequent requests.
        return products.Select(x => new Product { Id = x.Id, Name = x.Name, Description = x.Description,
            Category = x.Category, Icon = x.Icon, PriceCents = x.PriceCents }).ToList();
    }
    public async Task<List<OrderStatusOption>> Statuses()
    {
        var statuses = await Get(this.statuses, source.Statuses);
        return statuses.Select(x => x with { AllowedTransitions = x.AllowedTransitions.ToArray() }).ToList();
    }

    private async Task<T> Get<T>(Entry entry, Func<Task<T>> load)
    {
        await entry.Gate.WaitAsync();
        try
        {
            if (memory.TryGetValue(entry.Key, out Envelope<T>? cached) && cached is not null
                && cached.Value is not null && cached.ExpiresAt > clock.GetUtcNow()) return cached.Value;
            return await Load(entry, load);
        }
        finally { entry.Gate.Release(); }
    }

    // Called while holding the dataset gate. Evict before reading SQLite so a failed
    // refresh cannot leave old data available after an observed revision change.
    private async Task<T> Load<T>(Entry entry, Func<Task<T>> load)
    {
        memory.Remove(entry.Key);
        var value = await load();
        entry.LoadedAt = clock.GetUtcNow();
        entry.Warm = true;
        memory.Set(entry.Key, new Envelope<T>(entry.LoadedAt + Lifetime, value),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });
        return value;
    }

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
            memory.Remove(entry.Key);
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
            // A refresh failure must never turn a committed write into an error response.
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
