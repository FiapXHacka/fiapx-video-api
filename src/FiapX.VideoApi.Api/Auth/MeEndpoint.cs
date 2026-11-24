namespace FiapX.VideoApi.Api.Auth;

internal static class MeEndpoint
{
    /// <summary>
    /// <c>GET /me</c>: who the token belongs to. Lets clients (and the demo) check that a token issued by
    /// identity-service is accepted here; it is also the protected endpoint used by the auth integration tests.
    /// </summary>
    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me", (HttpContext context) => Results.Ok(new
        {
            sub = context.User.FindFirst("sub")?.Value,
            email = context.User.FindFirst("email")?.Value,
            name = context.User.FindFirst("name")?.Value,
        }));
        return endpoints;
    }
}
