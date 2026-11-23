using System.Diagnostics;
using System.Text.Json;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>Adds the event to the current DbContext; it is committed together with the caller's changes.</summary>
internal sealed class EfOutbox(VideoDbContext db, TimeProvider timeProvider, IOptions<RabbitMqOptions> options) : IOutbox
{
    public void Enqueue<TData>(string type, string subject, TData data)
        where TData : notnull
    {
        var message = OutboxMessageFactory.Create(
            type, subject, data, options.Value.Source, timeProvider.GetUtcNow(), Activity.Current?.Id);
        db.OutboxMessages.Add(message);
    }
}

internal static class OutboxMessageFactory
{
    public static OutboxMessage Create<TData>(
        string type,
        string subject,
        TData data,
        string source,
        DateTimeOffset now,
        string? traceParent)
        where TData : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var envelope = new CloudEvent<TData>
        {
            Id = Guid.CreateVersion7(now),
            Source = source,
            Type = type,
            Time = now,
            Subject = subject,
            DataSchema = CloudEventJson.DataSchemaFor(type),
            TraceParent = traceParent,
            Data = data,
        };
        return new OutboxMessage(envelope.Id, type, JsonSerializer.Serialize(envelope, CloudEventJson.Options), now);
    }
}
