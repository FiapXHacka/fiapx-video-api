using System.Diagnostics;
using System.Text.Json;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>Continues the producer's trace on the consumer side using the CloudEvent traceparent (ADR-0015).</summary>
public static class MessagingTelemetry
{
    public const string SourceName = "FiapX.VideoApi.Messaging";

    private static readonly ActivitySource Source = new(SourceName);

    internal static Activity? StartConsume(CloudEvent<JsonElement> envelope)
    {
        ActivityContext.TryParse(envelope.TraceParent, traceState: null, out var parent);
        var activity = Source.StartActivity($"{envelope.Type} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.message.id", envelope.Id.ToString());
        activity?.SetTag("messaging.destination.name", envelope.Type);
        activity?.SetTag("cloudevents.event_subject", envelope.Subject);
        return activity;
    }
}
