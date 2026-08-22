using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Ago.Chat.Api.Cors;

/// <summary>
/// `5-01`, layer 1: allows an `Origin` if *any* site knows it - `CheckCorsOriginHandler`'s own remarks
/// explain why a CORS preflight cannot resolve *which* site a request is for, and why this is
/// therefore not the tenant-isolation boundary (that is the in-app check each caller makes once it has
/// actually resolved a specific site - `AuthEndpoints.HandleVisitorSessionAsync`,
/// `HubConnectionRegistration`).
///
/// Registered as a singleton (ASP.NET Core resolves `ICorsPolicyProvider` once, not per request), so
/// `CheckCorsOriginHandler` - scoped, like every other Application handler - is resolved from
/// <see cref="HttpContext.RequestServices"/> per call rather than injected into this class's own
/// constructor.
/// </summary>
public sealed class SiteOriginCorsPolicyProvider : ICorsPolicyProvider
{
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Same-origin call (the dev harness, local-dev.md) - no CORS headers needed at all.
            return null;
        }

        var checkOrigin = context.RequestServices.GetRequiredService<CheckCorsOriginHandler>();
        var allowed = await checkOrigin.HandleAsync(new CheckOriginAllowed(origin), context.RequestAborted);
        if (!allowed)
        {
            // No policy at all - the browser gets no Access-Control-Allow-Origin header and blocks
            // the page's own JS from reading the response. Never a wildcard, never "allow anyway".
            return null;
        }

        // No AllowCredentials(): visitor/operator tokens travel in the request body, an Authorization
        // header, or the ?access_token= query string (realtime.md) - never a cookie - so credentialed
        // CORS mode buys nothing here and would only narrow what a caller can do.
        return new CorsPolicyBuilder(origin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .Build();
    }
}
