using System.Text.Json;
using System.Text.Json.Serialization;

namespace FiapX.VideoApi.Infrastructure.Messaging;

/// <summary>CloudEvents 1.0 envelope in structured JSON mode (ADR-0006).</summary>
public sealed record CloudEvent<TData>
{
    [JsonPropertyName("specversion")]
    public string SpecVersion { get; init; } = "1.0";

    /// <summary>Message id: the idempotency key used by the inbox.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Event type, also the routing key.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("time")]
    public required DateTimeOffset Time { get; init; }

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    [JsonPropertyName("dataschema")]
    public string? DataSchema { get; init; }

    [JsonPropertyName("datacontenttype")]
    public string? DataContentType { get; init; } = "application/json";

    /// <summary>W3C trace context extension, links producer and consumer traces (ADR-0015).</summary>
    [JsonPropertyName("traceparent")]
    public string? TraceParent { get; init; }

    [JsonPropertyName("data")]
    public required TData Data { get; init; }
}

public static class CloudEventJson
{
    public const string ContentType = "application/cloudevents+json";

    /// <summary>camelCase for <c>data</c>, as in docs/contracts/asyncapi.yaml.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DataSchemaFor(string type) => $"fiapx/{type}/v1";
}
