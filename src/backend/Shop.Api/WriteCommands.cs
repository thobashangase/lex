using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

public record WriteCommand(Guid Id, string Type, CheckoutRequest? Checkout = null,
    string? Key = null, Guid? OrderId = null, string? Status = null, string? ExpectedStatus = null);
public record WriteReply(int StatusCode, Order? Order = null, bool Replayed = false, string? Error = null);

public class ProcessedWriteCommand
{
    public Guid Id { get; set; }
    public string PayloadHash { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public DateTime CompletedAt { get; set; }
}

public interface IOrderWriteQueue
{
    bool IsReady { get; }
    Task<WriteReply> Send(WriteCommand command, CancellationToken cancellationToken = default);
}

// The order mutation and its saved result commit together. A redelivery after a lost
// acknowledgment returns the saved result instead of applying an old mutation again.
public class WriteCommandProcessor(ShopDb db, OrderService orders, TimeProvider clock)
{
    public async Task<WriteReply> Process(WriteCommand command)
    {
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
        await using var transaction = await db.Database.BeginTransactionAsync();
        var existing = await db.Set<ProcessedWriteCommand>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.Id);
        if (existing is not null)
            return existing.PayloadHash == hash
                ? JsonSerializer.Deserialize<WriteReply>(existing.ResponseJson)!
                : new WriteReply(409, Error: "A command ID cannot be reused with different content.");

        WriteReply reply;
        try
        {
            switch (command.Type)
            {
                case "checkout" when command.Checkout is not null:
                    var result = await orders.Checkout(command.Checkout, command.Key ?? "");
                    reply = new(result.Replayed ? 200 : 201, result.Order, result.Replayed);
                    break;
                case "status" when command.OrderId is not null:
                    reply = new(200, await orders.ChangeStatus(command.OrderId.Value, command.Status, command.ExpectedStatus));
                    break;
                case "cancel" when command.OrderId is not null:
                    reply = new(200, await orders.Cancel(command.OrderId.Value));
                    break;
                default:
                    reply = new(400, Error: "Invalid database write command.");
                    break;
            }
        }
        catch (ShopException exception)
        {
            reply = new(exception.Status, Error: exception.Message);
        }
        db.Set<ProcessedWriteCommand>().Add(new ProcessedWriteCommand {
            Id = command.Id, PayloadHash = hash, ResponseJson = JsonSerializer.Serialize(reply),
            CompletedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return reply;
    }
}
