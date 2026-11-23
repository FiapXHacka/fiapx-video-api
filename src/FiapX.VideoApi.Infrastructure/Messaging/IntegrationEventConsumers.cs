using System.Text.Json;
using FiapX.VideoApi.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>Maps an event type (routing key) to the handler that processes it.</summary>
public sealed class IntegrationEventRegistration
{
    internal IntegrationEventRegistration(
        string eventType,
        Func<IServiceProvider, CloudEvent<JsonElement>, CancellationToken, Task> invoke)
    {
        EventType = eventType;
        Invoke = invoke;
    }

    public string EventType { get; }

    internal Func<IServiceProvider, CloudEvent<JsonElement>, CancellationToken, Task> Invoke { get; }
}

public static class IntegrationEventConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <paramref name="eventType"/>. The consumer only starts when at
    /// least one handler is registered, so events keep waiting in the durable queue until a handler exists.
    /// </summary>
    public static IServiceCollection AddIntegrationEventConsumer<TData, THandler>(
        this IServiceCollection services, string eventType)
        where THandler : class, IIntegrationEventConsumer<TData>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        services.AddScoped<THandler>();
        services.AddSingleton(new IntegrationEventRegistration(eventType, async (provider, envelope, cancellationToken) =>
        {
            var data = envelope.Data.Deserialize<TData>(CloudEventJson.Options)
                ?? throw new JsonException($"Event {envelope.Id} ({envelope.Type}) has no data.");
            var handler = provider.GetRequiredService<THandler>();
            await handler.HandleAsync(
                new IncomingEvent<TData>(envelope.Id, envelope.Type, envelope.Source, envelope.Subject, envelope.Time, data),
                cancellationToken);
        }));
        return services;
    }
}
