using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

public static class ShopEndpoints
{
    public static RouteGroupBuilder MapShopEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").WithTags("Shop");

        group.MapGet("/products", GetProducts);
        group.MapGet("/order-statuses", GetStatuses);
        group.MapPatch("/orders/{id:guid}/status", ChangeStatus);
        group.MapPost("/cart/quote", GetQuote);
        group.MapPost("/orders", Checkout);
        group.MapGet("/orders", GetOrders);
        group.MapGet("/orders/{id:guid}", GetOrder);
        group.MapPost("/orders/{id:guid}/cancel", CancelOrder);

        return group;
    }

    private static async Task<IResult> GetProducts(ShopDb db)
    {
        var products = await db.Products.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        return Results.Ok(products);
    }

    private static async Task<IResult> GetQuote(CheckoutRequest request, PricingService pricing)
    {
        return Results.Ok(await pricing.Quote(request));
    }

    private static async Task<IResult> Checkout(
        CheckoutRequest request, OrderService orders, HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var result = await orders.Checkout(request, key);
        context.Response.Headers["Idempotency-Replayed"] = result.Replayed.ToString().ToLowerInvariant();

        return result.Replayed
            ? Results.Ok(result.Order)
            : Results.Created($"/api/orders/{result.Order.Id}", result.Order);
    }

    private static async Task<IResult> GetOrders(ShopDb db)
    {
        var orders = await db.Orders.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.CustomerId == OrderService.Customer)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        return Results.Ok(orders);
    }

    private static async Task<IResult> GetOrder(Guid id, ShopDb db)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == OrderService.Customer);

        return order is null
            ? Results.Problem(statusCode: 404, title: "Order not found.")
            : Results.Ok(order);
    }
    private static async Task<IResult> CancelOrder(Guid id, OrderService orders)
    {
        return Results.Ok(await orders.Cancel(id));
    }

    private static async Task<IResult> GetStatuses(ShopDb db)
    {
        var statuses = await db.OrderStatuses.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync();
        var transitions = await db.OrderStatusTransitions.AsNoTracking().ToListAsync();
        return Results.Ok(statuses.Select(status => new {
            status.Name, status.IsFinal,
            AllowedTransitions = transitions.Where(x => x.FromStatus == status.Name)
                .OrderBy(x => statuses.FindIndex(s => s.Name == x.ToStatus)).Select(x => x.ToStatus).ToArray()
        }));
    }

    private static async Task<IResult> ChangeStatus(Guid id, StatusChangeRequest request, OrderService orders)
    {
        return Results.Ok(await orders.ChangeStatus(id, request.Status, request.ExpectedStatus));
    }

}
