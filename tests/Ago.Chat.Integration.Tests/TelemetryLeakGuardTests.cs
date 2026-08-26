using System.Collections.Concurrent;
using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `16-05`'s guard. `coding-style.md` has said "never log message bodies, tokens, presigned URLs or
/// anything from a visitor's keyboard" since it was written, and `17-02` demonstrated that a
/// convention is not a mechanism: it found a live bearer token in the edge's access log, put there by
/// a component whose logging nobody had configured. The same shape of accident is available here -
/// nothing in `Ago.Chat.*` logs a message body today, but `messages.body` is one
/// <c>EnableSensitiveDataLogging()</c> away from being written to disk on every insert, and the
/// distance between "no code logs it" and "nothing logs it" is exactly the distance a test can close.
///
/// So this test does not inspect the logging configuration; it runs the real thing and reads what
/// came out. A message whose body is a distinctive canary is written through the **production**
/// persistence wiring - <see cref="ServiceCollectionExtensions.AddPostgresPersistence"/>, the same
/// call every host makes - against a logger factory that captures *everything at
/// <see cref="LogLevel.Trace"/>*, which is stricter than any host's `appsettings.json` and therefore
/// cannot pass merely because a level happens to be turned down. The same run is watched by an
/// in-memory span exporter subscribed to the same sources `AddPlatformObservability` subscribes to,
/// because a span attribute carries user input far more casually than a log statement does: Npgsql's
/// own instrumentation puts the full statement text on `db.query.text` for every command, which is
/// safe only for as long as every value in it is a parameter.
///
/// Inverting it is one line: add <c>.EnableSensitiveDataLogging()</c> to
/// <c>AddPostgresPersistence</c>'s <c>UseNpgsql</c> call and this fails on the log assertion, with
/// the canary printed inside EF's parameter list. That is the failure mode being guarded against, and
/// it was run before this was committed. The second test's inversion is moving its canary from the
/// query string into the path, where nothing redacts it - it then fails and prints the whole
/// `url.full`.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TelemetryLeakGuardTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - `2-06` only keeps partitions for the current month and the next
    // two (MessageUniqueSequenceTests has the full reasoning). Truncated to whole seconds so it
    // round-trips through timestamptz unchanged.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// Shaped like the personal data a real support conversation collects - `personal-data.md`'s
    /// "my name is Ivan, call me on +7..." - so that a leak of it would look exactly like a leak of
    /// the real thing. Every value in it is invented: `example.invalid` is reserved by RFC 2606 and
    /// can never be a real domain, and the number is not a dialable range. `CLAUDE.md`: everything
    /// here is public, including fixtures.
    /// </summary>
    private const string Canary = "AGO-CANARY-9d41 ivan.petrov@example.invalid +7 000 000-00-00";

    /// <summary>The outbound test's canary, deliberately made of characters `Uri.EscapeDataString`
    /// leaves alone - see that test for why the difference matters.</summary>
    private const string OutboundCanary = "AGO-CANARY-outbound-3b7e";

    [Fact]
    public async Task AMessageBodyWrittenThroughTheProductionPersistenceWiring_ReachesNoLogAndNoSpanAttribute()
    {
        var capturedLogs = new CapturingLoggerProvider();
        var exportedSpans = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            // Exactly what AddPlatformObservability subscribes to, so this sees what Jaeger would
            // see rather than a hand-picked subset (TracingEndToEndTests makes the same argument).
            .AddSource(Ago.Platform.Observability.ObservabilityServiceCollectionExtensions.ActivitySourceWildcard)
            .AddSource("Npgsql")
            .AddInMemoryExporter(exportedSpans)
            .Build();

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(capturedLogs);
        });
        // The production call, not a hand-built DbContextOptions - the whole point is that the thing
        // under test is the wiring a host actually uses, including whatever EF logging that wiring
        // leaves switched on.
        services.AddPostgresPersistence(fixture.ConnectionString);
        await using var provider = services.BuildServiceProvider();

        var messageId = new MessageId(new UuidV7Generator().NewId(Now));
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgoChatDbContext>();
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();

            var conversation = await conversations.GetByIdAsync(conversationId, CancellationToken.None);
            Assert.NotNull(conversation);
            conversation.AddVisitorMessage(visitorId, messageId, new MessageBody(Canary), Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        tracerProvider.ForceFlush();

        // The test must be able to fail. If the write never happened, "the canary is nowhere" would
        // be true and worthless - so prove the body really is in the database before asserting it is
        // nowhere else.
        await using (var verify = fixture.CreateDbContext())
        {
            var stored = await verify.Set<Message>().SingleAsync(m => m.Id == messageId, CancellationToken.None);
            Assert.Equal(Canary, stored.Body.Value);
        }

        var logs = capturedLogs.Entries.ToList();
        // Same reasoning again, for the other half: a logger that captured nothing proves nothing.
        // EF logs its command text in this category at Information, and this provider is listening
        // at Trace, so an empty capture means the wiring under test was not exercised at all.
        Assert.Contains(logs, entry => entry.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, entry => entry.Text.Contains(Canary, StringComparison.Ordinal));

        Assert.NotEmpty(exportedSpans);
        var leakingTag = exportedSpans
            .SelectMany(span => span.TagObjects.Select(tag => (span.OperationName, tag.Key, Value: tag.Value?.ToString())))
            .FirstOrDefault(t => t.Value is not null && t.Value.Contains(Canary, StringComparison.Ordinal));
        Assert.True(
            leakingTag.Value is null,
            $"Span '{leakingTag.OperationName}' carried the message body on attribute '{leakingTag.Key}'.");
    }

    /// <summary>
    /// The outbound half, and the one `17-02` did not reach. That item proved the *inbound* query
    /// string is redacted before it becomes a span attribute (`url.query = ?access_token=Redacted`),
    /// and named the environment variable that would turn that off. Outbound is a separate mechanism
    /// with a separate off switch, and it matters here for a reason specific to this product: the
    /// only URL `Ago.Chat.Webhooks` ever calls is one **the tenant typed**, and a webhook URL with a
    /// shared secret in its query string (`...?token=...`) is an ordinary thing for a tenant to
    /// configure. `16-05` read a real outbound span off the local cluster's Jaeger and found
    /// `url.full` recorded in full, so the only question left is whether the query part of it is
    /// redacted - which is what this asserts, against the same `System.Net.Http` activity source the
    /// deployed hosts export.
    /// </summary>
    [Fact]
    public async Task AQueryStringOnAnOutboundHttpCall_IsRedactedBeforeItBecomesASpanAttribute()
    {
        var exportedSpans = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            // The .NET runtime's own HttpClient activity source - `AddHttpClientInstrumentation()`
            // in `AddPlatformObservability` subscribes to this one, which is why the spans in Jaeger
            // carry `otel.scope.name = System.Net.Http`.
            .AddSource("System.Net.Http")
            .AddInMemoryExporter(exportedSpans)
            .Build();

        // Port 1 on the loopback address: nothing listens there, the connection is refused
        // immediately, and the span is still produced with its URL attributes set - so this needs no
        // server and cannot hang. `16-05` confirmed against real Jaeger data that a *failed*
        // outbound call is exactly the case that produces `url.full`.
        //
        // A URL-safe canary, not the one the other test uses: `Uri.EscapeDataString` turns that one's
        // `@` into `%40` and its spaces into `%20`, so a search for its literal text could never
        // match whatever the span recorded, and the test would pass whether or not anything was
        // redacted. Found by deliberately moving the canary into the *path* - where nothing redacts
        // it - and watching the test pass anyway. A guard that cannot fail is not a guard.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var uri = new Uri($"http://127.0.0.1:1/webhooks/deliver?token={OutboundCanary}");
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(uri, CancellationToken.None));

        tracerProvider.ForceFlush();

        Assert.NotEmpty(exportedSpans);
        // The span really is about this request - otherwise "the token is nowhere" is vacuous.
        Assert.Contains(
            exportedSpans.SelectMany(span => span.TagObjects),
            tag => tag.Key == "url.full" && tag.Value?.ToString()?.Contains("/webhooks/deliver", StringComparison.Ordinal) == true);
        var leakingTag = exportedSpans
            .SelectMany(span => span.TagObjects.Select(tag => (span.OperationName, tag.Key, Value: tag.Value?.ToString())))
            .FirstOrDefault(t => t.Value is not null && t.Value.Contains(OutboundCanary, StringComparison.Ordinal));
        Assert.True(
            leakingTag.Value is null,
            $"Span '{leakingTag.OperationName}' carried the outbound query string on attribute '{leakingTag.Key}': {leakingTag.Value}");
    }

    /// <summary>
    /// Captures every log entry any category emits, formatted the way a console/JSON provider would
    /// format it, plus the raw state and any exception - because a body can reach a log file through
    /// the message template, through a structured value the template never renders, or through an
    /// exception's own message, and only the first of those is visible in the formatted text.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(string Category, string Text)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentBag<(string Category, string Text)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                entries.Add((category, state.ToString() ?? string.Empty));
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = string.Join(
                    '\n',
                    formatter(state, exception),
                    state?.ToString(),
                    exception?.ToString());
                entries.Add((category, text));
            }
        }
    }
}
