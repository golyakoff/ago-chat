using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-83`: <see cref="DownloadThresholdWatchdogJob"/> end to end against a real Postgres - the
/// item's own explicit demand that the soft-threshold notification "actually fires the email +
/// banner-visible state once per crossing, not once per tick", proven by running
/// <see cref="DownloadThresholdWatchdogJob.RunOnceAsync"/> twice against the identical row rather than
/// asserting it from the query's own `WHERE soft_notified_at IS NULL` clause. The identical shape
/// <see cref="InactivityWatchdogJobTests"/> already establishes for its own sibling sweep - this job
/// needs no <see cref="System.IServiceScopeFactory"/> double the way that one does
/// (<see cref="DownloadThresholdWatchdogJob"/>'s own constructor takes <see cref="NpgsqlDataSource"/>
/// directly, no per-candidate DI scope), so the fixture here is simpler.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DownloadThresholdWatchdogJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The item's own headline claim: a site whose real, maintained
    /// <c>site_attachment_egress.bytes_out</c> (the real <see cref="AttachmentEgressMeterStore"/>
    /// write, not a hand-inserted row) has reached its tier's real soft threshold is warned by mail
    /// and marked - the banner's own data source (<c>GetDownloadUsageForSiteHandler</c>) reads the
    /// live figure independently of this column, so this job's own job is only the mail and the
    /// once-per-crossing marker, not the banner's own visibility.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteAtItsSoftThreshold_IsWarnedByMail_AndMarkedNotified()
    {
        var tier = await SeedTierThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 1_000_000);
        var siteId = await SeedSiteAsync("Acme Support", tier);
        await SeedOperatorEmailAsync(siteId, "owner@example.com");
        await RecordEgressAsync(siteId, bytes: 150);

        var mailSender = new FakeNotificationMailSender();
        await CreateJob(mailSender).RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(mailSender.Sent);
        Assert.Equal("owner@example.com", sent.To);
        Assert.Contains("Acme Support", sent.Subject);
        Assert.Contains("Acme Support", sent.Body);

        var notifiedAt = await ReadSoftNotifiedAtAsync(siteId);
        Assert.NotNull(notifiedAt);
    }

    /// <summary>The once-per-crossing guarantee itself, proven by running the sweep twice against the
    /// identical row rather than by inspecting the SQL - a fresh cycle that finds the row already
    /// marked must not send a second mail, whether the second cycle is a "slow batch, same tick" replay
    /// or, as here, a genuinely later tick that finds nothing changed.</summary>
    [Fact]
    public async Task RunOnceAsync_CalledTwice_SendsOnlyOneMail_ForTheSameCrossing()
    {
        var tier = await SeedTierThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 1_000_000);
        var siteId = await SeedSiteAsync("Repeat Sweep Shop", tier);
        await SeedOperatorEmailAsync(siteId, "owner@example.com");
        await RecordEgressAsync(siteId, bytes: 150);

        var mailSender = new FakeNotificationMailSender();
        var job = CreateJob(mailSender);
        await job.RunOnceAsync(CancellationToken.None);
        await job.RunOnceAsync(CancellationToken.None);

        Assert.Single(mailSender.Sent);
    }

    /// <summary>A site whose bytes climb further after already being notified this period is not
    /// notified again either - the same once-per-`(site, period)` guard, exercised this time by a
    /// state change between cycles rather than an identical replay.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteAlreadyNotifiedThisPeriod_IsNotWarnedAgain_EvenAsBytesGrowFurther()
    {
        var tier = await SeedTierThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 1_000_000);
        var siteId = await SeedSiteAsync("Growing Shop", tier);
        await SeedOperatorEmailAsync(siteId, "owner@example.com");
        await RecordEgressAsync(siteId, bytes: 150);

        var mailSender = new FakeNotificationMailSender();
        var job = CreateJob(mailSender);
        await job.RunOnceAsync(CancellationToken.None);
        Assert.Single(mailSender.Sent);

        await RecordEgressAsync(siteId, bytes: 50);
        await job.RunOnceAsync(CancellationToken.None);

        Assert.Single(mailSender.Sent);
    }

    /// <summary>Below the soft threshold, no mail and no marker - the negative control every test
    /// above relies on to mean what it says.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteBelowItsSoftThreshold_IsNotWarned()
    {
        var tier = await SeedTierThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 1_000_000);
        var siteId = await SeedSiteAsync("Under Threshold Shop", tier);
        await SeedOperatorEmailAsync(siteId, "owner@example.com");
        await RecordEgressAsync(siteId, bytes: 99);

        var mailSender = new FakeNotificationMailSender();
        await CreateJob(mailSender).RunOnceAsync(CancellationToken.None);

        Assert.Empty(mailSender.Sent);
        Assert.Null(await ReadSoftNotifiedAtAsync(siteId));
    }

    /// <summary>A site with no operator email on file is still marked notified - `DownloadThresholdWatchdogJob`'s
    /// own remarks: retrying a site with nobody to mail forever would never succeed, and the console
    /// banner is a live read independent of this column.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteWithNoOperatorEmail_IsStillMarkedNotified_AndSendsNoMail()
    {
        var tier = await SeedTierThresholdAsync(softThresholdBytes: 100, hardThresholdBytes: 1_000_000);
        var siteId = await SeedSiteAsync("No Email On File Shop", tier);
        await RecordEgressAsync(siteId, bytes: 150);

        var mailSender = new FakeNotificationMailSender();
        await CreateJob(mailSender).RunOnceAsync(CancellationToken.None);

        Assert.Empty(mailSender.Sent);
        Assert.NotNull(await ReadSoftNotifiedAtAsync(siteId));
    }

    private DownloadThresholdWatchdogJob CreateJob(INotificationMailSender mailSender) => new(
        fixture.DataSource,
        mailSender,
        new FixedClock(Now),
        Options.Create(new DownloadThresholdWatchdogJobOptions
        {
            Interval = TimeSpan.FromMinutes(10),
            BatchSize = 50,
            ConsoleUrl = "https://office.example.test",
        }),
        NullLogger<DownloadThresholdWatchdogJob>.Instance);

    private async Task<string> SeedTierThresholdAsync(long softThresholdBytes, long hardThresholdBytes)
    {
        var tier = $"test_{Guid.NewGuid():N}";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tier_download_thresholds (tier, soft_threshold_bytes, hard_threshold_bytes, updated_at, updated_by)
            VALUES (@tier, @soft, @hard, now(), 'DownloadThresholdWatchdogJobTests')
            """,
            connection);
        command.Parameters.AddWithValue("tier", tier);
        command.Parameters.AddWithValue("soft", softThresholdBytes);
        command.Parameters.AddWithValue("hard", hardThresholdBytes);
        await command.ExecuteNonQueryAsync();
        return tier;
    }

    private async Task<SiteId> SeedSiteAsync(string name, string tier)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], name: name, tier: tier));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task SeedOperatorEmailAsync(SiteId siteId, string email)
    {
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Offline, capacity: 5, email: email));
        await db.SaveChangesAsync();
    }

    /// <summary>The real write path - the same <see cref="AttachmentEgressMeterStore"/>
    /// `GetAttachmentDownloadUrlHandler.RecordDownloadAsync` itself calls, not a hand-inserted row.
    /// Each call adds to the current-month figure, the same "the once-per-crossing guard is a database
    /// compare-and-set" shape this job's own remarks describe.</summary>
    private async Task RecordEgressAsync(SiteId siteId, long bytes) =>
        await new AttachmentEgressMeterStore(fixture.DataSource).RecordAsync(
            siteId, new DateOnly(Now.Year, Now.Month, 1), bytes, CancellationToken.None);

    /// <summary>Dapper, not a raw `NpgsqlCommand` scalar read - `site_attachment_egress` has no EF
    /// entity (`Stage23AddSiteAttachmentEgress`'s own remarks). Reads into a plain `DateTime` and
    /// wraps it with `DateTimeKind.Utc` before returning - the identical two-step
    /// <see cref="ChannelDeliveryReadStore"/>'s own remarks already establish for the same reason: this
    /// Npgsql data source hands a `timestamptz` back as `DateTime`, not `DateTimeOffset`, and Dapper
    /// throws rather than converting a boxed `DateTime` to a `DateTimeOffset`-typed generic parameter
    /// directly.</summary>
    private async Task<DateTimeOffset?> ReadSoftNotifiedAtAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var value = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            "select soft_notified_at from site_attachment_egress where site_id = @siteId and period_month = @periodMonth",
            new { siteId = siteId.Value, periodMonth = new DateOnly(Now.Year, Now.Month, 1).ToDateTime(TimeOnly.MinValue) });
        return value is { } notified ? new DateTimeOffset(DateTime.SpecifyKind(notified, DateTimeKind.Utc)) : null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeNotificationMailSender : INotificationMailSender
    {
        public List<NotificationMailMessage> Sent { get; } = [];

        public Task SendAsync(NotificationMailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
