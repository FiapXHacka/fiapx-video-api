using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>
/// Consumes <c>video-api.video-status</c> with manual acks. The ack happens only after the handler's transaction
/// committed. A failing handler is retried in process with backoff and then dead-lettered (nack without requeue).
/// If the process dies mid-message, the broker redelivers it and the quorum delivery limit stops crash loops.
/// Reconnects with a delay when the channel closes.
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
        if (!dispatcher.HasHandlers)
        {
            LogNoHandlers(RabbitMqTopology.StatusQueue);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

    private async Task ConsumeUntilChannelClosesAsync(CancellationToken stoppingToken)
    {
        var conn = await connection.GetAsync(stoppingToken);
        await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, options.Value, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: options.Value.PrefetchCount, global: false, stoppingToken);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) => await HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(RabbitMqTopology.StatusQueue, autoAck: false, consumer, stoppingToken);
        LogConsuming(RabbitMqTopology.StatusQueue);

        await closed.Task.WaitAsync(stoppingToken);
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        // The body buffer is only valid during this callback, and retries happen inside it.
        var messageId = delivery.BasicProperties.MessageId;
        var maxAttempts = Math.Max(1, options.Value.MaxHandlerAttempts);
        var delay = options.Value.HandlerRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dispatcher.DispatchAsync(delivery.Body, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down: leave the message unacked, the broker redelivers it to another consumer.
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                LogHandlerRetry(ex, messageId, attempt, maxAttempts, delay);
                await Task.Delay(delay, timeProvider, stoppingToken);
                delay *= 2;
            }
            catch (Exception ex)
            {
                LogHandlerGaveUp(ex, messageId, maxAttempts);
                await DeadLetterAsync(channel, delivery.DeliveryTag);
                return;
            }
        }
    }

    // requeue: false sends the message to the queue's dead-letter exchange (video-api.video-status.dlq).
    private async Task DeadLetterAsync(IChannel channel, ulong deliveryTag)
    {
        try
        {
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, CancellationToken.None);
        }
        catch (Exception ex) when (!channel.IsOpen)
        {
            // The channel is gone: the broker requeues unacked messages by itself.
            LogNackSkipped(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No event handlers registered; not consuming {Queue} (messages stay in the queue)")]
    private partial void LogNoHandlers(string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue}")]
    private partial void LogConsuming(string queue);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer failed, reconnecting in {Delay}")]
    private partial void LogConsumerFailed(Exception exception, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handler failed for message {MessageId} (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}")]
    private partial void LogHandlerRetry(Exception exception, string? messageId, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler failed for message {MessageId} after {MaxAttempts} attempts; sending it to the DLQ")]
    private partial void LogHandlerGaveUp(Exception exception, string? messageId, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Nack skipped because the channel is closed: {Reason}")]
    private partial void LogNackSkipped(string reason);
}
