namespace FiapX.VideoApi.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>amqp://user:password@host:5672/vhost</summary>
    public string Uri { get; set; } = string.Empty;

    public string Exchange { get; set; } = "fiapx.events";
    public string DeadLetterExchange { get; set; } = "fiapx.dlx";

    /// <summary>CloudEvent <c>source</c> of the events this service publishes.</summary>
    public string Source { get; set; } = "fiapx/video-api";

    public string ClientName { get; set; } = "video-api";
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// Attempts per delivery before dead-lettering. Retries run in process because RabbitMQ 4 does not count
    /// <c>basic.nack(requeue)</c> towards the quorum queue delivery limit (it only counts consumer failures).
    /// </summary>
    public int MaxHandlerAttempts { get; set; } = 3;

    /// <summary>Delay before the second attempt; doubles on each retry.</summary>
    public TimeSpan HandlerRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 50;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}
