using FiapX.VideoApi.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FiapX.VideoApi.UnitTests.Api;

public sealed class AuthenticationSetupTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVideoApiAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void JwtBearer_validates_issuer_audience_lifetime_and_rs256_signature()
    {
        using var provider = BuildProvider(new()
        {
            ["Auth:Issuer"] = "https://api.fiapx.example",
            ["Auth:MetadataAddress"] = "http://identity-service:8080/.well-known/openid-configuration",
            ["Auth:RequireHttpsMetadata"] = "false",
        });

        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        jwt.MetadataAddress.ShouldBe("http://identity-service:8080/.well-known/openid-configuration");
        jwt.RequireHttpsMetadata.ShouldBeFalse();
        jwt.MapInboundClaims.ShouldBeFalse();
        var p = jwt.TokenValidationParameters;
        p.ValidateIssuer.ShouldBeTrue();
        p.ValidIssuer.ShouldBe("https://api.fiapx.example");
        p.ValidateAudience.ShouldBeTrue();
        p.ValidAudience.ShouldBe("fiapx-api");
        p.ValidateLifetime.ShouldBeTrue();
        p.RequireSignedTokens.ShouldBeTrue();
        p.ValidAlgorithms.ShouldBe([SecurityAlgorithms.RsaSha256]);
        p.NameClaimType.ShouldBe("sub");
    }

    [Theory]
    [InlineData(null, "http://identity/.well-known/openid-configuration")]
    [InlineData("https://issuer", null)]
    [InlineData("https://issuer", "not a url")]
    public void Missing_or_invalid_settings_fail_validation(string? issuer, string? metadataAddress)
    {
        using var provider = BuildProvider(new()
        {
            ["Auth:Issuer"] = issuer,
            ["Auth:MetadataAddress"] = metadataAddress,
        });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AuthOptions>>().Value);
    }
}
