using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-06`'s Done-when: "tests prove the two token schemes cannot be substituted for each other,
/// including on the shared attachment route." Until this file that was an inference - `JwtSchemes`'s
/// own doc comment asserted the audience mismatch made substitution impossible, and nothing checked
/// it. Both directions are exercised against a *real* Keycloak-issued operator token and a *real*
/// visitor token minted by <see cref="JwtTokenService"/> itself, not two hand-built principals: a
/// forged token would only ever prove that a bad signature is rejected, which is not the claim.
///
/// Three routes, matching the three shapes `Ago.Chat.Api` actually has. `/operator-only` is
/// `OperatorHub`/`/conversations/queue`'s shape (Operator scheme, `RequireOperatorIdentity`);
/// `/visitor-only` is `VisitorHub`'s (Visitor scheme, nothing else); `/shared` is `5-03`'s attachment
/// group, and it is configured from <see cref="AuthorizationPolicies.EitherTokenKind"/> - the same
/// method `AttachmentEndpoints` passes to `RequireAuthorization` - so this file exercises the real
/// policy rather than a copy of it that could drift.
///
/// The Operator scheme's own configuration is transcribed from `Program.cs` the same way
/// <see cref="OperatorOidcAuthenticationTests"/> already transcribes it, and for the same reason:
/// standing up the full host would need Postgres, RabbitMQ, Redis and MinIO to prove something about
/// token validation alone.
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class TokenSchemeSeparationTests(OperatorOidcFixture fixture)
{
    private const string VisitorTokenIssuer = "ago-chat-api";

    // `17-03`: JwtTokenService takes the key ring now. One active key, nothing retired - this file
    // is about the two *schemes* being unsubstitutable, not about rotation.
    private readonly VisitorSigningKeyRing _visitorSigningKeys = TestSigningKeys.Ring();

    [Fact]
    public async Task VisitorToken_IsRejectedWhereAnOperatorTokenIsRequired()
    {
        var (token, _) = IssueVisitorToken();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/operator-only");

        // 401, not 403: the Operator scheme never authenticated this token at all (different issuer,
        // different audience, and a signature Keycloak's JWKS cannot verify), so there is no principal
        // for `RequireOperatorIdentity` to reject. Asserting the exact status is the point - a 403
        // would mean the token had authenticated and only failed a claim check, which is a materially
        // weaker separation than the one `JwtSchemes` claims.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OperatorToken_IsRejectedWhereAVisitorTokenIsRequired()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/visitor-only");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EachTokenIsAcceptedOnItsOwnRoute()
    {
        var (visitorToken, visitorId) = IssueVisitorToken();
        var operatorToken = await fixture.GetDemoOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", visitorToken);
        var visitorResponse = await client.GetAsync("/visitor-only");
        Assert.Equal(HttpStatusCode.OK, visitorResponse.StatusCode);
        Assert.Equal(visitorId.Value, (await visitorResponse.Content.ReadFromJsonAsync<WhoResponse>())!.Subject);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var operatorResponse = await client.GetAsync("/operator-only");
        Assert.Equal(HttpStatusCode.OK, operatorResponse.StatusCode);
        Assert.Equal(fixture.SeededOperatorId.Value, (await operatorResponse.Content.ReadFromJsonAsync<WhoResponse>())!.Subject);
    }

    [Fact]
    public async Task OnTheSharedRoute_EachTokenIsClassifiedAsItsOwnKind()
    {
        var (visitorToken, visitorId) = IssueVisitorToken();
        var operatorToken = await fixture.GetDemoOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", visitorToken);
        var asVisitor = await client.GetAsync("/shared");
        Assert.Equal(HttpStatusCode.OK, asVisitor.StatusCode);
        var visitorBody = await asVisitor.Content.ReadFromJsonAsync<SharedResponse>();
        Assert.False(visitorBody!.IsOperator);
        Assert.Equal(AgoClaimTypes.VisitorKind, visitorBody.Kind);
        Assert.Equal(visitorId.Value, visitorBody.VisitorId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var asOperator = await client.GetAsync("/shared");
        Assert.Equal(HttpStatusCode.OK, asOperator.StatusCode);
        var operatorBody = await asOperator.Content.ReadFromJsonAsync<SharedResponse>();
        Assert.True(operatorBody!.IsOperator);
        Assert.Equal(AgoClaimTypes.OperatorKind, operatorBody.Kind);
        Assert.Equal(fixture.SeededOperatorId.Value, operatorBody.OperatorId);
        Assert.Equal(fixture.SeededSiteId.Value, operatorBody.SiteId);
    }

    /// <summary>
    /// `17-06`'s actual finding, and the reason the shared route's policy gained a `kind` requirement.
    /// A Keycloak token whose `sub` matches no `operators` row is neither kind - it authenticates on
    /// the Operator scheme (real signature, right issuer, right audience) but
    /// <see cref="OperatorIdentityClaimsTransformation"/> adds nothing, so before this item
    /// `IsOperator()` returned <c>false</c> and every handler on this route read that as "a visitor",
    /// parsing Keycloak's own `sub` GUID as a <c>VisitorId</c>. Nothing was reachable through it - the
    /// participant checks downstream compare that id against the conversation's real visitor - but the
    /// route is supposed to answer "which of two kinds is this", and a third kind was silently getting
    /// one of the two answers. Since `10-01` the third kind is not hypothetical: anyone can create one
    /// through the realm's public registration form.
    /// </summary>
    [Fact]
    public async Task OnTheSharedRoute_AKeycloakIdentityThatIsNotAnOperator_IsNeitherKindAndIsRejected()
    {
        var token = await fixture.GetOrphanOperatorAccessTokenAsync();

        using var host = await BuildTestHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/shared");

        // 403, not 401: this token *is* authentic - that is exactly what makes the case worth a test.
        // It is rejected because it claims to be neither kind of participant, not because Keycloak
        // failed to vouch for it.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private (string Token, VisitorId VisitorId) IssueVisitorToken()
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        // Fully qualified: `Microsoft.AspNetCore.Authentication` (used above for
        // `IClaimsTransformation`) exposes its own obsolete `SystemClock`, so an unqualified name here
        // is ambiguous rather than wrong-and-obvious.
        var tokens = new JwtTokenService(
            _visitorSigningKeys, VisitorTokenIssuer, new Ago.Platform.Hosting.SystemClock());
        return (tokens.IssueVisitorToken(visitorId, fixture.SeededSiteId), visitorId);
    }

    private async Task<IHost> BuildTestHostAsync()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(fixture.DataSource);
                    services.AddDbContext<Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext>((provider, options) =>
                        options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
                    services.AddScoped<IOperatorRepository, OperatorRepository>();
                    services.AddScoped<ResolveOperatorIdentityHandler>();
                    services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

                    services.AddAuthentication()
                        .AddJwtBearer(JwtSchemes.Visitor, options =>
                        {
                            options.MapInboundClaims = false;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = VisitorTokenIssuer,
                                ValidateAudience = true,
                                ValidAudience = JwtSchemes.Visitor,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKeyResolver = (_, _, _, _) => _visitorSigningKeys.ValidationKeys(),
                                ValidateLifetime = true,
                            };
                        })
                        .AddJwtBearer(JwtSchemes.Operator, options =>
                        {
                            options.MapInboundClaims = false;
                            options.Authority = fixture.KeycloakAuthority;
                            options.RequireHttpsMetadata = false;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateAudience = true,
                                ValidAudience = OperatorOidcFixture.ClientId,
                                ValidateLifetime = true,
                            };
                        });

                    services.AddAuthorization(options => options.AddPolicy(
                        "RequireOperatorIdentity",
                        policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId)));
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/operator-only", (HttpContext ctx) =>
                                Results.Ok(new WhoResponse(ctx.User.GetOperatorId().Value)))
                            .RequireAuthorization(new AuthorizeAttribute
                            {
                                AuthenticationSchemes = JwtSchemes.Operator,
                                Policy = "RequireOperatorIdentity",
                            });

                        endpoints.MapGet("/visitor-only", (HttpContext ctx) =>
                                Results.Ok(new WhoResponse(ctx.User.GetVisitorId().Value)))
                            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtSchemes.Visitor });

                        endpoints.MapGet("/shared", (HttpContext ctx) =>
                            {
                                var isOperator = ctx.User.IsOperator();
                                return Results.Ok(new SharedResponse(
                                    ctx.User.FindFirst(AgoClaimTypes.Kind)?.Value,
                                    isOperator,
                                    isOperator ? null : ctx.User.GetVisitorId().Value,
                                    isOperator ? ctx.User.GetOperatorId().Value : null,
                                    ctx.User.GetSiteId().Value));
                            })
                            .RequireAuthorization(AuthorizationPolicies.EitherTokenKind);
                    });
                });
            });

        return await hostBuilder.StartAsync();
    }

    private sealed record WhoResponse(Guid Subject);

    private sealed record SharedResponse(
        string? Kind, bool IsOperator, Guid? VisitorId, Guid? OperatorId, Guid SiteId);
}
