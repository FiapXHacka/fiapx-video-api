using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FiapX.VideoApi.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for identity-service: serves the OIDC discovery document and the JWKS of a locally generated RSA key
/// through the JwtBearer backchannel, and issues tokens (valid or deliberately wrong) for the auth tests.
/// </summary>
public sealed class FakeIdentity : IDisposable
{
    public const string Issuer = "https://issuer.test";
    public const string Audience = "fiapx-api";
    public const string MetadataAddress = "http://identity.test/.well-known/openid-configuration";
    private const string JwksAddress = "http://identity.test/.well-known/jwks.json";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public FakeIdentity()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };
    }

    public HttpMessageHandler CreateBackchannel() => new DiscoveryHandler(this);

    public string CreateToken(
        string? issuer = Issuer,
        string? audience = Audience,
        DateTime? expires = null,
        SigningCredentials? credentials = null)
    {
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(10);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "01936f50-0000-7000-8000-000000000042"),
                new Claim("email", "investor@fiapx.test"),
                new Claim("name", "Investor"),
            ]),
            IssuedAt = exp.AddMinutes(-20),
            NotBefore = exp.AddMinutes(-20),
            Expires = exp,
            SigningCredentials = credentials ?? new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static SigningCredentials Hs256Credentials() =>
        new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-shared-secret-that-is-at-least-32-bytes-long")), SecurityAlgorithms.HmacSha256);

    private string JwksJson()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = _key.KeyId });
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = jwk.Kty, use = "sig", alg = SecurityAlgorithms.RsaSha256, kid = jwk.Kid, n = jwk.N, e = jwk.E } },
        });
    }

    public void Dispose() => _rsa.Dispose();

    private sealed class DiscoveryHandler(FakeIdentity identity) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.ToString() switch
            {
                MetadataAddress => JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = JwksAddress }),
                JwksAddress => identity.JwksJson(),
                _ => null,
            };
            var response = body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }
}
