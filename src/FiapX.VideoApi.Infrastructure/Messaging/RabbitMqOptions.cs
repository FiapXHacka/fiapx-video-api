namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>Canonical <c>RabbitMq:*</c> keys shared by the .NET services. Queue names are code constants (topology contract).</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>amqp://user:password@host:5672/vhost (credentials come from the environment).</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>Service name: connection name in the broker and CloudEvent <c>source</c> (<c>fiapx/{ClientName}</c>).</summary>
    public string ClientName { get; set; } = "video-api";

    public string Exchange { get; set; } = "fiapx.events";
    public string DeadLetterExchange { get; set; } = "fiapx.dlx";

    public ushort PrefetchCount { get; set; } = 16;

    /// <summary>Messages handled in parallel by the consumer channel.</summary>
    public ushort ConsumerConcurrency { get; set; } = 4;

    /// <summary>
    /// Attempts per delivery before rejecting it to the DLQ. Retries run in process because RabbitMQ 4 does not
    /// count <c>basic.nack</c> towards the quorum queue delivery limit (only <c>basic.reject</c> and lost consumers).
    /// </summary>
    public int MaxHandlerAttempts { get; set; } = 3;

    /// <summary>Delay before the second attempt; doubles on each retry, with jitter.</summary>
    public TimeSpan HandlerRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public string Source => $"fiapx/{ClientName}";
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 50;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}
