namespace FiapX.VideoApi.Application.Messaging;

/// <summary>
/// Transactional outbox (ADR-0007): the event is stored in the same transaction as the state change and
/// published to RabbitMQ afterwards by the relay. Callers save their unit of work as usual.
/// </summary>
public interface IOutbox
{
    /// <param name="type">CloudEvent type, also the routing key (e.g. <c>video.uploaded</c>).</param>
    /// <param name="subject">Aggregate id the event is about.</param>
    /// <param name="data">Event payload, serialized as the CloudEvent <c>data</c>.</param>
    void Enqueue<TData>(string type, string subject, TData data)
        where TData : notnull;
}
