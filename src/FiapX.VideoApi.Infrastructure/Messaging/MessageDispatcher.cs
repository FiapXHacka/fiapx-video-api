using System.Diagnostics;
using System.Text.Json;
using FiapX.VideoApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>What the consumer must do with a delivery (alignment spec §1 / ADR-0007).</summary>
internal enum DispatchOutcome
{
    /// <summary>Handled: the effect and the inbox row were committed. Ack.</summary>
    Handled,

    /// <summary>Already in the inbox for this consumer. Ack.</summary>
    Duplicate,

    /// <summary>Type not handled here, or envelope without id/type (tolerant reader). Ack.</summary>
    Ignored,

    /// <summary>Known type with invalid data: permanent. Reject without requeue (DLQ).</summary>
    Rejected,
}

/// <summary>
/// Turns a raw message into a consumer call, inside a fresh DI scope and transaction, with inbox deduplication.
/// Exceptions thrown by the consumer propagate: the RabbitMQ consumer retries and finally rejects the message.
/// </summary>
internal sealed partial class MessageDispatcher
{
    /// <summary>Inbox consumer name: one row per message per queue.</summary>
    public const string ConsumerName = RabbitMqTopology.StatusQueue;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly Dictionary<string, IntegrationEventRegistration> _registrations;

    public MessageDispatcher(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IntegrationEventRegistration> registrations,
        TimeProvider timeProvider,
        ILogger<MessageDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _registrations = registrations.ToDictionary(r => r.EventType, StringComparer.Ordinal);
    }

    public bool HasHandlers => _registrations.Count > 0;

    public async Task<DispatchOutcome> DispatchAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        CloudEvent<JsonElement>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CloudEvent<JsonElement>>(body.Span, CloudEventJson.Options);
        }
        catch (JsonException ex)
        {
            LogMalformedEnvelope(ex.Message);
            return DispatchOutcome.Ignored;
        }

        if (envelope is null || envelope.Id == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Type))
        {
            LogMalformedEnvelope("missing id or type");
            return DispatchOutcome.Ignored;
        }

        if (!_registrations.TryGetValue(envelope.Type, out var registration))
        {
            LogUnknownType(envelope.Type, envelope.Id);
            return DispatchOutcome.Ignored;
        }

        object data;
        try
        {
            data = registration.Deserialize(envelope.Data);
        }
        catch (InvalidEventDataException ex)
        {
            LogInvalidData(ex, envelope.Id, envelope.Type);
            return DispatchOutcome.Rejected;
        }

        using var activity = MessagingTelemetry.StartConsume(envelope);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the inbox row before running the consumer. A concurrent copy of the same message blocks on the
        // unique key until this transaction ends: after a commit it inserts nothing (duplicate), after a rollback
        // it takes over. So the consumer runs once even with ConsumerConcurrency > 1.
        var processedAt = _timeProvider.GetUtcNow();
        var claimed = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO inbox_messages (message_id, consumer, processed_at) VALUES ({envelope.Id}, {ConsumerName}, {processedAt}) ON CONFLICT (message_id, consumer) DO NOTHING",
            cancellationToken);
        if (claimed == 0)
        {
            LogDuplicate(envelope.Id, envelope.Type);
            activity?.SetTag("messaging.duplicate", true);
            return DispatchOutcome.Duplicate;
        }

        await registration.Invoke(scope.ServiceProvider, envelope, data, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return DispatchOutcome.Handled;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acking message without a valid envelope: {Reason}")]
    private partial void LogMalformedEnvelope(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acking message {MessageId} with type {Type}, not handled by this service")]
    private partial void LogUnknownType(string type, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rejecting message {MessageId} ({Type}) to the DLQ: invalid data")]
    private partial void LogInvalidData(Exception exception, Guid messageId, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} ({Type}) already handled, skipping")]
    private partial void LogDuplicate(Guid messageId, string type);
}
