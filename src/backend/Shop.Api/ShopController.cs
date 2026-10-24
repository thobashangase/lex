using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Shop.Api;
[ApiController]
[Route("api")]
public class ShopController(ShopDb db, PricingService pricing, OrderService orders) : ControllerBase {
 [HttpGet("products")]
 public async Task<IActionResult> Products() => Ok(await db.Products.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
 [HttpPost("cart/quote")]
 public async Task<IActionResult> Quote(CheckoutRequest request) => Ok(await pricing.Quote(request));
 [HttpPost("orders")]
 public async Task<IActionResult> Checkout(CheckoutRequest request, [FromHeader(Name="Idempotency-Key")] string? key) {
  var result = await orders.Checkout(request, key ?? "");
  Response.Headers["Idempotency-Replayed"] = result.Replayed.ToString().ToLowerInvariant();
  return result.Replayed ? Ok(result.Order) : Created($"/api/orders/{result.Order.Id}", result.Order);
 }
 [HttpGet("orders")]
 public async Task<IActionResult> Orders() => Ok(await db.Orders.AsNoTracking().Include(x => x.Lines).Where(x => x.CustomerId == OrderService.Customer).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync());
 [HttpGet("orders/{id:guid}")]
 public async Task<IActionResult> Order(Guid id) {
  var order = await db.Orders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == OrderService.Customer);
  return order is null ? NotFound(new ProblemDetails { Status=404, Title="Order not found." }) : Ok(order);
 }
}
