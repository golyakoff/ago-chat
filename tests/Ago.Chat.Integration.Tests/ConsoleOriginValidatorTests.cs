using Ago.Chat.Api.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-18`: the operator hub's origin check, against the origins the **console** is served from.
///
/// <para>The defect this replaces was total and silent: `OperatorHub` validated an operator's
/// connection `Origin` against the *tenant's* `AllowedOrigins` - the list of pages allowed to embed
/// that tenant's widget - so any tenant whose list did not happen to contain the console had every
/// operator connection aborted immediately after a successful SignalR handshake. Every tenant `8-07`
/// mints is such a tenant, because its list is exactly the demo shop page.</para>
///
/// <para>No fixture, no container: this reads one option list and one header. That it needs neither is
/// the point of splitting it from <see cref="HubOriginValidator"/>, which needs a database.</para>
/// </summary>
public class ConsoleOriginValidatorTests
{
    private static ConsoleOriginValidator Validator(params string[] allowed) =>
        new(new ConsoleOriginOptions { AllowedOrigins = allowed });

    [Fact]
    public void AllowsTheConfiguredConsoleOrigin()
    {
        var validator = Validator("https://console.example", "http://localhost:5173");

        Assert.True(validator.IsAllowed(ContextWithOrigin("https://console.example")));
        Assert.True(validator.IsAllowed(ContextWithOrigin("http://localhost:5173")));
    }

    /// <summary>
    /// The regression itself. A tenant's widget origin is not a console origin, and an operator
    /// connection arriving from one is exactly as unwelcome as any other stranger's page - which is
    /// the half the old code got right, on the wrong list.
    /// </summary>
    [Fact]
    public void RefusesAnOriginThatIsNotTheConsole()
    {
        var validator = Validator("https://console.example");

        Assert.False(validator.IsAllowed(ContextWithOrigin("https://demo-shop1.example")));
        Assert.False(validator.IsAllowed(ContextWithOrigin("https://console.example.evil")));
    }

    /// <summary>
    /// Exact string comparison, the same rule <c>Site.AllowedOrigins</c> already follows. A scheme or
    /// port that differs is a different origin, and treating it as the same is how an origin check
    /// stops being one.
    /// </summary>
    [Theory]
    [InlineData("http://console.example")]
    [InlineData("https://console.example:8443")]
    [InlineData("https://Console.example")]
    [InlineData("https://console.example/")]
    public void RefusesANearMiss(string origin) =>
        Assert.False(Validator("https://console.example").IsAllowed(ContextWithOrigin(origin)));

    /// <summary>
    /// No `Origin` header at all is allowed, matching <see cref="HubOriginValidator"/>'s identical
    /// branch: there is no cross-origin claim to verify. A browser always sends one; the dev harness
    /// and any non-browser client do not, and neither is what this check defends against.
    /// </summary>
    [Fact]
    public void AllowsAConnectionThatSendsNoOriginAtAll() =>
        Assert.True(Validator("https://console.example").IsAllowed(ContextWithOrigin(null)));

    /// <summary>
    /// An empty list refuses everything that carries an `Origin`. That is the safe direction, and it is
    /// why <see cref="ConsoleOriginOptions"/> is `[MinLength(1)]` and validated at startup - a
    /// deployment that forgot the setting must fail at boot, not refuse every operator in silence,
    /// which is precisely the failure mode `5-18` exists to remove.
    /// </summary>
    [Fact]
    public void RefusesEverythingWhenNoOriginIsConfigured() =>
        Assert.False(Validator().IsAllowed(ContextWithOrigin("https://console.example")));

    private static HubCallerContext ContextWithOrigin(string? origin)
    {
        var features = new FeatureCollection();
        if (origin is not null)
        {
            var inner = new DefaultHttpContext();
            inner.Request.Headers.Origin = origin;
            features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeature(inner));
        }

        return new OriginOnlyHubCallerContext(features);
    }

    private sealed class HttpContextFeature(HttpContext httpContext)
        : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private sealed class OriginOnlyHubCallerContext(IFeatureCollection features) : HubCallerContext
    {
        public override string ConnectionId => "test-connection";

        public override string? UserIdentifier => null;

        public override System.Security.Claims.ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
