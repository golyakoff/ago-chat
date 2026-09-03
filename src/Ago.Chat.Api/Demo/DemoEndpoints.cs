using System.Globalization;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.MintDemoTenant;

namespace Ago.Chat.Api.Demo;

/// <summary>
/// `8-07`/`adr/0058`: the one route in this codebase that creates rows for a completely anonymous
/// caller.
///
/// <para><b>Unauthenticated on purpose, and that is the whole feature</b> - Done-when #1 is "a stranger
/// can obtain working console credentials without the author doing anything", and any gate at all
/// defeats it. What replaces authentication is two guards that authentication would not have given
/// anyway: a per-IP rate limit and a total cap on live demo tenants, both enforced in
/// <see cref="MintDemoTenantHandler"/> where they can be tested without a socket.</para>
///
/// <para><b>Not a second registration path.</b> `8-07`'s Out of scope is explicit that this must not
/// become self-service signup for real customers, which is Stage 10's `POST /api/v1/sites` behind
/// `RequireKeycloakIdentity`. The two differ in what they produce, not only in who may call them:
/// everything this mints carries <c>demo_expires_at</c> and is deleted within a day.</para>
/// </summary>
public static class DemoEndpoints
{
    public static void MapDemoEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/demo/credentials", HandleMintAsync)
            .AllowAnonymous();
    }

    // Public, not private: the same reason AuthEndpoints.HandleVisitorSessionAsync is public - a named
    // method Minimal API happily takes as a method group, and a test can then call it directly with
    // hand-built dependencies (RateLimitingTests' own precedent), no hosting/routing pipeline needed.
    public static async Task<IResult> HandleMintAsync(
        MintDemoTenantHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // No request body at all: there is nothing a caller could usefully say, and every field this
        // endpoint might have accepted would be an attacker-controlled string written into a tenant
        // row. The absence is a design decision, not an omission.
        //
        // Best-effort IP, the same fallback `SitesEndpoints` uses - `RemoteIpAddress` is null under
        // some hosting and test setups, and "unknown" still buckets those together rather than letting
        // them past the limiter entirely.
        var requestIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var result = await handler.HandleAsync(new MintDemoTenant(requestIp), cancellationToken);
        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // `ago-root#347`: a deliberate refusal must not read as a fault, and api-design.md's own
            // widget-facing rule ("returns 429 with Retry-After, which the widget must honour") applies
            // here too - ErrorExtensions maps demo.rate_limited to 429 on its own, but the header still
            // needs the number, which Error itself cannot carry (DemoTenantErrors.
            // TryGetRateLimitedRetryAfterSeconds's own remarks).
            if (DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds(error) is { } retryAfterSeconds)
            {
                httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            }

            return error.ToProblem(httpContext);
        }

        var minted = result.Value;
        return Results.Ok(new MintedDemoTenantResponse(
            minted.Username,
            minted.Password,
            minted.SiteName,
            minted.SitePublicKey,
            minted.VisitorUrl,
            minted.ExpiresAt));
    }
}

/// <summary>
/// The wire shape. Its own type rather than returning <see cref="MintedDemoTenant"/> directly, per
/// `api-design.md` - an Application result and an HTTP contract are allowed to diverge, and this one
/// will the moment the console wants a field the handler does not produce.
///
/// <para><see cref="Password"/> crosses the wire exactly once and is stored nowhere: not in Postgres,
/// not in a log, not in the outbox. Keycloak holds its hash and the viewer holds the plaintext on
/// screen. That is the entire reason this item can sidestep `10-05`'s email verification - there is no
/// address to send it to and nothing to recover if it is lost, because minting another takes one
/// click.</para>
/// </summary>
public sealed record MintedDemoTenantResponse(
    string Username,
    string Password,
    string SiteName,
    string SitePublicKey,
    string VisitorUrl,
    DateTimeOffset ExpiresAt);
