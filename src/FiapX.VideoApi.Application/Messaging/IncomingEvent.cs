namespace FiapX.VideoApi.Application.Messaging;

/// <summary>An event received from the broker, already unwrapped from its CloudEvent envelope.</summary>
public sealed record IncomingEvent<TData>(
    Guid Id,
    string Type,
    string Source,
    string? Subject,
    DateTimeOffset Time,
    TData Data);
