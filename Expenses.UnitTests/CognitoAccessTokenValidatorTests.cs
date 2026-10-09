using System.Security.Cryptography;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Expenses.Test.Support;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Expenses.Test;

public sealed class CognitoAccessTokenValidatorTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("use")]
    [InlineData("missing-use")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("future")]
    public async Task Requires_signature_issuer_access_use_client_and_lifetime(string scenario)
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var metadata = new OpenIdConnectConfiguration();
        metadata.SigningKeys.Add(key);
        var clock = new MutableClock();
        using var validator = new CognitoAccessTokenValidator("https://issuer.example", "expected-client", clock,
            new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
        var claims = new Dictionary<string, object> { ["sub"] = "subject", ["client_id"] = scenario == "client" ? "wrong" : "expected-client" };
        if (scenario != "missing-use") claims["token_use"] = scenario == "use" ? "id" : "access";
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = scenario == "issuer" ? "https://wrong.example" : "https://issuer.example",
            Claims = claims,
            NotBefore = clock.Now.AddMinutes(scenario == "future" ? 2 : -2).UtcDateTime,
            Expires = clock.Now.AddMinutes(scenario == "expired" ? -1 : 15).UtcDateTime,
            IssuedAt = clock.Now.AddMinutes(-2).UtcDateTime,
            SigningCredentials = new SigningCredentials(scenario == "signature" ? new RsaSecurityKey(other) { KeyId = "test-key" } : key, SecurityAlgorithms.RsaSha256)
        });
        if (scenario == "valid") Assert.Equal("subject", (await validator.ValidateAsync(token, default)).Subject);
        else Assert.Equal(SessionFailure.InvalidCredentials, (await Assert.ThrowsAsync<SessionException>(() => validator.ValidateAsync(token, default))).Failure);
    }
}
