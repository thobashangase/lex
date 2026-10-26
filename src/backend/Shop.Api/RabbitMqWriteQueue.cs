using System.Collections.Concurrent;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Shop.Api;

public class RabbitMqWriteQueue(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<RabbitMqWriteQueue> logger) : IOrderWriteQueue, IHostedService, IAsyncDisposable
{
    public const string QueueName = "shop.database-writes.v1";
    public const string DeadLetterQueue = "shop.database-writes.failed.v1";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WriteReply>> pending = new();
    private readonly SemaphoreSlim publishing = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private IConnection? connection;
    private IChannel? publisher;
    private IChannel? worker;
    private IChannel? replies;
    private string replyQueue = "";
    private bool initialized;
    private bool ready;
    private int disposed;
    public bool IsReady => ready && connection?.IsOpen == true && publisher?.IsOpen == true && worker?.IsOpen == true && replies?.IsOpen == true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory {
            HostName = configuration["RabbitMq:Host"] ?? "localhost",
            Port = configuration.GetValue("RabbitMq:Port", 5672),
            VirtualHost = configuration["RabbitMq:VirtualHost"] ?? "/",
            UserName = configuration["RabbitMq:Username"] ?? "shop",
            Password = configuration["RabbitMq:Password"] ?? "shop-demo-password",
            AutomaticRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };
        connection = await factory.CreateConnectionAsync(cancellationToken);
        publisher = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);
        worker = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        replies = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await worker.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await worker.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> {
                ["x-single-active-consumer"] = true,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = DeadLetterQueue,
                ["x-max-length"] = 10000,
                ["x-overflow"] = "reject-publish"
            }, cancellationToken: cancellationToken);
        await worker.BasicQosAsync(0, 1, false, cancellationToken);
        replyQueue = $"shop.write-replies.{Guid.NewGuid():N}";
        await replies.QueueDeclareAsync(replyQueue, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);
        var replyConsumer = new AsyncEventingBasicConsumer(replies);
        replyConsumer.ReceivedAsync += (_, delivery) => {
            if (delivery.BasicProperties.CorrelationId is { } id && pending.TryGetValue(id, out var completion))
            {
                try { completion.TrySetResult(JsonSerializer.Deserialize<WriteReply>(delivery.Body.Span)!); }
                catch (JsonException exception) { completion.TrySetException(exception); }
            }
            return Task.CompletedTask;
        };
        await replies.BasicConsumeAsync(replyQueue, autoAck: true, consumer: replyConsumer, cancellationToken: cancellationToken);
        var consumer = new AsyncEventingBasicConsumer(worker);
        consumer.ReceivedAsync += Handle;
        await worker.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);
        // Schema changes and demo seeding also run inside the serialized consumer, before
        // requests are served. Redelivered commands can safely trigger initialization first.
        var result = await Send(new WriteCommand(Guid.NewGuid(), "initialize"), cancellationToken);
        if (result.StatusCode != 200) throw new InvalidOperationException(result.Error);
        ready = true;
    }

    public async Task<WriteReply> Send(WriteCommand command, CancellationToken cancellationToken = default)
    {
        var id = command.Id.ToString();
        var completion = new TaskCompletionSource<WriteReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new ShopException("This write is already pending.", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await Publish(QueueName, new BasicProperties {
                Persistent = true, ContentType = "application/json", MessageId = id,
                CorrelationId = id, ReplyTo = replyQueue
            }, JsonSerializer.SerializeToUtf8Bytes(command), true, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is not ShopException)
        {
            logger.LogWarning(exception, "Write {CommandId} did not receive a confirmed response", id);
            throw new ShopException("The write could not be confirmed. It may still complete. Refresh orders before retrying; retry checkout with the same submission key.", 503);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task Publish(string queue, BasicProperties properties, byte[] body, bool mandatory, CancellationToken token)
    {
        await publishing.WaitAsync(token);
        try { await publisher!.BasicPublishAsync("", queue, mandatory, properties, body, token); }
        finally { publishing.Release(); }
    }

    private async Task Handle(object sender, BasicDeliverEventArgs delivery)
    {
        WriteCommand? command;
        try { command = JsonSerializer.Deserialize<WriteCommand>(delivery.Body.Span); }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Malformed write command sent to dead-letter queue");
            await worker!.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
            return;
        }
        if (command is null || command.Id == Guid.Empty)
        {
            await worker!.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
            return;
        }
        WriteReply? reply = null;
        for (var attempt = 1; attempt <= 3 && !stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!initialized)
                {
                    await SeedData.Initialize(scope.ServiceProvider.GetRequiredService<ShopDb>());
                    await scope.ServiceProvider.GetRequiredService<ReferenceDataCache>().RefreshAfterWrite();
                    initialized = true;
                }
                reply = command.Type == "initialize" ? new WriteReply(200)
                    : await scope.ServiceProvider.GetRequiredService<WriteCommandProcessor>().Process(command);
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Write {CommandId} failed on attempt {Attempt}", command.Id, attempt);
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), stopping.Token);
            }
        }
        try
        {
            if (stopping.IsCancellationRequested) return; // Unacked work is redelivered on shutdown.
            var failed = reply is null;
            reply ??= new WriteReply(503, Error: "The database write failed after retries. Refresh orders and retry safely.");
            if (!string.IsNullOrEmpty(delivery.BasicProperties.ReplyTo))
                await Publish(delivery.BasicProperties.ReplyTo, new BasicProperties {
                    CorrelationId = delivery.BasicProperties.CorrelationId, ContentType = "application/json"
                }, JsonSerializer.SerializeToUtf8Bytes(reply), false, stopping.Token);
            if (failed) await worker!.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
            else await worker!.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Reply/ack failed for write {CommandId}; committed writes are safe to redeliver", command.Id);
            // A connection failure leaves the delivery unacknowledged. If the worker
            // channel is still open, requeue explicitly so it cannot block the consumer.
            if (worker?.IsOpen == true)
                await worker.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        foreach (var completion in pending.Values) completion.TrySetCanceled();
        if (connection?.IsOpen == true) await connection.CloseAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (connection is not null) await connection.DisposeAsync();
        stopping.Dispose();
        publishing.Dispose();
    }
}
