using Expenses.Application.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Expenses.Infrastructure.Identity;

public sealed class CognitoAccessTokenValidator : IAccessTokenValidator, IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly IConfigurationManager<OpenIdConnectConfiguration> configuration;
    private readonly string issuer;
    private readonly string clientId;
    private readonly TimeProvider clock;
    public CognitoAccessTokenValidator(CognitoSettings settings, TimeProvider clock)
    {
        issuer = $"https://cognito-idp.{settings.Region}.amazonaws.com/{settings.UserPoolId}";
        clientId = settings.ClientId;
        this.clock = clock;
        configuration = new ConfigurationManager<OpenIdConnectConfiguration>(issuer + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http) { RequireHttps = true });
    }
    // Injectable metadata allows signed-token tests without weakening production validation.
    public CognitoAccessTokenValidator(string issuer, string clientId, TimeProvider clock,
        IConfigurationManager<OpenIdConnectConfiguration> configuration)
    {
        this.issuer = issuer;
        this.clientId = clientId;
        this.clock = clock;
        this.configuration = configuration;
    }
    public async Task<VerifiedAccess> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 16384) throw new SessionException(SessionFailure.InvalidCredentials);
        OpenIdConnectConfiguration metadata;
        try { metadata = await configuration.GetConfigurationAsync(cancellationToken); }
        catch { throw new SessionException(SessionFailure.Unavailable); }
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidateIssuer = true, ValidateAudience = false,
            IssuerSigningKeys = metadata.SigningKeys, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], RequireExpirationTime = true, ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero, IncludeTokenOnFailedValidation = false, LogValidationExceptions = false,
            LifetimeValidator = (notBefore, expires, _, _) => expires.HasValue && expires > clock.GetUtcNow().UtcDateTime &&
                (!notBefore.HasValue || notBefore <= clock.GetUtcNow().UtcDateTime)
        };
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            if (result.Exception is SecurityTokenSignatureKeyNotFoundException) configuration.RequestRefresh();
            throw new SessionException(SessionFailure.InvalidCredentials);
        }
        var jwt = (JsonWebToken)result.SecurityToken;
        if (!jwt.TryGetClaim("token_use", out var use) || use.Value != "access" ||
            !jwt.TryGetClaim("client_id", out var client) || client.Value != clientId || string.IsNullOrWhiteSpace(jwt.Subject))
            throw new SessionException(SessionFailure.InvalidCredentials);
        return new VerifiedAccess(jwt.Subject, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }
    public void Dispose() => http.Dispose();
}
