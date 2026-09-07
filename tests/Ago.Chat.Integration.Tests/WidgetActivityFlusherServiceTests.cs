using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
/// the identical shape for <c>BatchFlusherService</c>) is what this file restores: <see
/// cref="BackgroundService.StopAsync"/>'s own implementation is <c>Task.WhenAny(_executeTask, &lt;the
/// token it was given&gt;)</c> - a *bounded* token can win that race and let <c>StopAsync</c> return
/// before the final flush inside <c>_executeTask</c> has actually finished writing, and a busier
/// machine (the full project's container fleet, not a filtered dozen tests) makes that race wider, not
/// narrower. <see cref="CancellationToken.None"/> never fires, so it cannot win that race - the read
/// below can only run after the final flush has completed, not merely started.</para>
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
        var service = new WidgetActivityFlusherService(
            accumulator, writer, options, NullLogger<WidgetActivityFlusherService>.Instance);

        await service.StartAsync(CancellationToken.None);
        accumulator.RecordLoad(siteId, Now);
        accumulator.RecordOpen(siteId, Now);

        // CancellationToken.None - see this class's own remarks on why any bounded token here
        // reintroduces the exact race that made the dropped predecessor of this test flaky.
        await service.StopAsync(CancellationToken.None);

        var readStore = new WidgetActivityReadStore(fixture.DataSource);
        var totals = await readStore.GetTotalsAsync(siteId, DateOnly.FromDateTime(Now.UtcDateTime), CancellationToken.None);

        Assert.Equal(1, totals.Loads);
        Assert.Equal(1, totals.Opens);
        Assert.Equal(0, totals.Conversations);
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
