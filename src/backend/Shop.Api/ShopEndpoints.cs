using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

public static class ShopEndpoints
{
    public static RouteGroupBuilder MapShopEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").WithTags("Shop");

        group.MapGet("/products", GetProducts);
        group.MapGet("/health", (IOrderWriteQueue writes) => writes.IsReady ? Results.Ok(new { status = "Ready" }) : Results.Problem(statusCode: 503, title: "Write queue unavailable."));
        group.MapGet("/order-statuses", GetStatuses);
        group.MapPatch("/orders/{id:guid}/status", ChangeStatus);
        group.MapPost("/cart/quote", GetQuote);
        group.MapPost("/orders", Checkout);
        group.MapGet("/orders", GetOrders);
        group.MapGet("/orders/{id:guid}", GetOrder);
        group.MapPost("/orders/{id:guid}/cancel", CancelOrder);

        return group;
    }

    private static async Task<IResult> GetProducts(ReferenceDataCache cache)
    {
        var products = await cache.Products();
        return Results.Ok(products);
    }

    private static async Task<IResult> GetQuote(CheckoutRequest request, PricingService pricing)
    {
        return Results.Ok(await pricing.Quote(request));
    }

    private static async Task<IResult> Checkout(
        CheckoutRequest request, IOrderWriteQueue writes, HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var result = await writes.Send(new WriteCommand(Guid.NewGuid(), "checkout", Checkout: request, Key: key), context.RequestAborted);
        EnsureSuccess(result);
        context.Response.Headers["Idempotency-Replayed"] = result.Replayed.ToString().ToLowerInvariant();

        return result.Replayed
            ? Results.Ok(result.Order)
            : Results.Created($"/api/orders/{result.Order!.Id}", result.Order);
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
    private static async Task<IResult> CancelOrder(Guid id, IOrderWriteQueue writes, HttpContext context)
    {
        var result = await writes.Send(new WriteCommand(Guid.NewGuid(), "cancel", OrderId: id), context.RequestAborted);
        EnsureSuccess(result);
        return Results.Ok(result.Order);
    }

    private static async Task<IResult> GetStatuses(ReferenceDataCache cache)
    {
        return Results.Ok(await cache.Statuses());
    }

    private static async Task<IResult> ChangeStatus(Guid id, StatusChangeRequest request, IOrderWriteQueue writes, HttpContext context)
    {
        var result = await writes.Send(new WriteCommand(Guid.NewGuid(), "status", OrderId: id, Status: request.Status, ExpectedStatus: request.ExpectedStatus), context.RequestAborted);
        EnsureSuccess(result);
        return Results.Ok(result.Order);
    }

    private static void EnsureSuccess(WriteReply reply)
    {
        if (reply.StatusCode >= 400) throw new ShopException(reply.Error ?? "Write failed.", reply.StatusCode);
    }

}
