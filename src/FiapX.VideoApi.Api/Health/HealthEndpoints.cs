using System.Text.Json;
using FiapX.VideoApi.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FiapX.VideoApi.Api.Health;

internal static class HealthEndpoints
{
    /// <summary>
    /// <c>/health/live</c>: the process is up (no dependency checks, so a broker outage never restarts pods).
    /// <c>/health/ready</c>: Postgres and RabbitMQ are reachable (pod receives traffic).
    /// </summary>
    public static IEndpointRouteBuilder MapVideoApiHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag),
            ResponseWriter = WriteAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
    }
}
