using System.Diagnostics;
using Ago.Chat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// The tracing-signal counterpart to <see cref="TelegramTokenRedactingLoggingHandlerTests"/>, and a
/// second leak of the same secret through a completely separate pipe. `14-07`'s own logging leak was
/// closed by <c>RemoveAllLoggers()</c> plus <see cref="TelegramTokenRedactingLoggingHandler"/>; that
/// does nothing at all for OpenTelemetry, which does not observe the <c>HttpClient</c> handler chain
/// at all - it listens to <c>System.Net.Http</c>'s own <c>DiagnosticSource</c> from *inside*
/// <c>SocketsHttpHandler</c>, below every <see cref="DelegatingHandler"/>, and records
/// <c>url.full</c> on every outbound client span. <c>Ago.Platform.Observability</c>'s
/// <c>AddPlatformObservability</c> wires <c>AddHttpClientInstrumentation()</c> in all three serving
/// hosts, so before this fix a real bot token was written into this deployment's own Jaeger on every
/// <c>sendMessage</c>, <c>getUpdates</c> and <c>getMe</c>.
///
/// <para>OpenTelemetry.Instrumentation.Http 1.18.0 redacts exactly two things in <c>url.full</c> on
/// its own - the URI's <c>userinfo</c> component and the *values* in the query string (the latter
/// switchable off with <c>DisableUrlQueryRedaction</c>). The path is never touched, which is safe for
/// every other client in this codebase and exactly unsafe for Telegram, whose auth travels in the
/// path (<see cref="TelegramBotApiOptions"/>' own remarks). Confirmed empirically by this test failing
/// before the fix, not inferred from the instrumentation's documentation.</para>
///
/// <para>Two levels of proof, the same pair <see cref="TelegramTokenRedactingLoggingHandlerTests"/>
/// uses: the pure-function facts below, and an end-to-end fact that drives a real
/// <see cref="TelegramApiClient"/> against a real Kestrel stand-in through a real
/// <see cref="TracerProvider"/> built the way <c>ChatModule</c> builds it, so a wiring mistake (the
/// enrichment registered under a different options name, or never reached by the instrumentation)
/// fails the test rather than passing it.</para>
/// </summary>
public sealed class TelegramTraceUrlRedactionTests
{
    private const string RealToken = "8957102923:AAFnBe_1H9lGMWJJNOCVOHylmdtKbY32KBk-not-a-real-secret";

    [Fact]
    public async Task ARealTelegramCall_NeverPutsTheTokenIntoAnyExportedSpan()
    {
        await using var telegram = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{RealToken}/getMe", () => Results.Json(new { ok = true, result = new { id = 1 } })));

        var exported = new List<Activity>();
        var services = new ServiceCollection();
        // The one registration ChatModule makes for this, resolved through the same IOptions pipeline
        // the instrumentation itself reads - not a hand-built HttpClientTraceInstrumentationOptions
        // instance, which would prove the redaction function and nothing about the wiring.
        services.AddTelegramTokenTraceRedaction();
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddHttpClientInstrumentation()
            .AddInMemoryExporter(exported));

        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using (var httpClient = new HttpClient { BaseAddress = new Uri(telegram.BaseUrl) })
        {
            var client = new TelegramApiClient(httpClient);

            var result = await client.GetMeAsync(RealToken, CancellationToken.None);

            Assert.True(result.Ok);
        }

        tracerProvider.ForceFlush();

        // Other test classes in this assembly run in parallel and make their own outbound HTTP calls,
        // and the TracerProvider built above observes the whole process for as long as it lives - so
        // this test's own span is picked out by the ephemeral port its fake Telegram host is listening
        // on, never by assuming it was the only one exported. (Found by the full-suite run: filtered to
        // this one class the test passed, and in the whole assembly a stray "POST" span from a
        // neighbouring test made it fail on a bare Assert.Single.)
        var origin = telegram.BaseUrl.TrimEnd('/');
        var span = Assert.Single(
            exported,
            activity => activity.GetTagItem(TelegramTraceUrlRedaction.UrlFullTagName)?.ToString()
                ?.StartsWith(origin, StringComparison.Ordinal) == true);

        Assert.DoesNotContain(RealToken, span.DisplayName, StringComparison.Ordinal);
        // Every tag, not just url.full - the claim is "the token is nowhere in this span", which a
        // targeted assertion on one attribute could not make.
        Assert.All(span.TagObjects, tag =>
            Assert.DoesNotContain(RealToken, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));

        // Not just "the token is gone" - the span must still say which Telegram method was called, or
        // the fix would have bought secrecy by destroying the diagnostics the span exists for.
        var urlFull = span.GetTagItem(TelegramTraceUrlRedaction.UrlFullTagName)?.ToString();
        Assert.NotNull(urlFull);
        Assert.Contains("/bot***/getMe", urlFull, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "https://api.telegram.org/bot123456:AAExampleToken/getUpdates?timeout=30",
        "https://api.telegram.org/bot***/getUpdates?timeout=30")]
    [InlineData(
        "https://api.telegram.org/bot123456:AAExampleToken/sendMessage",
        "https://api.telegram.org/bot***/sendMessage")]
    public void RedactBotTokenInUrlFull_ForAUrlTelegramApiClientActuallyProduces_ReplacesOnlyTheTokenSegment(
        string requestUrl, string expected)
    {
        using var activity = StartClientActivity(requestUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

        TelegramTraceUrlRedaction.RedactBotTokenInUrlFull(activity, request);

        Assert.Equal(expected, activity.GetTagItem(TelegramTraceUrlRedaction.UrlFullTagName));
    }

    /// <summary>The gate is structural (a <c>bot&lt;something&gt;:&lt;something&gt;</c> path segment),
    /// not "is this host api.telegram.org" - so it keeps working if a deployment points
    /// <see cref="TelegramBotApiOptions.BaseUrl"/> at a mirror, and it must leave every other outbound
    /// client in this process untouched. This fact is the second half of that claim.</summary>
    [Fact]
    public void RedactBotTokenInUrlFull_ForANonTelegramUrl_LeavesTheSpanExactlyAsTheInstrumentationWroteIt()
    {
        const string Url = "https://storage.example.com/bucket/attachments/robot-picture.png";
        using var activity = StartClientActivity(Url);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);

        TelegramTraceUrlRedaction.RedactBotTokenInUrlFull(activity, request);

        Assert.Equal(Url, activity.GetTagItem(TelegramTraceUrlRedaction.UrlFullTagName));
    }

    /// <summary>Stands in for what the instrumentation has already done by the time its enrichment
    /// hook runs: an activity that already carries the unredacted <c>url.full</c> this fix overwrites.
    /// An <see cref="ActivityListener"/> is required for <see cref="ActivitySource.StartActivity"/> to
    /// return anything at all.</summary>
    private static Activity StartClientActivity(string requestUrl)
    {
        var source = new ActivitySource("Ago.Chat.Tests.TelegramTraceUrlRedaction");
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var activity = source.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag(TelegramTraceUrlRedaction.UrlFullTagName, requestUrl);
        return activity;
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary><see cref="TelegramApiClientTests"/>' own established technique - a real Kestrel host
    /// on a real ephemeral loopback port, standing in for Telegram's own API.</summary>
    private static async Task<TestHost> BuildFakeTelegramHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
