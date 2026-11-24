using FiapX.VideoApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiapX.VideoApi.Infrastructure.Messaging;

internal sealed record OutboxBatchResult(int Published, bool Failed, bool Full);

/// <summary>Publishes one batch of pending outbox messages, in order, stopping at the first failure.</summary>
internal sealed partial class OutboxProcessor(
    VideoDbContext db,
    IMessagePublisher publisher,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    public async Task<OutboxBatchResult> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var batchSize = options.Value.BatchSize;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED lets several replicas relay in parallel without publishing the same row twice.
        var batch = await db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE published_at IS NULL ORDER BY occurred_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var published = 0;
        var failed = false;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(
                    new OutgoingMessage(message.Id, message.Type, message.Payload, message.OccurredAt), cancellationToken);
                message.MarkPublished(timeProvider.GetUtcNow());
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.RegisterFailedAttempt();
                LogPublishFailed(ex, message.Id, message.Type, message.Attempts);
                failed = true;
                break; // keep ordering: retry this message first on the next round
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OutboxBatchResult(published, failed, batch.Count == batchSize);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish outbox message {MessageId} ({Type}), attempt {Attempts}")]
    private partial void LogPublishFailed(Exception exception, Guid messageId, string type, int attempts);
}

/// <summary>Background loop that drains the outbox (polling with backoff on failures).</summary>
internal sealed partial class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled();
            return;
        }

        var backoff = options.Value.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                var result = await processor.PublishPendingAsync(stoppingToken);
                if (result.Failed)
                {
                    backoff = NextBackoff(backoff);
                    delay = backoff;
                }
                else
                {
                    backoff = options.Value.PollInterval;
                    delay = result.Full ? TimeSpan.Zero : options.Value.PollInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                backoff = NextBackoff(backoff);
                delay = backoff;
                LogRelayFailed(ex, delay);
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        var doubled = current * 2;
        return doubled > options.Value.MaxBackoff ? options.Value.MaxBackoff : doubled;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay disabled (Outbox:Enabled=false)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay failed, retrying in {Delay}")]
    private partial void LogRelayFailed(Exception exception, TimeSpan delay);
}
