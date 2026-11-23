namespace FiapX.VideoApi.Application.Messaging;

/// <summary>
/// Handles one event type. It runs inside the consumer's database transaction, together with the inbox
/// record, so it must only change state through the scoped DbContext and never ack/nack by itself.
/// </summary>
public interface IIntegrationEventConsumer<TData>
{
    Task HandleAsync(IncomingEvent<TData> message, CancellationToken cancellationToken);
}
