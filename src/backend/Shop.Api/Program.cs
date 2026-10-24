using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shop.Api;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Shop") ?? "Data Source=shop.db;Default Timeout=30"));
builder.Services.AddScoped<IProductCatalog, ProductCatalog>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) => {
 try { await next(context); }
 catch (ShopException ex) { await Results.Problem(statusCode:ex.Status, title:ex.Message).ExecuteAsync(context); }
});
using (var scope = app.Services.CreateScope()) await SeedData.Initialize(scope.ServiceProvider.GetRequiredService<ShopDb>());
app.MapControllers();
app.Run();
public partial class Program { }
