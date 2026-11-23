using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FiapX.VideoApi.Infrastructure.Messaging;

internal sealed record OutgoingMessage(Guid Id, string Type, string Payload, DateTimeOffset OccurredAt);

internal interface IMessagePublisher
{
    /// <summary>Returns only after the broker confirmed the message; throws if it was nacked or unroutable.</summary>
    Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes persistent messages with publisher confirms. <c>mandatory</c> is set, so a message with no bound
/// queue fails instead of being dropped, and stays in the outbox until a consumer queue exists (RF-02).
/// </summary>
internal sealed class RabbitMqPublisher(IRabbitMqConnection connection, IOptions<RabbitMqOptions> options)
    : IMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1); // channels are not thread-safe
    private IChannel? _channel;

    public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = message.Id.ToString(),
                Type = message.Type,
                ContentType = CloudEventJson.ContentType,
                Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            };
            await channel.BasicPublishAsync(
                exchange: options.Value.Exchange,
                routingKey: message.Type,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken: cancellationToken);
        }
        catch
        {
            await ResetChannelAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        var conn = await connection.GetAsync(cancellationToken);
        var channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await RabbitMqTopology.DeclareAsync(channel, options.Value, cancellationToken);
        _channel = channel;
        return channel;
    }

    private async Task ResetChannelAsync()
    {
        if (_channel is { IsOpen: true })
        {
            return;
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
