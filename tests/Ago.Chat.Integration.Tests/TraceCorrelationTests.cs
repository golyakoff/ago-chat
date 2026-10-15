using System.Diagnostics;
using System.Text.Json;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-330`: the trace id a caller is handed is the W3C one an exporter indexes, in the
/// <c>X-Trace-Id</c> header and in a problem body's <c>traceId</c>. Same level as
/// `ErrorExtensionsRetryAfterTests` - no server, no containers - because what is owned here is which
/// value goes where, not ASP.NET Core's own activity creation (that is `TracingEndToEndTests`' side).
/// </summary>
public sealed class TraceCorrelationTests : IDisposable
{
    private readonly ActivitySource _source = new("Ago.Chat.Tests.TraceCorrelation");
    private readonly ActivityListener _listener;

    public TraceCorrelationTests()
    {
        // Without a listener StartActivity returns null, exactly like a host with telemetry off.
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == _source.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Fact]
    public void CurrentTraceId_WithAnActivity_IsItsW3CTraceId()
    {
        using var activity = _source.StartActivity("request");

        Assert.NotNull(activity);
        Assert.Equal(activity.TraceId.ToHexString(), TraceCorrelation.CurrentTraceId());
        Assert.Equal(32, TraceCorrelation.CurrentTraceId()!.Length);
    }

    [Fact]
    public void CurrentTraceId_WithoutAnActivity_IsNull()
    {
        Assert.Null(Activity.Current);
        Assert.Null(TraceCorrelation.CurrentTraceId());
    }

    [Fact]
    public async Task Middleware_TracedRequest_SetsTheHeaderOnTheResponse()
    {
        using var activity = _source.StartActivity("request");

        using var response = await SendThroughMiddlewareAsync();

        Assert.True(response.Headers.TryGetValues(TraceCorrelation.HeaderName, out var values));
        Assert.Equal(activity!.TraceId.ToHexString(), Assert.Single(values));
    }

    [Fact]
    public async Task Middleware_UntracedRequest_SetsNoHeader()
    {
        using var response = await SendThroughMiddlewareAsync();

        Assert.False(response.Headers.Contains(TraceCorrelation.HeaderName));
    }

    [Fact]
    public async Task ToProblem_TracedRequest_PutsTheW3CTraceIdInTheBody()
    {
        using var activity = _source.StartActivity("request");

        var traceId = await ProblemTraceIdAsync();

        Assert.Equal(activity!.TraceId.ToHexString(), traceId);
    }

    [Fact]
    public async Task ToProblem_UntracedRequest_KeepsTheFieldWithTheRequestIdentifier()
    {
        var traceId = await ProblemTraceIdAsync();

        Assert.Equal("0HN-test:00000001", traceId);
    }

    // A real TestServer rather than a DefaultHttpContext: the header is written from OnStarting, which
    // DefaultHttpContext's response feature never fires, so only a real response pipeline proves it.
    // PreserveExecutionContext lets the test's Activity.Current reach the request, so it
    // is the "server activity" the middleware sees - standing in for the one ASP.NET Core's hosting
    // layer starts in production.
    private static async Task<HttpResponseMessage> SendThroughMiddlewareAsync()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer(options => options.PreserveExecutionContext = true)
                .Configure(app => app
                    .UseTraceIdResponseHeader()
                    .Run(ctx => ctx.Response.WriteAsync("ok"))))
            .StartAsync();

        return await host.GetTestClient().GetAsync("/");
    }

    private static async Task<string?> ProblemTraceIdAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            TraceIdentifier = "0HN-test:00000001",
            Response = { Body = new MemoryStream() },
        };

        await ConversationErrors.RateLimited(TimeSpan.FromSeconds(5)).ToProblem(httpContext).ExecuteAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(httpContext.Response.Body);
        return body.RootElement.GetProperty("traceId").GetString();
    }
}
