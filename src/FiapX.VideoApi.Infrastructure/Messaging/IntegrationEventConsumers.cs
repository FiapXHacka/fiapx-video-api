using System.Text.Json;
using FiapX.VideoApi.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>Maps an event type (routing key) to the consumer that processes it.</summary>
public sealed class IntegrationEventRegistration
{
    internal IntegrationEventRegistration(
        string eventType,
        Func<JsonElement, object> deserialize,
        Func<IServiceProvider, CloudEvent<JsonElement>, object, CancellationToken, Task> invoke)
    {
        EventType = eventType;
        Deserialize = deserialize;
        Invoke = invoke;
    }

    public string EventType { get; }

    /// <summary>Turns <c>data</c> into the typed payload; throws <see cref="InvalidEventDataException"/> if it cannot.</summary>
    internal Func<JsonElement, object> Deserialize { get; }

    internal Func<IServiceProvider, CloudEvent<JsonElement>, object, CancellationToken, Task> Invoke { get; }
}

/// <summary>The <c>data</c> of a known event type is invalid: retrying cannot fix it, so the message goes to the DLQ.</summary>
public sealed class InvalidEventDataException : Exception
{
    public InvalidEventDataException()
    {
    }

    public InvalidEventDataException(string message)
        : base(message)
    {
    }

    public InvalidEventDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class IntegrationEventConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TConsumer"/> for <paramref name="eventType"/>. The RabbitMQ consumer only starts
    /// when at least one consumer is registered; until then events wait in the durable queue.
    /// </summary>
    public static IServiceCollection AddIntegrationEventConsumer<TData, TConsumer>(
        this IServiceCollection services, string eventType)
        where TData : class
        where TConsumer : class, IIntegrationEventConsumer<TData>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        services.AddScoped<TConsumer>();
        services.AddSingleton(new IntegrationEventRegistration(
            eventType,
            data => DeserializeData<TData>(eventType, data),
            async (provider, envelope, data, cancellationToken) =>
            {
                var consumer = provider.GetRequiredService<TConsumer>();
                await consumer.HandleAsync(
                    new IncomingEvent<TData>(envelope.Id, envelope.Type, envelope.Source, envelope.Subject, envelope.Time, (TData)data),
                    cancellationToken);
            }));
        return services;
    }

    private static TData DeserializeData<TData>(string eventType, JsonElement data)
        where TData : class
    {
        try
        {
            return data.Deserialize<TData>(CloudEventJson.Options)
                ?? throw new InvalidEventDataException($"{eventType} has no data.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidEventDataException($"{eventType} data is invalid: {ex.Message}", ex);
        }
    }
}
