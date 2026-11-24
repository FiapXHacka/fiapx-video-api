namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>Row of <c>outbox_messages</c>: a CloudEvent waiting to be published (ADR-0007).</summary>
public sealed class OutboxMessage
{
    public OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>CloudEvent type, used as routing key.</summary>
    public string Type { get; private set; }

    /// <summary>Complete CloudEvent JSON (stored as jsonb).</summary>
    public string Payload { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int Attempts { get; private set; }

    public void MarkPublished(DateTimeOffset at)
    {
        PublishedAt = at;
        Attempts++;
    }

    public void RegisterFailedAttempt() => Attempts++;
}

/// <summary>Row of <c>inbox_messages</c>: a message already handled by a consumer (deduplication).</summary>
public sealed class InboxMessage
{
    public InboxMessage(Guid messageId, string consumer, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        Consumer = consumer;
        ProcessedAt = processedAt;
    }

    public Guid MessageId { get; private set; }
    public string Consumer { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }
}
