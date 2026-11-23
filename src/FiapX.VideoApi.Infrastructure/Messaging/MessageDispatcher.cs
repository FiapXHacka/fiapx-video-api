using System.Diagnostics;
using System.Text.Json;
using FiapX.VideoApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>
/// Turns a raw message into a handler call with inbox deduplication (ADR-0007).
/// Returning normally means "ack"; throwing means the consumer retries and finally dead-letters the message.
/// Messages that can never succeed (malformed, unknown type) are acked.
/// </summary>
internal sealed partial class MessageDispatcher
{
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

    /// <summary>Inbox consumer name: one row per message per queue.</summary>
    public const string ConsumerName = RabbitMqTopology.StatusQueue;

    public bool HasHandlers => _registrations.Count > 0;

    public async Task DispatchAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        CloudEvent<JsonElement>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CloudEvent<JsonElement>>(body.Span, CloudEventJson.Options);
        }
        catch (JsonException ex)
        {
            LogMalformed(ex.Message);
            return;
        }

        if (envelope is null || envelope.Id == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Type))
        {
            LogMalformed("missing id or type");
            return;
        }

        if (!_registrations.TryGetValue(envelope.Type, out var registration))
        {
            LogUnknownType(envelope.Type, envelope.Id);
            return;
        }

        using var activity = MessagingTelemetry.StartConsume(envelope);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var alreadyHandled = await db.InboxMessages.AnyAsync(
            m => m.MessageId == envelope.Id && m.Consumer == ConsumerName, cancellationToken);
        if (alreadyHandled)
        {
            LogDuplicate(envelope.Id, envelope.Type);
            activity?.SetTag("messaging.duplicate", true);
            return;
        }

        await registration.Invoke(scope.ServiceProvider, envelope, cancellationToken);
        db.InboxMessages.Add(new InboxMessage(envelope.Id, ConsumerName, _timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarding malformed message: {Reason}")]
    private partial void LogMalformed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarding message {MessageId} with unknown type {Type}")]
    private partial void LogUnknownType(string type, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} ({Type}) already handled, skipping")]
    private partial void LogDuplicate(Guid messageId, string type);
}
