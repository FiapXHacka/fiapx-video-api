using FiapX.VideoApi.Application.Messaging;
using RabbitMQ.Client;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>
/// Exchanges and queues owned by video-api (fiapx/docs/architecture/04-mensageria.md). Declarations are
/// idempotent; arguments must match the definitions used by docker-compose and the cluster.
/// </summary>
internal static class RabbitMqTopology
{
    public const string StatusQueue = "video-api.video-status";
    public const string StatusDeadLetterQueue = StatusQueue + ".dlq";
    public const int StatusQueueDeliveryLimit = 5;

    public static IReadOnlyList<string> StatusBindings { get; } =
    [
        EventTypes.VideoProcessingStarted,
        EventTypes.VideoProcessed,
        EventTypes.VideoProcessingFailed,
    ];

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: options.Exchange, type: ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchange: options.DeadLetterExchange, type: ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: StatusDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: StatusDeadLetterQueue, exchange: options.DeadLetterExchange, routingKey: StatusDeadLetterQueue,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: StatusQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = StatusQueueDeliveryLimit,
                ["x-dead-letter-exchange"] = options.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = StatusDeadLetterQueue,
            },
            cancellationToken: cancellationToken);
        foreach (var routingKey in StatusBindings)
        {
            await channel.QueueBindAsync(
                queue: StatusQueue, exchange: options.Exchange, routingKey: routingKey,
                cancellationToken: cancellationToken);
        }
    }
}
