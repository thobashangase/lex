using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
namespace Shop.Api;
public class Product {
 public int Id { get; set; }
 public string Name { get; set; } = "";
 public string Description { get; set; } = "";
 public string Category { get; set; } = "";
 public string Icon { get; set; } = "";
 public long PriceCents { get; set; }
}
public class Order {
 public Guid Id { get; set; } = Guid.NewGuid();
 public string CustomerId { get; set; } = "demo-customer";
 public string SubmissionKey { get; set; } = "";
 public string Fingerprint { get; set; } = "";
 public DateTime CreatedAt { get; set; }
 public string Status { get; set; } = "Confirmed";
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
// MVC validates positional records through constructor parameters, not property-targeted attributes.
public record CartLine([Range(1, int.MaxValue)] int ProductId, [Range(1, 99)] int Quantity);
public record CheckoutRequest([Required, MinLength(1), MaxLength(50)] List<CartLine> Items);
public record QuoteLine(int ProductId, string Name, int Quantity, long UnitPriceCents, long LineTotalCents);
public record Quote(List<QuoteLine> Lines, long SubtotalCents, long DeliveryCents, long TotalCents, string Currency = "ZAR");
public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) {
 public DbSet<Product> Products => Set<Product>();
 public DbSet<Order> Orders => Set<Order>();
 protected override void OnModelCreating(ModelBuilder b) {
  b.Entity<Order>().HasIndex(x => new { x.CustomerId, x.SubmissionKey }).IsUnique();
  b.Entity<Order>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId);
 }
}
public static class SeedData {
 public static async Task Initialize(ShopDb db) {
  await db.Database.EnsureCreatedAsync();
  if (await db.Products.AnyAsync()) return;
  db.Products.AddRange(
   new Product { Id=1, Name="Everyday headphones", Description="Wireless comfort for your daily soundtrack.", Category="Audio", Icon="🎧", PriceCents=89900 },
   new Product { Id=2, Name="Stoneware mug", Description="A warm start, one slow sip at a time.", Category="Home", Icon="☕", PriceCents=18900 },
   new Product { Id=3, Name="Weekender backpack", Description="Lightweight, roomy and ready to go.", Category="Travel", Icon="🎒", PriceCents=64900 },
   new Product { Id=4, Name="Desk companion", Description="A little green for your favourite workspace.", Category="Home", Icon="🪴", PriceCents=24900 },
   new Product { Id=5, Name="Pocket notebook", Description="Good ideas deserve a place to land.", Category="Stationery", Icon="📓", PriceCents=9900 },
   new Product { Id=6, Name="Insulated bottle", Description="Keep your cool, wherever the day takes you.", Category="Travel", Icon="🧴", PriceCents=32900 });
  await db.SaveChangesAsync();
 }
}
