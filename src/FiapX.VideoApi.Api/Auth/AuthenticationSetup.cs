using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FiapX.VideoApi.Api.Auth;

/// <summary>Validation of the tokens issued by identity-service (ADR-0011).</summary>
internal sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Value of the <c>iss</c> claim (the public issuer, also used by the API Gateway authorizer).</summary>
    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = "fiapx-api";

    /// <summary>
    /// OpenID discovery document of identity-service (internal URL), whose <c>jwks_uri</c> points to
    /// <c>/.well-known/jwks.json</c>. Keys are cached and refreshed by the JwtBearer handler.
    /// </summary>
    public string MetadataAddress { get; set; } = string.Empty;

    public bool RequireHttpsMetadata { get; set; } = true;
}

internal static class AuthenticationSetup
{
    public static IServiceCollection AddVideoApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "Auth:Issuer is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "Auth:Audience is required.")
            .Validate(o => Uri.TryCreate(o.MetadataAddress, UriKind.Absolute, out _), "Auth:MetadataAddress must be an absolute URL.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) => Configure(jwt, auth.Value));

        // Every endpoint requires a valid token unless it explicitly allows anonymous access (health checks).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    internal static void Configure(JwtBearerOptions jwt, AuthOptions auth)
    {
        jwt.MetadataAddress = auth.MetadataAddress;
        jwt.RequireHttpsMetadata = auth.RequireHttpsMetadata;
        jwt.MapInboundClaims = false; // keep "sub", "email", "name" as issued
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = auth.Issuer,
            ValidateAudience = true,
            ValidAudience = auth.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
        };
    }
}
