using AutoFixture;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shop.Api;
using Xunit;
namespace Shop.Tests;
public class ShopTests {
 private readonly Fixture fixture = new();
 private (PricingService, Mock<IProductCatalog>) Pricing(long cents) {
  var product = fixture.Build<Product>().With(x => x.Id, 1).With(x => x.PriceCents, cents).Create();
  var catalog = new Mock<IProductCatalog>();
  catalog.Setup(x => x.GetProducts(It.IsAny<IEnumerable<int>>())).ReturnsAsync([product]);
  return (new PricingService(catalog.Object), catalog);
 }
 [Theory][InlineData(99999,6500)][InlineData(100000,0)][InlineData(100001,0)]
 public async Task DeliveryThresholdAndTotals(long subtotal, long delivery) {
  var (service, mock) = Pricing(subtotal);
  var quote = await service.Quote(new([new(1,1)]));
  Assert.Equal(delivery, quote.DeliveryCents); Assert.Equal(subtotal+delivery, quote.TotalCents);
  mock.Verify(x => x.GetProducts(It.IsAny<IEnumerable<int>>()), Times.Once);
 }
 [Theory][InlineData(0)][InlineData(-1)][InlineData(100)]
 public async Task InvalidQuantityIsRejectedBeforeCatalogCall(int quantity) {
  var (service,mock) = Pricing(100);
  await Assert.ThrowsAsync<ShopException>(() => service.Quote(new([new(1,quantity)])));
  mock.Verify(x => x.GetProducts(It.IsAny<IEnumerable<int>>()), Times.Never);
 }
 [Fact] public async Task DuplicateLinesAreRejected() {
  var (service,_) = Pricing(100);
  await Assert.ThrowsAsync<ShopException>(() => service.Quote(new([new(1,1),new(1,2)])));
 }
 [Fact] public async Task UnknownProductIsRejected() {
  var (service,_) = Pricing(100);
  await Assert.ThrowsAsync<ShopException>(() => service.Quote(new([new(999,1)])));
 }
 [Fact] public async Task EmptyCartIsRejected() {
  var (service,_) = Pricing(100);
  await Assert.ThrowsAsync<ShopException>(() => service.Quote(new([])));
 }
 [Fact] public async Task ReplayPersistsOneOrderAndPreservesOriginalPrice() {
  await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
  await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
  await db.Database.EnsureCreatedAsync();
  var (pricing, mock) = Pricing(10000);
  var service = new OrderService(db, pricing, TimeProvider.System);
  var key = fixture.Create<Guid>().ToString();
  var first = await service.Checkout(new([new(1,2)]),key);
  var replay = await service.Checkout(new([new(1,2)]),key);
  Assert.False(first.Replayed); Assert.True(replay.Replayed);
  Assert.Equal(OrderStatusNames.Pending, first.Order.Status);
  Assert.Equal(first.Order.Id,replay.Order.Id); Assert.Equal(26500,replay.Order.TotalCents);
  Assert.Equal(1, await db.Orders.CountAsync());
  mock.Verify(x => x.GetProducts(It.IsAny<IEnumerable<int>>()), Times.Once);
  var conflict = await Assert.ThrowsAsync<ShopException>(() => service.Checkout(new([new(1,3)]),key));
  Assert.Equal(409,conflict.Status);
 }
 [Fact] public async Task SubmissionKeyMustBeUuid() {
  await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite("Data Source=:memory:").Options);
  var (pricing,_) = Pricing(100);
  await Assert.ThrowsAsync<ShopException>(() => new OrderService(db,pricing,TimeProvider.System).Checkout(new([new(1,1)]),""));
 }
 [Fact] public async Task SeedIsRepeatable() {
  await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
  await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
  await SeedData.Initialize(db); await SeedData.Initialize(db);
  Assert.Equal(6,await db.Products.CountAsync());
  var seeded = await db.Orders.Include(x => x.Lines).ToListAsync();
  Assert.Equal(4, seeded.Count);
  Assert.Equal(4, seeded.Select(x => x.Status).Distinct().Count());
  Assert.All(seeded, order => {
   Assert.True(order.CreatedAt < DateTime.UtcNow);
   Assert.NotEmpty(order.Lines);
   Assert.Equal(order.SubtotalCents + order.DeliveryCents, order.TotalCents);
  });
  var pending = seeded.Single(x => x.Status == OrderStatusNames.Pending);
  var (pricing,_) = Pricing(100);
  await new OrderService(db,pricing,TimeProvider.System).Cancel(pending.Id);
  await SeedData.Initialize(db);
  Assert.Equal(OrderStatusNames.Cancelled, await db.Orders.AsNoTracking().Where(x => x.Id == pending.Id).Select(x => x.Status).SingleAsync());
  Assert.Equal(4, await db.Orders.CountAsync());
 }
 [Fact] public async Task ConcurrentSubmissionsCreateOnlyOneOrder() {
  var path = Path.Combine(Path.GetTempPath(), $"shop-test-{Guid.NewGuid()}.db");
  var options = new DbContextOptionsBuilder<ShopDb>().UseSqlite($"Data Source={path};Default Timeout=30;Pooling=False").Options;
  try {
   await using (var setup = new ShopDb(options)) await setup.Database.EnsureCreatedAsync();
   var key = Guid.NewGuid().ToString();
   async Task<(Order Order, bool Replayed)> Submit() {
    await using var db = new ShopDb(options);
    var (pricing,_) = Pricing(10000);
    return await new OrderService(db,pricing,TimeProvider.System).Checkout(new([new(1,2)]),key);
   }
   var results = await Task.WhenAll(Task.Run(Submit),Task.Run(Submit));
   Assert.Equal(results[0].Order.Id,results[1].Order.Id);
   Assert.Single(results, x => !x.Replayed);
   await using var verify = new ShopDb(options);
   Assert.Equal(1,await verify.Orders.CountAsync());
  } finally { File.Delete(path); File.Delete(path+"-wal"); File.Delete(path+"-shm"); }
 }

 [Theory]
 [InlineData(OrderStatusNames.Pending)]
 [InlineData(OrderStatusNames.Confirmed)]
 [InlineData(OrderStatusNames.Cancelled)]
 public async Task CancellationIsSafeToRepeatAndPreservesPrices(string status) {
  await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
  await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
  await db.Database.EnsureCreatedAsync();
  var (pricing,mock) = Pricing(10000);
  var service = new OrderService(db,pricing,TimeProvider.System);
  var key = fixture.Create<Guid>().ToString();
  var created = await service.Checkout(new([new(1,2)]),key);
  created.Order.Status = status;
  await db.SaveChangesAsync();
  var cancelled = await service.Cancel(created.Order.Id);
  var repeated = await service.Cancel(created.Order.Id);
  Assert.Equal(OrderStatusNames.Cancelled, cancelled.Status);
  Assert.Equal(OrderStatusNames.Cancelled, repeated.Status);
  Assert.Equal(created.Order.TotalCents, cancelled.TotalCents);
  Assert.Equal(2, Assert.Single(cancelled.Lines).Quantity);
  Assert.Equal(created.Order.CreatedAt, cancelled.CreatedAt);
  Assert.Equal(1, await db.Orders.CountAsync());
  var replay = await service.Checkout(new([new(1,2)]),key);
  Assert.True(replay.Replayed);
  Assert.Equal(OrderStatusNames.Cancelled,replay.Order.Status);
  mock.Verify(x => x.GetProducts(It.IsAny<IEnumerable<int>>()), Times.Once);
 }
 [Fact] public async Task FulfilledOrderCannotBeCancelled() {
  await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
  await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
  await SeedData.Initialize(db);
  var order = await db.Orders.SingleAsync(x => x.Status == OrderStatusNames.Fulfilled);
  var (pricing,_) = Pricing(100);
  var error = await Assert.ThrowsAsync<ShopException>(() => new OrderService(db,pricing,TimeProvider.System).Cancel(order.Id));
  Assert.Equal(409,error.Status);
  Assert.Equal(OrderStatusNames.Fulfilled, await db.Orders.AsNoTracking().Where(x => x.Id == order.Id).Select(x => x.Status).SingleAsync());
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task MissingOrAnotherCustomersOrderReturnsNotFound(bool exists) {
  await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
  await using var db = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options);
  await db.Database.EnsureCreatedAsync();
  var id = fixture.Create<Guid>();
  if(exists) {
   db.Orders.Add(new Order { Id=id, CustomerId="another-customer", SubmissionKey=fixture.Create<Guid>().ToString() });
   await db.SaveChangesAsync();
  }
  var (pricing,_) = Pricing(100);
  var error = await Assert.ThrowsAsync<ShopException>(() => new OrderService(db,pricing,TimeProvider.System).Cancel(id));
  Assert.Equal(404,error.Status);
  if(exists) Assert.Equal(OrderStatusNames.Pending,await db.Orders.AsNoTracking().Select(x => x.Status).SingleAsync());
 }

}
