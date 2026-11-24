using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FiapX.VideoApi.IntegrationTests.Infrastructure;

namespace FiapX.VideoApi.IntegrationTests;

/// <summary>
/// Token validation through the real JwtBearer pipeline: OIDC discovery and JWKS are served by
/// <see cref="FakeIdentity"/> (alignment spec §3, ADR-0011).
/// </summary>
[Collection(PlatformTests.Name)]
public sealed class AuthTests(PlatformFixture fixture)
{
    private async Task<HttpResponseMessage> GetMeAsync(string? token)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/me", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_rs256_token_is_accepted()
    {
        using var response = await GetMeAsync(fixture.Identity.CreateToken());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("sub").GetString().ShouldBe("01936f50-0000-7000-8000-000000000042");
        body.RootElement.GetProperty("email").GetString().ShouldBe("investor@fiapx.test");
    }

    public static TheoryData<string> InvalidTokens() => new()
    {
        "wrong-issuer",
        "wrong-audience",
        "expired",
        "hs256",
        "garbage",
    };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_tokens_are_rejected(string kind)
    {
        var token = kind switch
        {
            "wrong-issuer" => fixture.Identity.CreateToken(issuer: "https://evil.test"),
            "wrong-audience" => fixture.Identity.CreateToken(audience: "another-api"),
            "expired" => fixture.Identity.CreateToken(expires: DateTime.UtcNow.AddMinutes(-5)),
            "hs256" => fixture.Identity.CreateToken(credentials: FakeIdentity.Hs256Credentials()),
            _ => "not-a-jwt",
        };

        using var response = await GetMeAsync(token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_token_is_rejected_but_health_stays_anonymous()
    {
        using var me = await GetMeAsync(token: null);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = fixture.Factory.CreateClient();
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
