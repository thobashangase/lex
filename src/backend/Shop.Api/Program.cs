using Microsoft.EntityFrameworkCore;
using Shop.Api;
using StackExchange.Redis;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Shop") ?? "Data Source=shop.db;Default Timeout=30"));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => {
 var options = ConfigurationOptions.Parse(builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379");
 options.AbortOnConnectFail = false;
 options.ConnectRetry = 0;
 options.ConnectTimeout = 1000;
 options.AsyncTimeout = 1000;
 options.BacklogPolicy = BacklogPolicy.FailFast;
 return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<IReferenceCacheStore, RedisReferenceCacheStore>();
builder.Services.AddSingleton<IReferenceDataSource, ReferenceDataSource>();
builder.Services.AddSingleton<ReferenceDataCache>();
builder.Services.AddScoped<IProductCatalog, CachedProductCatalog>();
builder.Services.AddScoped<PricingService>();
// Checkout and state transitions remain authoritative even during cache outages or
// the short polling window for external reference-data edits.
builder.Services.AddScoped(services => new OrderService(services.GetRequiredService<ShopDb>(),
 new PricingService(new ProductCatalog(services.GetRequiredService<ShopDb>())),
 services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<WriteCommandProcessor>();
builder.Services.AddSingleton<RabbitMqWriteQueue>();
builder.Services.AddSingleton<IOrderWriteQueue>(services => services.GetRequiredService<RabbitMqWriteQueue>());
builder.Services.AddHostedService(services => services.GetRequiredService<RabbitMqWriteQueue>());
builder.Services.AddHostedService<ReferenceCacheMaintenance>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) => {
 try { await next(context); }
 catch (ShopException ex) { await Results.Problem(statusCode:ex.Status, title:ex.Message).ExecuteAsync(context); }
 catch (BadHttpRequestException ex) { await Results.Problem(statusCode:ex.StatusCode, title:"Invalid request.", detail:ex.Message).ExecuteAsync(context); }
});

app.MapShopEndpoints();
app.Run();
public partial class Program { }
