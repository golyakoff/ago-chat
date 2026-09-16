using System.Collections.Concurrent;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-40`: `23-07`'s first Done-when clause - "a pod restart mid-window loses at most the unflushed
/// batch and never a whole day" - depends on <see cref="WidgetActivityFlusherService"/>'s own
/// best-effort flush after its loop ends (that type's own remarks). The backlog item's own record of
/// what went wrong the first time is why this file looks the way it does, not a guess:
///
/// <para><b>A dropped predecessor of this test raced the service's own completion, not its timer.</b>
/// It already used a long <see cref="WidgetActivityOptions.FlushInterval"/> so the periodic tick could
/// not fire, and it still failed about half the time in the full test project. The one difference from
/// every other shutdown-drain test in this codebase (<c>MessagePipelineTests.StopPipelineAsync</c>,
/// the identical shape for <c>BatchFlusherService</c>) is what this file restores: a *bounded* token
/// passed to <see cref="BackgroundService.StopAsync"/> can win a race against <c>_executeTask</c> and
/// let <c>StopAsync</c> return before the final flush has actually finished writing, and a busier
/// machine (the full project's container fleet, not a filtered dozen tests) makes that race wider, not
/// narrower. <see cref="CancellationToken.None"/> never fires, so it cannot win that race - the read
/// below can only run after the final flush has completed, not merely started.</para>
///
/// <para><b>`25-112` corrected this comment's own description of the mechanism, without changing the
/// conclusion above.</b> The earlier text named <c>Task.WhenAny(_executeTask, &lt;the token&gt;)</c> as
/// <em>the</em> implementation - that is the .NET Framework compatibility path in
/// <c>dotnet/runtime</c>'s own source for <see cref="BackgroundService"/>. On this project's actual
/// target framework (`net10.0`), <c>StartAsync</c> dispatches <c>ExecuteAsync</c> through
/// <c>Task.Run</c> rather than running it inline up to its first await, and <c>StopAsync</c> awaits
/// <c>_executeTask.WaitAsync(cancellationToken, ConfigureAwaitOptions.SuppressThrowing)</c> rather than
/// racing a hand-rolled <see cref="TaskCompletionSource"/>. Passing <see cref="CancellationToken.None"/>
/// closes the race either way - a token that can never fire cannot make <c>WaitAsync</c> complete
/// early - so `23-40`'s own fix needed no change, but this file's account of *why* did.</para>
///
/// <para>What is <b>not</b> used here, deliberately: no <see cref="Task.Delay"/>, no retry loop, no
/// timeout to wait out. `23-40`'s own text names exactly this failure mode - a generous wait would
/// make the flaky version green too, without making it deterministic.</para>
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class WidgetActivityFlusherServiceTests(SiteCachingFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Proves the *final* flush specifically, not merely "some flush eventually happens": with
    /// <see cref="WidgetActivityOptions.FlushInterval"/> set to a day, the periodic tick inside
    /// <see cref="WidgetActivityFlusherService.ExecuteAsync"/> provably cannot fire during this test's
    /// own lifetime, on any machine under any load. The only remaining path for these counts to reach
    /// Postgres is the flush after the loop's own cancellation - delete that call and this test fails
    /// with zero totals, which is the fails-before this item asks for.
    /// </summary>
    [Fact]
    public async Task StopAsync_WithTheOnlyPossibleFlushBeingTheFinalOne_StillWritesTheAccumulatedCounts()
    {
        var siteId = await SeedSiteAsync(fixture);
        var accumulator = new WidgetActivityAccumulator();
        var writer = new WidgetActivityWriter(fixture.DataSource);
        var options = Options.Create(new WidgetActivityOptions { FlushInterval = TimeSpan.FromDays(1) });

        // `25-112`: this test used `NullLogger<WidgetActivityFlusherService>.Instance` until this recurrence,
        // which throws away the one signal that would tell "the write never happened" apart from "the write
        // was attempted and failed" - `ExecuteAsync`'s own final-flush block deliberately catches and only
        // *logs* a failure (this type's own remarks: an orderly shutdown must not become a crash loop over a
        // dashboard number). `23-40`'s own investigation already reached for exactly this - "a capturing
        // logger was added for exactly that" - but that instrumented version was never the one that shipped,
        // so every failure since (this one included) has had to guess. A `CapturingLogger` costs nothing on
        // the path that already passes and turns the next failure into evidence instead of another entry in
        // `23-40`'s own honest-uncertainty box.
        var logger = new CapturingLogger();
        var service = new WidgetActivityFlusherService(accumulator, writer, options, logger);

        await service.StartAsync(CancellationToken.None);
        accumulator.RecordLoad(siteId, Now);
        accumulator.RecordOpen(siteId, Now);

        // CancellationToken.None - see this class's own remarks on why any bounded token here
        // reintroduces the exact race that made the dropped predecessor of this test flaky.
        await service.StopAsync(CancellationToken.None);

        var readStore = new WidgetActivityReadStore(fixture.DataSource);
        var totals = await readStore.GetTotalsAsync(siteId, DateOnly.FromDateTime(Now.UtcDateTime), CancellationToken.None);

        try
        {
            Assert.Equal(1, totals.Loads);
            Assert.Equal(1, totals.Opens);
            Assert.Equal(0, totals.Conversations);
        }
        catch (Exception ex) when (!logger.Entries.IsEmpty)
        {
            // Only reached on an assertion failure, and only adds text when the final flush's own
            // catch block actually logged something - the ordinary passing run never touches this.
            throw new Exception(
                $"{ex.Message}\n\nThe final flush's own catch block (WidgetActivityFlusherService." +
                $"ExecuteAsync) logged during shutdown, which a NullLogger would have discarded: " +
                string.Join(" | ", logger.Entries),
                ex);
        }
    }

    /// <summary>`25-112`'s own capturing logger - deliberately the minimum this test needs (one
    /// service, one logger instance, no categories to distinguish) rather than the fuller
    /// <c>ILoggerProvider</c>-based capture <c>TelemetryLeakGuardTests</c> uses for a real DI-built
    /// host. A <see cref="ConcurrentQueue{T}"/> because <see cref="WidgetActivityFlusherService"/>'s
    /// own final-flush catch can run its continuation on a different thread than the one that called
    /// <c>StopAsync</c>, depending on how the cancellation callback is scheduled.</summary>
    private sealed class CapturingLogger : ILogger<WidgetActivityFlusherService>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" ({exception})")}");
    }

    private static async Task<SiteId> SeedSiteAsync(SiteCachingFixture fixture, string allowedOrigin = "https://tenant.example")
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [allowedOrigin]));
        await db.SaveChangesAsync();
        return siteId;
    }
}
