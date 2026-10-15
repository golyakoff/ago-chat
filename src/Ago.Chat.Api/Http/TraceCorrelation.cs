using System.Diagnostics;

namespace Ago.Chat.Api.Http;

/// <summary>
/// `26-330`: the one place an HTTP caller learns which trace its request became. Before this the only
/// "trace id" a caller ever saw was <c>HttpContext.TraceIdentifier</c> in a problem body - Kestrel's
/// connection-and-request counter (<c>0HN...:00000001</c>), which no tracing backend indexes - so a
/// support ticket quoting it could not be turned into a Jaeger search. The W3C trace id of the server
/// activity <c>AddPlatformObservability</c>'s ASP.NET Core instrumentation starts is the value Jaeger
/// finds, and it is what both the <c>X-Trace-Id</c> response header and the problem body's
/// <c>traceId</c> carry now.
///
/// A request with no listening <c>ActivitySource</c> (a host started without telemetry, a unit test)
/// has no activity and therefore no trace id: <see cref="CurrentTraceId"/> returns null and the header
/// is simply absent, rather than being filled with a value that looks like a trace id and is not.
/// </summary>
public static class TraceCorrelation
{
    /// <summary>Response header carrying the 32-hex-digit W3C trace id. Exposed through CORS
    /// (<c>SiteOriginCorsPolicyProvider</c>) so the widget and console can read it from a browser.</summary>
    public const string HeaderName = "X-Trace-Id";

    /// <summary>The current activity's W3C trace id, or null when the request is not being traced.</summary>
    public static string? CurrentTraceId()
    {
        var activity = Activity.Current;
        return activity is { IdFormat: ActivityIdFormat.W3C } && activity.TraceId != default
            ? activity.TraceId.ToHexString()
            : null;
    }

    /// <summary>
    /// Adds <see cref="HeaderName"/> to every response of a traced request, including 4xx/5xx and
    /// CORS-rejected ones. Registered right after <c>UseForwardedHeaders</c>, before CORS and auth, so
    /// a request those reject still tells the caller which trace to look for. Written from
    /// <c>OnStarting</c> so a handler that replaces the response cannot drop it.
    /// </summary>
    public static IApplicationBuilder UseTraceIdResponseHeader(this IApplicationBuilder app) =>
        app.Use(static (context, next) =>
        {
            if (CurrentTraceId() is { } traceId)
            {
                context.Response.OnStarting(
                    static state =>
                    {
                        var (response, id) = ((HttpResponse, string))state;
                        response.Headers[HeaderName] = id;
                        return Task.CompletedTask;
                    },
                    (context.Response, traceId));
            }

            return next(context);
        });
}
