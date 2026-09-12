using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-73`: <see cref="InactivityWatchdogJob"/> end to end against a real Postgres - both passes
/// (warn, then request `22-30`'s own real erasure mechanism), and the grace-period case that must
/// trigger neither. <see cref="SiteActivityWatchdogTests"/> covers the two reset hooks that feed this
/// job's own input column; this file covers only what the job itself does once that column holds a
/// given value.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InactivityWatchdogJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan InactivityWindow = TimeSpan.FromDays(90);
    private static readonly TimeSpan WarningLeadTime = TimeSpan.FromDays(15);

    [Fact]
    public async Task RunOnceAsync_ASiteInsideItsWarningWindow_IsWarnedInBothLanguagesWithCorrectPlaceholders_AndMarkedWarned()
    {
        // One day past the warn threshold (InactivityWindow - WarningLeadTime ago), comfortably short
        // of the full InactivityWindow - a pure warning candidate, not also an erasure one.
        var lastActivity = Now - (InactivityWindow - WarningLeadTime) - TimeSpan.FromDays(1);
        var siteId = await SeedSiteAsync("Acme Support", lastActivity);
        await SeedOperatorEmailAsync(siteId, "owner@example.com");

        var mailSender = new FakeNotificationMailSender();
        await CreateJob(mailSender).RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(mailSender.Sent);
        Assert.Equal("owner@example.com", sent.To);
        Assert.Contains("Acme Support", sent.Subject);
        Assert.Contains("Acme Support", sent.Body);
        // Both languages, in the one mail - the drafted template's own RU block and EN block, verbatim.
        Assert.Contains("Здравствуйте", sent.Body);
        Assert.Contains("Hello,", sent.Body);
        Assert.Contains("Офис", sent.Body);
        Assert.Contains("Office console", sent.Body);
        // The expected deletion date is lastActivity + InactivityWindow; daysRemaining is that minus Now,
        // rounded up - WarningLeadTime minus the one day already elapsed since the threshold, i.e. 14.
        Assert.Contains("14", sent.Subject);

        await using var verify = fixture.CreateDbContext();
        var warnedAt = await ReadColumnAsync(verify, siteId, "InactivityWarningSentAt");
        Assert.NotNull(warnedAt);
        var erasureRequestedAt = await ReadColumnAsync(verify, siteId, "ErasureRequestedAt");
        Assert.Null(erasureRequestedAt);
    }

    [Fact]
    public async Task RunOnceAsync_ASiteAlreadyWarnedThisCycle_IsNotWarnedAgain()
    {
        var lastActivity = Now - (InactivityWindow - WarningLeadTime) - TimeSpan.FromDays(1);
        var siteId = await SeedSiteAsync("Already Warned Shop", lastActivity);
        await SeedOperatorEmailAsync(siteId, "owner@example.com");
        await MarkWarnedDirectlyAsync(siteId, Now - TimeSpan.FromDays(1));

        var mailSender = new FakeNotificationMailSender();
        await CreateJob(mailSender).RunOnceAsync(CancellationToken.None);

        Assert.Empty(mailSender.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_ASiteFullyPastTheWindow_RequestsErasureThroughTheRealMechanism_NotTheStaleDemoExpiryJob()
    {
        var lastActivity = Now - InactivityWindow - TimeSpan.FromDays(1);
        var siteId = await SeedSiteAsync("Long Gone Shop", lastActivity);

        await CreateJob(new FakeNotificationMailSender()).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var erasureRequestedAt = await ReadColumnAsync(verify, siteId, "ErasureRequestedAt");
        Assert.NotNull(erasureRequestedAt);
        var erasureRequestedBy = await ReadGuidColumnAsync(verify, siteId, "ErasureRequestedBy");
        // OperatorId.System's own sentinel - proves this went through IErasureRequestRepository (22-30's
        // real mechanism), the same row SiteErasureJob's own sweep later drains, never a bespoke delete.
        Assert.Equal(Guid.Empty, erasureRequestedBy);
    }

    /// <summary>
    /// `23-73`'s own explicit rule, at the job level this time (<see cref="SiteActivityWatchdogTests"/>
    /// proves the reset hooks themselves never fire for visitor traffic): a site whose watchdog was
    /// never set by either hook - the "operator never once replied, never once signed in" case - still
    /// falls back to its own <see cref="Site.CreatedAt"/> (<see cref="InactivityWatchdogQuery"/>'s own
    /// remarks) and is swept for erasure once that creation time is old enough, exactly as if an
    /// operator had once acted and then gone silent.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_ASiteNeverTouchedByEitherHook_IsStillEventuallySweptForErasure_ViaItsOwnCreatedAt()
    {
        var siteId = await SeedSiteAsync("Inbound Only Shop", lastOperatorActivityAt: null, createdAt: Now - InactivityWindow - TimeSpan.FromDays(1));

        await CreateJob(new FakeNotificationMailSender()).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var erasureRequestedAt = await ReadColumnAsync(verify, siteId, "ErasureRequestedAt");
        Assert.NotNull(erasureRequestedAt);
    }

    [Fact]
    public async Task RunOnceAsync_ASiteInsideItsGracePeriod_IsNeitherWarnedNorSwept()
    {
        var siteId = await SeedSiteAsync("Fresh Shop", Now - TimeSpan.FromDays(10));
        await SeedOperatorEmailAsync(siteId, "owner@example.com");

        var mailSender = new FakeNotificationMailSender();
        await CreateJob(mailSender).RunOnceAsync(CancellationToken.None);

        Assert.Empty(mailSender.Sent);

        await using var verify = fixture.CreateDbContext();
        var warnedAt = await ReadColumnAsync(verify, siteId, "InactivityWarningSentAt");
        Assert.Null(warnedAt);
        var erasureRequestedAt = await ReadColumnAsync(verify, siteId, "ErasureRequestedAt");
        Assert.Null(erasureRequestedAt);
    }

    private InactivityWatchdogJob CreateJob(INotificationMailSender mailSender) => new(
        fixture.DataSource,
        new DirectScopeFactory(fixture),
        mailSender,
        new FixedClock(Now),
        new UuidV7Generator(),
        Options.Create(new InactivityWatchdogJobOptions
        {
            InactivityWindow = InactivityWindow,
            WarningLeadTime = WarningLeadTime,
            BatchSize = 50,
            ConsoleLoginUrl = "https://office.example.test",
        }),
        NullLogger<InactivityWatchdogJob>.Instance);

    private async Task<SiteId> SeedSiteAsync(string name, DateTimeOffset? lastOperatorActivityAt, DateTimeOffset? createdAt = null)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], name: name, createdAt: createdAt ?? Now));
            await db.SaveChangesAsync();
        }

        if (lastOperatorActivityAt is { } value)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "update sites set last_operator_activity_at = @value where id = @id", connection);
            command.Parameters.AddWithValue("value", value);
            command.Parameters.AddWithValue("id", siteId.Value);
            await command.ExecuteNonQueryAsync();
        }

        return siteId;
    }

    private async Task SeedOperatorEmailAsync(SiteId siteId, string email)
    {
        await using var db = fixture.CreateDbContext();
        var operatorEntity = new Operator(new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Offline, capacity: 5, email: email);
        db.Operators.Add(operatorEntity);
        await db.SaveChangesAsync();
    }

    private async Task MarkWarnedDirectlyAsync(SiteId siteId, DateTimeOffset at)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "update sites set inactivity_warning_sent_at = @at where id = @id", connection);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("id", siteId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DateTimeOffset?> ReadColumnAsync(AgoChatDbContext db, SiteId siteId, string shadowPropertyName) =>
        await db.Sites.Where(s => s.Id == siteId)
            .Select(s => EF.Property<DateTimeOffset?>(s, shadowPropertyName))
            .SingleAsync();

    private static async Task<Guid?> ReadGuidColumnAsync(AgoChatDbContext db, SiteId siteId, string shadowPropertyName) =>
        await db.Sites.Where(s => s.Id == siteId)
            .Select(s => EF.Property<Guid?>(s, shadowPropertyName))
            .SingleAsync();

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

    /// <summary>
    /// The job's own production shape resolves <see cref="IErasureRequestRepository"/> from a fresh
    /// <see cref="IServiceScopeFactory"/> scope per erasure candidate (<see cref="InactivityWatchdogJob"/>'s
    /// own remarks on why). This test double reproduces exactly that against `fixture`'s real Postgres,
    /// the same "one known consumer, no full ASP.NET Core container" shape
    /// <c>AutoCloseInactiveConversationsJobTests.DirectScopeFactory</c> already establishes.
    /// </summary>
    private sealed class DirectScopeFactory(PostgresFixture fixture) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DirectScope(new ErasureRequestRepository(fixture.DataSource));

        private sealed class DirectScope(IErasureRequestRepository repository) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new SingleServiceProvider(repository);

            public void Dispose()
            {
            }
        }

        private sealed class SingleServiceProvider(object service) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
        }
    }
}
