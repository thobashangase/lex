using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace Shop.Api;
public class ShopException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
public interface IProductCatalog { Task<List<Product>> GetProducts(IEnumerable<int> ids); }
public class ProductCatalog(ShopDb db) : IProductCatalog {
 public Task<List<Product>> GetProducts(IEnumerable<int> ids) => db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();
}
public class PricingService(IProductCatalog catalog) {
 public async Task<Quote> Quote(CheckoutRequest request) {
  if (request.Items is null || request.Items.Count is < 1 or > 50) throw new ShopException("Choose between 1 and 50 cart lines.");
  if (request.Items.Any(x => x is null || x.ProductId < 1 || x.Quantity is < 1 or > 99)) throw new ShopException("Each quantity must be between 1 and 99.");
  if (request.Items.Select(x => x.ProductId).Distinct().Count() != request.Items.Count) throw new ShopException("A product may appear only once in the cart.");
  var products = await catalog.GetProducts(request.Items.Select(x => x.ProductId));
  var lines = request.Items.Select(x => {
   var p = products.SingleOrDefault(p => p.Id == x.ProductId) ?? throw new ShopException($"Product {x.ProductId} is unavailable.");
   if (p.PriceCents < 0) throw new ShopException($"Product {p.Id} has an invalid negative price.");
   return new QuoteLine(p.Id, p.Name, x.Quantity, p.PriceCents, checked(p.PriceCents * x.Quantity));
  }).ToList();
  var subtotal = lines.Sum(x => x.LineTotalCents);
  var delivery = subtotal >= 100000 ? 0 : 6500;
  return new Quote(lines, subtotal, delivery, checked(subtotal + delivery));
 }
}
public class OrderService(ShopDb db, PricingService pricing, TimeProvider clock) {
 public const string Customer = "demo-customer";
 public Task<Order> Cancel(Guid id) => ChangeStatus(id, OrderStatusNames.Cancelled);

 public async Task<Order> ChangeStatus(Guid id, string? target, string? expectedStatus = null) {
  var order = await db.Orders.AsNoTracking().Include(x => x.Lines)
   .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == Customer)
   ?? throw new ShopException("Order not found.", 404);
  if (string.IsNullOrWhiteSpace(target) || !await db.OrderStatuses.AnyAsync(x => x.Name == target))
   throw new ShopException("Choose a valid order status.");
  // A retry of an already-applied update is successful, including terminal statuses.
  if (order.Status == target) return order;
  if (expectedStatus is not null && expectedStatus != order.Status)
   throw new ShopException("The order status has changed. Refresh your orders and try again.", 409);
  var current = await db.OrderStatuses.AsNoTracking().SingleAsync(x => x.Name == order.Status);
  if (current.IsFinal)
   throw new ShopException($"{current.Name} orders cannot change status.", 409);
  if (!await db.OrderStatusTransitions.AnyAsync(x => x.FromStatus == order.Status && x.ToStatus == target))
   throw new ShopException($"Cannot change an order from {order.Status} to {target}.", 409);
  // Compare and update atomically: concurrent requests cannot overwrite a later state.
  var changed = await db.Orders.Where(x => x.Id == id && x.CustomerId == Customer && x.Status == order.Status)
   .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, target));
  if (changed == 0) {
   var latest = await db.Orders.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == id && x.CustomerId == Customer);
   if (latest.Status == target) return latest;
   throw new ShopException("The order status has changed. Refresh your orders and try again.", 409);
  }
  order.Status = target;
  return order;
 }

 public async Task<(Order Order, bool Replayed)> Checkout(CheckoutRequest request, string key) {
  if (!Guid.TryParseExact(key, "D", out _)) throw new ShopException("Provide a UUID Idempotency-Key header.");
  key = key.ToLowerInvariant();
  if (request.Items is null || request.Items.Any(x => x is null)) throw new ShopException("Cart items are required.");
  var canonical = string.Join(";", request.Items.OrderBy(x => x.ProductId).Select(x => $"{x.ProductId}:{x.Quantity}"));
  var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
  async Task<Order?> Existing() => await db.Orders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CustomerId == Customer && x.SubmissionKey == key);
  (Order, bool) Replay(Order order) {
   if (order.Fingerprint != fingerprint) throw new ShopException("This submission key was already used for a different cart. Start a new checkout.", 409);
   return (order, true);
  }
  var existing = await Existing();
  if (existing is not null) return Replay(existing);
  var quote = await pricing.Quote(request);
  var order = new Order { CustomerId=Customer, SubmissionKey=key, Fingerprint=fingerprint, CreatedAt=clock.GetUtcNow().UtcDateTime,
   SubtotalCents=quote.SubtotalCents, DeliveryCents=quote.DeliveryCents, TotalCents=quote.TotalCents,
   Lines=quote.Lines.Select(x => new OrderLine { ProductId=x.ProductId, Name=x.Name, Quantity=x.Quantity, UnitPriceCents=x.UnitPriceCents, LineTotalCents=x.LineTotalCents }).ToList() };
  db.Orders.Add(order);
  try { await db.SaveChangesAsync(); }
  catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 }) {
   db.ChangeTracker.Clear();
   var winner = await Existing();
   if (winner is null) throw;
   return Replay(winner);
  }
  return (order, false);
 }
}
