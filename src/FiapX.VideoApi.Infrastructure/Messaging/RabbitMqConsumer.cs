using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>
/// Declares the video-api topology at startup and consumes <c>video-api.video-status</c> (alignment spec §1):
/// <list type="bullet">
/// <item>handled, duplicate, unknown type or envelope without id/type → <c>BasicAck</c>;</item>
/// <item>known type with invalid data → <c>BasicReject(requeue: false)</c> → DLQ;</item>
/// <item>consumer throws → in-process retries with exponential backoff and jitter, each in a fresh scope and
/// transaction, then <c>BasicReject(requeue: false)</c> → DLQ;</item>
/// <item>shutdown while handling → <c>BasicNack(requeue: true)</c>, which does not count as a delivery failure.</item>
/// </list>
/// <c>BasicNack(requeue: true)</c> is never used for failures: RabbitMQ 4 does not count it towards the quorum
/// delivery limit, so the message would loop forever. A process crash is covered by <c>x-delivery-limit</c>.
/// </summary>
internal sealed partial class RabbitMqConsumer(
    IRabbitMqConnection connection,
    MessageDispatcher dispatcher,
    IOptions<RabbitMqOptions> options,
    TimeProvider timeProvider,
    ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!dispatcher.HasHandlers)
                {
                    await DeclareTopologyAsync(stoppingToken);
                    LogNoHandlers(RabbitMqTopology.StatusQueue);
                    return;
                }

                await ConsumeUntilChannelClosesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogConsumerFailed(ex, ReconnectDelay);
            }

            try
            {
                await Task.Delay(ReconnectDelay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DeclareTopologyAsync(CancellationToken stoppingToken)
    {
        var conn = await connection.GetAsync(stoppingToken);
        await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, options.Value, stoppingToken);
    }

    private async Task ConsumeUntilChannelClosesAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var conn = await connection.GetAsync(stoppingToken);
        await using var channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: settings.ConsumerConcurrency),
            stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, settings, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: settings.PrefetchCount, global: false, stoppingToken);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) => await HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(RabbitMqTopology.StatusQueue, autoAck: false, consumer, stoppingToken);
        LogConsuming(RabbitMqTopology.StatusQueue, settings.ConsumerConcurrency, settings.PrefetchCount);

        await closed.Task.WaitAsync(stoppingToken);
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        // The body buffer is only valid during this callback; retries happen inside it.
        var messageId = delivery.BasicProperties.MessageId;
        var maxAttempts = Math.Max(1, options.Value.MaxHandlerAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var outcome = await dispatcher.DispatchAsync(delivery.Body, stoppingToken);
                if (outcome == DispatchOutcome.Rejected)
                {
                    await SettleAsync(channel, () => channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None));
                }
                else
                {
                    await SettleAsync(channel, () => channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None));
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Not a failure: give the message back without counting a delivery.
                await SettleAsync(channel, () => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None));
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = RetryDelay(attempt);
                LogHandlerRetry(ex, messageId, attempt, maxAttempts, delay);
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    await SettleAsync(channel, () => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None));
                    return;
                }
            }
            catch (Exception ex)
            {
                LogHandlerGaveUp(ex, messageId, maxAttempts);
                await SettleAsync(channel, () => channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None));
                return;
            }
        }
    }

    /// <summary>Exponential backoff from HandlerRetryDelay (attempt 1 → 1x, 2 → 2x, ...) plus up to 20% jitter.</summary>
    internal TimeSpan RetryDelay(int attempt)
    {
        var baseDelay = options.Value.HandlerRetryDelay * Math.Pow(2, attempt - 1);
        var jitter = baseDelay * (RandomNumberGenerator.GetInt32(0, 201) / 1000.0);
        return baseDelay + jitter;
    }

    private async Task SettleAsync(IChannel channel, Func<ValueTask> settle)
    {
        try
        {
            await settle();
        }
        catch (Exception ex) when (!channel.IsOpen)
        {
            // The channel is gone: the broker requeues unacked messages by itself.
            LogSettleSkipped(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No event consumers registered; {Queue} declared but not consumed (messages wait in the queue)")]
    private partial void LogNoHandlers(string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue} (concurrency {Concurrency}, prefetch {Prefetch})")]
    private partial void LogConsuming(string queue, ushort concurrency, ushort prefetch);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer failed, reconnecting in {Delay}")]
    private partial void LogConsumerFailed(Exception exception, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer failed for message {MessageId} (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}")]
    private partial void LogHandlerRetry(Exception exception, string? messageId, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer failed for message {MessageId} after {MaxAttempts} attempts; rejecting it to the DLQ")]
    private partial void LogHandlerGaveUp(Exception exception, string? messageId, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ack/reject skipped because the channel is closed: {Reason}")]
    private partial void LogSettleSkipped(string reason);
}
