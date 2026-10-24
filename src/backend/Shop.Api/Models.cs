using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
namespace Shop.Api;
public class Product {
 public int Id { get; set; }
 public string Name { get; set; } = "";
 public string Description { get; set; } = "";
 public string Category { get; set; } = "";
 public string Icon { get; set; } = "";
 public long PriceCents { get; set; }
}
[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus { Pending, Confirmed, Fulfilled, Cancelled }

public class Order {
 public Guid Id { get; set; } = Guid.NewGuid();
 public string CustomerId { get; set; } = "demo-customer";
 public string SubmissionKey { get; set; } = "";
 public string Fingerprint { get; set; } = "";
 public DateTime CreatedAt { get; set; }
 public OrderStatus Status { get; set; } = OrderStatus.Pending;
 public string Currency { get; set; } = "ZAR";
 public long SubtotalCents { get; set; }
 public long DeliveryCents { get; set; }
 public long TotalCents { get; set; }
 public List<OrderLine> Lines { get; set; } = [];
}
public class OrderLine {
 public int Id { get; set; }
 public Guid OrderId { get; set; }
 public int ProductId { get; set; }
 public string Name { get; set; } = "";
 public int Quantity { get; set; }
 public long UnitPriceCents { get; set; }
 public long LineTotalCents { get; set; }
}
// PricingService and OrderService validate these requests for every caller.
public record CartLine(int ProductId, int Quantity);
public record CheckoutRequest(List<CartLine> Items);
public record QuoteLine(int ProductId, string Name, int Quantity, long UnitPriceCents, long LineTotalCents);
public record Quote(List<QuoteLine> Lines, long SubtotalCents, long DeliveryCents, long TotalCents, string Currency = "ZAR");
public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) {
 public DbSet<Product> Products => Set<Product>();
 public DbSet<Order> Orders => Set<Order>();
 protected override void OnModelCreating(ModelBuilder b) {
  b.Entity<Order>().Property(x => x.Status).HasConversion<string>();
  b.Entity<Order>().HasIndex(x => new { x.CustomerId, x.SubmissionKey }).IsUnique();
  b.Entity<Order>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId);
 }
}
public static class SeedData {
 public static async Task Initialize(ShopDb db) {
  await db.Database.EnsureCreatedAsync();
  if (!await db.Products.AnyAsync()) {
  db.Products.AddRange(
   new Product { Id=1, Name="Everyday headphones", Description="Wireless comfort for your daily soundtrack.", Category="Audio", Icon="🎧", PriceCents=89900 },
   new Product { Id=2, Name="Stoneware mug", Description="A warm start, one slow sip at a time.", Category="Home", Icon="☕", PriceCents=18900 },
   new Product { Id=3, Name="Weekender backpack", Description="Lightweight, roomy and ready to go.", Category="Travel", Icon="🎒", PriceCents=64900 },
   new Product { Id=4, Name="Desk companion", Description="A little green for your favourite workspace.", Category="Home", Icon="🪴", PriceCents=24900 },
   new Product { Id=5, Name="Pocket notebook", Description="Good ideas deserve a place to land.", Category="Stationery", Icon="📓", PriceCents=9900 },
   new Product { Id=6, Name="Insulated bottle", Description="Keep your cool, wherever the day takes you.", Category="Travel", Icon="🧴", PriceCents=32900 });
  await db.SaveChangesAsync();
  }
  await SeedOrders(db);
 }
 private static async Task SeedOrders(ShopDb db) {
  var examples = new[] {
   (Id: Guid.Parse("10000001-0000-0000-0000-000000000001"), Status: OrderStatus.Pending, DaysAgo: 2, ProductId: 2, Quantity: 2),
   (Id: Guid.Parse("10000002-0000-0000-0000-000000000002"), Status: OrderStatus.Confirmed, DaysAgo: 5, ProductId: 3, Quantity: 1),
   (Id: Guid.Parse("10000003-0000-0000-0000-000000000003"), Status: OrderStatus.Fulfilled, DaysAgo: 12, ProductId: 1, Quantity: 2),
   (Id: Guid.Parse("10000004-0000-0000-0000-000000000004"), Status: OrderStatus.Cancelled, DaysAgo: 20, ProductId: 5, Quantity: 3)
  };
  var pricing = new PricingService(new ProductCatalog(db));
  var now = DateTime.UtcNow;
  foreach (var example in examples) {
   // Fixed IDs make this additive for existing demos and preserve later cancellations.
   if (await db.Orders.AnyAsync(x => x.Id == example.Id)) continue;
   var quote = await pricing.Quote(new([new(example.ProductId, example.Quantity)]));
   db.Orders.Add(new Order {
    Id = example.Id, CustomerId = OrderService.Customer,
    SubmissionKey = $"seed-{example.Id}", Fingerprint = "seed",
    CreatedAt = now.AddDays(-example.DaysAgo), Status = example.Status,
    SubtotalCents = quote.SubtotalCents, DeliveryCents = quote.DeliveryCents, TotalCents = quote.TotalCents,
    Lines = quote.Lines.Select(x => new OrderLine { ProductId = x.ProductId, Name = x.Name,
     Quantity = x.Quantity, UnitPriceCents = x.UnitPriceCents, LineTotalCents = x.LineTotalCents }).ToList()
   });
  }
  await db.SaveChangesAsync();
 }
}
