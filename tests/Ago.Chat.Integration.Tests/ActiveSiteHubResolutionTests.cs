using Ago.Chat.Api.Auth;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-07`/`adr/0068`: the one piece of this item with real transport uncertainty - does a
/// client-supplied signal actually reach <see cref="OperatorIdentityClaimsTransformation"/> during a
/// SignalR hub connection's own handshake, in this app's actual transport configuration. Verified
/// here against a **real Kestrel host, bound to a real loopback TCP port, over a real WebSocket** -
/// deliberately not `TestServer` (whose in-memory transport does not exercise the same WebSocket
/// upgrade path a browser client does) and deliberately not `OperatorHub` itself (whose constructor
/// pulls in `AssignConversationHandler`, `SendOperatorMessageHandler`, presence publishing and origin
/// validation - real dependencies for real business logic, none of which bears on the one question
/// this file answers). <see cref="ActiveSiteEchoHub"/> below carries the identical
/// <c>[Authorize(AuthenticationSchemes = JwtSchemes.Operator, Policy = "RequireOperatorIdentity")]</c>
/// attribute <c>OperatorHub</c> itself carries and reads <c>Context.User.GetSiteId()</c> the same way
/// - so this proves the transport and claims-resolution mechanism `OperatorHub` actually relies on,
/// isolated from its unrelated business wiring.
///
/// <para><b>The answer, stated plainly:</b> a header does not reliably reach a WebSocket upgrade
/// request in a browser (the same reason this app already carries the bearer token itself as
/// <c>?access_token=</c> - `Program.cs`'s own <c>HubTokenFromQueryString</c>), so the active-site
/// signal for a hub connection is a **query-string parameter**
/// (<see cref="OperatorIdentityClaimsTransformation.ActiveSiteQueryParameterName"/>), appended to the
/// hub URL exactly where the token already rides. This suite proves that value actually reaches
/// <c>HttpContext.Request.Query</c> during the real handshake and resolves to the correct, distinct
/// operator identity - not assumed by analogy with how the token already does it.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class ActiveSiteHubResolutionTests(OperatorOidcFixture fixture)
{
    [Authorize(AuthenticationSchemes = JwtSchemes.Operator, Policy = "RequireOperatorIdentity")]
    private sealed class ActiveSiteEchoHub : Hub
    {
        public Task<Guid> GetActiveSiteIdAsync() => Task.FromResult(Context.User!.GetSiteId().Value);
    }

    [Fact]
    public async Task AMultiTenantIdentitysHubConnection_WithTheQueryStringSignal_ResolvesTheCorrectDistinctSiteEachTime()
    {
        var (token, subject) = await SeedTwoTenanciesAsync();
        await using var host = await BuildTestHostAsync();

        await using var connectionA = BuildHubConnection(host.BaseUrl, token, subject.SiteA);
        await connectionA.StartAsync();
        var resolvedA = await connectionA.InvokeAsync<Guid>("GetActiveSiteIdAsync");
        Assert.Equal(subject.SiteA, resolvedA);
        await connectionA.StopAsync();

        await using var connectionB = BuildHubConnection(host.BaseUrl, token, subject.SiteB);
        await connectionB.StartAsync();
        var resolvedB = await connectionB.InvokeAsync<Guid>("GetActiveSiteIdAsync");
        Assert.Equal(subject.SiteB, resolvedB);
        await connectionB.StopAsync();
    }

    /// <summary>Same tenant-isolation invariant as <c>ActiveSiteResolutionTests</c>'s own header test,
    /// proven for the hub's own query-string mechanism instead: asking (via the connection URL, which
    /// only the client controls) for a site this identity does not administer must refuse the
    /// connection outright, never silently connect against a different one of that identity's real
    /// tenancies.</summary>
    [Fact]
    public async Task AHubConnection_RequestingASiteThisIdentityDoesNotAdminister_FailsToConnect()
    {
        var (token, subject) = await SeedTwoTenanciesAsync();
        await using var host = await BuildTestHostAsync();
        var siteNotAdministered = new SiteId(Guid.NewGuid());

        await using var connection = BuildHubConnection(host.BaseUrl, token, siteNotAdministered.Value);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    /// <summary>The ambiguous case the resolver itself refuses (`ResolveOperatorIdentityHandlerTests`'
    /// own unit proof) - here proven for the connection this identity would actually make if the
    /// console ever forgot to attach the signal: no query-string parameter at all, two real
    /// tenancies, so the handshake's own authentication pass adds no `OperatorId` claim and
    /// `RequireOperatorIdentity` refuses the connection.</summary>
    [Fact]
    public async Task AHubConnection_WithNoActiveSiteSignal_AndMoreThanOneTenancy_FailsToConnect()
    {
        var (token, _) = await SeedTwoTenanciesAsync();
        await using var host = await BuildTestHostAsync();

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.BaseUrl}hubs/test-echo", options => { options.AccessTokenProvider = () => Task.FromResult(token)!; })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    private async Task<(string AccessToken, (Guid SiteA, Guid SiteB) Sites)> SeedTwoTenanciesAsync()
    {
        var (token, username) = await fixture.CreateFreshUserAccessTokenAsync();
        var externalSubjectId = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Subject;

        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Domain.Site(siteA, $"site_{siteA.Value:N}", []));
        db.Sites.Add(new Domain.Site(siteB, $"site_{siteB.Value:N}", []));
        db.Operators.Add(new Domain.Operator(
            new Domain.OperatorId(Guid.NewGuid()), siteA, Domain.OperatorStatus.Online, 5, externalSubjectId));
        db.Operators.Add(new Domain.Operator(
            new Domain.OperatorId(Guid.NewGuid()), siteB, Domain.OperatorStatus.Online, 5, externalSubjectId));
        await db.SaveChangesAsync();

        // Silence "unused variable" for username - kept for parity with CreateFreshUserAccessTokenAsync's
        // own signature and because a future reader may want RefreshAccessTokenAsync(username) here.
        _ = username;

        return (token, (siteA.Value, siteB.Value));
    }

    private static HubConnection BuildHubConnection(string baseUrl, string token, Guid activeSiteId) =>
        new HubConnectionBuilder()
            .WithUrl(
                $"{baseUrl}hubs/test-echo?{OperatorIdentityClaimsTransformation.ActiveSiteQueryParameterName}={activeSiteId}",
                options => { options.AccessTokenProvider = () => Task.FromResult(token)!; })
            .Build();

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port - deliberately not
    /// `TestServer`, this file's own doc comment explains why.</summary>
    private async Task<TestHost> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddRouting();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<Application.Abstractions.IOperatorRepository, Infrastructure.Postgres.OperatorRepository>();
        builder.Services.AddScoped<Application.UseCases.ResolveOperatorIdentity.ResolveOperatorIdentityHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddAuthentication()
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
                    ClockSkew = TimeSpan.Zero,
                };
                // The identical SignalR-over-WebSocket token pattern `Program.cs` uses
                // (`HubTokenFromQueryString`) - a WebSocket upgrade cannot carry an Authorization
                // header, so the client passes the bearer token as `?access_token=` instead.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/test-echo"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<ActiveSiteEchoHub>("/hubs/test-echo");

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var baseUrl = addresses.Addresses.First();
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new TestHost(app, baseUrl);
    }
}
