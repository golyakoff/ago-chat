using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Application.UseCases.RequestSiteErasure;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-30`: this item's own central claim, on AGO Chat's own side of the boundary - "the second half
/// is proved rather than assumed." Every piece here is real, the identical "real classes throughout,
/// one hand-built stand-in for the process this repository cannot run" shape
/// `Ago.Calendar.Integration.Tests.ModuleQuantityGrantedWireTests`'s own class remarks establish for
/// the sibling repository: a real Postgres (<see cref="ErasureFixture"/>), the real
/// <see cref="SiteErasureJob"/>, the real <see cref="EnabledModuleReadStore"/> and the real
/// <see cref="HttpModuleRegistrationGateway"/> - and, standing in for the one process this repository
/// genuinely cannot run, a hand-built Kestrel host answering the erase-tenant-data wire contract
/// exactly as `Ago.Calendar.Api.ChatModule.ModuleRegistrationEndpoints.HandleEraseAsync` would.
///
/// <para><b>What this suite is not.</b> It does not prove the calendar's own erasure is correct -
/// `TenantErasureEndpointTests` in `ago-calendar` proves that, against the calendar's own real
/// Postgres. This suite proves the other half: that <see cref="SiteErasureJob"/> genuinely waits for a
/// module's confirmation before deleting the site row, genuinely reaches a revoked or lapsed module
/// exactly as readily as an active one, and genuinely marks the receipt <c>Failed</c>, naming the
/// module, once a stated window has passed with no confirmation.</para>
/// </summary>
[Collection(ErasureCollection.Name)]
public sealed class SiteErasureModuleGateIntegrationTests(ErasureFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly ModuleKey Calendar = new("calendar");

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>
    /// The item's own second and third Done-when, in one test: while the module has not confirmed, the
    /// site row stands - proven directly against Postgres, not inferred from the job's return value -
    /// and once it does confirm, the site (and, with it, every table `SiteErasureIntegrationTests`
    /// already proves) is erased. The module server answers "not confirmed" for the first few sweeps,
    /// then flips to confirmed, the same "module deployment briefly behind" shape a real one would
    /// show.
    /// </summary>
    [Fact]
    public async Task SiteErasure_WaitsForTheRealModuleToConfirm_BeforeDeletingTheSiteRow()
    {
        await using var moduleServer = new FakeCalendarEraseServer();
        await moduleServer.StartAsync();
        moduleServer.Confirmed = false;

        var clock = new SettableClock(Now);
        var siteId = await SeedSiteWithModuleAsync(moduleServer.BaseAddress, revoked: false, expired: false);
        await RequestErasureAsync(siteId, clock);

        var (conversationJob, siteJob) = CreateJobs(clock);

        // Several sweeps while the module has not confirmed: the site must not move.
        for (var i = 0; i < 3; i++)
        {
            await conversationJob.SweepAsync(CancellationToken.None);
            await siteJob.SweepAsync(CancellationToken.None);
        }

        Assert.True(moduleServer.ReceivedRequests.Count > 0);
        Assert.Equal(1, await CountAsync("sites", siteId.Value));
        Assert.Equal(1, await CountAsync("enabled_modules", siteId.Value, column: "site_id"));

        // The module server, unblocking - the identical eventual-recovery a redeployed module would
        // produce for a genuinely transient outage.
        moduleServer.Confirmed = true;

        await conversationJob.SweepAsync(CancellationToken.None);
        await siteJob.SweepAsync(CancellationToken.None);

        Assert.Equal(0, await CountAsync("sites", siteId.Value));
        Assert.Equal(0, await CountAsync("enabled_modules", siteId.Value, column: "site_id"));
    }

    /// <summary>The item's own fourth Done-when, the revoked half: a module `RevokeModuleForSiteAsOwnerHandler`
    /// stamped rather than deleted (`EnabledModule.RevokedAt`'s own remarks) is still reached by this
    /// gate - <see cref="IEnabledModuleReadStore.GetAllForSiteAsync"/>'s own unfiltered read includes
    /// it, and this job asks it for erasure exactly as it would an active one.</summary>
    [Fact]
    public async Task SiteErasure_StillReachesARevokedModule_AndErasesOnceItConfirms()
    {
        await using var moduleServer = new FakeCalendarEraseServer();
        await moduleServer.StartAsync();
        moduleServer.Confirmed = true;

        var clock = new SettableClock(Now);
        var siteId = await SeedSiteWithModuleAsync(moduleServer.BaseAddress, revoked: true, expired: false);
        await RequestErasureAsync(siteId, clock);

        var (conversationJob, siteJob) = CreateJobs(clock);
        await conversationJob.SweepAsync(CancellationToken.None);
        await siteJob.SweepAsync(CancellationToken.None);

        Assert.True(moduleServer.ReceivedRequests.Count > 0);
        Assert.Equal(0, await CountAsync("sites", siteId.Value));
    }

    /// <summary>The lapsed half of the same Done-when: an expired grant, which
    /// `IEnabledModuleReadStore.GetForSiteAsync`'s own hot path already excludes from routing, must
    /// still be reached here - "a lapsed grant still means the calendar holds this tenant's data"
    /// (`22-30`'s own backlog).</summary>
    [Fact]
    public async Task SiteErasure_StillReachesALapsedModule_AndErasesOnceItConfirms()
    {
        await using var moduleServer = new FakeCalendarEraseServer();
        await moduleServer.StartAsync();
        moduleServer.Confirmed = true;

        var clock = new SettableClock(Now);
        var siteId = await SeedSiteWithModuleAsync(moduleServer.BaseAddress, revoked: false, expired: true);
        await RequestErasureAsync(siteId, clock);

        var (conversationJob, siteJob) = CreateJobs(clock);
        await conversationJob.SweepAsync(CancellationToken.None);
        await siteJob.SweepAsync(CancellationToken.None);

        Assert.True(moduleServer.ReceivedRequests.Count > 0);
        Assert.Equal(0, await CountAsync("sites", siteId.Value));
    }

    /// <summary>
    /// The item's own third Done-when, word for word: "A module that does not answer leaves the
    /// receipt `Failed` with the module named, and the site row undeleted. Proven by making the module
    /// unreachable." The module server is stopped outright (connection refused, not merely a slow
    /// answer), and the clock is advanced past <see cref="SiteErasureJobOptions.ModuleUnreachableWindow"/>
    /// between requesting erasure and the sweep that should mark it <c>Failed</c>.
    /// </summary>
    [Fact]
    public async Task SiteErasure_WhenTheModuleIsUnreachablePastTheWindow_MarksTheReceiptFailed_NamingTheModule_AndLeavesTheSiteRow()
    {
        await using var moduleServer = new FakeCalendarEraseServer();
        await moduleServer.StartAsync();
        var entryPoint = moduleServer.BaseAddress;
        await moduleServer.StopAsync();

        var clock = new SettableClock(Now);
        var siteId = await SeedSiteWithModuleAsync(entryPoint, revoked: false, expired: false);
        var erasureRecordId = await RequestErasureAsync(siteId, clock);

        var options = new SiteErasureJobOptions { ModuleUnreachableWindow = TimeSpan.FromMinutes(30) };
        var (conversationJob, siteJob) = CreateJobs(clock, options);

        // Still inside the window: pending, not yet Failed.
        await conversationJob.SweepAsync(CancellationToken.None);
        await siteJob.SweepAsync(CancellationToken.None);
        Assert.Equal("Pending", await GetErasureRecordStatusAsync(erasureRecordId));
        Assert.Equal(1, await CountAsync("sites", siteId.Value));

        // Past the window - the same "not an error, a fact about elapsed time" trigger this job's
        // own EraseModulesAsync remarks describe.
        clock.UtcNow = Now.AddMinutes(31);
        await conversationJob.SweepAsync(CancellationToken.None);
        await siteJob.SweepAsync(CancellationToken.None);

        Assert.Equal("Failed", await GetErasureRecordStatusAsync(erasureRecordId));
        var reason = await GetErasureRecordFailureReasonAsync(erasureRecordId);
        Assert.Contains(Calendar.Value, reason, StringComparison.Ordinal);
        // Load-bearing ordering (`22-30`'s own Done-when #2): the site row still stands.
        Assert.Equal(1, await CountAsync("sites", siteId.Value));
        Assert.Equal(1, await CountAsync("enabled_modules", siteId.Value, column: "site_id"));

        // Not terminal (ErasureRecordStatus's own remarks): once the module comes back, the identical
        // record moves straight to Completed on a later sweep. Kestrel cannot restart a stopped
        // WebApplication, so this rebinds a fresh one to the identical address instead.
        moduleServer.RestartAt(entryPoint);
        await moduleServer.StartAsync();
        moduleServer.Confirmed = true;
        await conversationJob.SweepAsync(CancellationToken.None);
        await siteJob.SweepAsync(CancellationToken.None);

        Assert.Equal("Completed", await GetErasureRecordStatusAsync(erasureRecordId));
        Assert.Equal(0, await CountAsync("sites", siteId.Value));
    }

    // ------------------------------------------------------------------------------------------

    private (ConversationErasureJob ConversationJob, SiteErasureJob SiteJob) CreateJobs(
        IClock clock, SiteErasureJobOptions? options = null)
    {
        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            fixture.FileStorage, new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        var conversationJob = new ConversationErasureJob(
            fixture.DataSource, fixture.FileStorage, archiveEraser, clock,
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);

        var provisioner = new KeycloakDemoIdentityProvisioner(
            new HttpClient(),
            new KeycloakAdminOptions
            {
                BaseUrl = fixture.KeycloakBaseUrl,
                Realm = ErasureFixture.RealmName,
                ClientId = ErasureFixture.ProvisionerClientId,
                ClientSecret = ErasureFixture.ProvisionerClientSecret,
            },
            clock,
            NullLogger<KeycloakDemoIdentityProvisioner>.Instance);

        var recordingPublisher = new RecordingEventPublisher();
        var cacheInvalidation = new CacheInvalidationPublisher(recordingPublisher, clock);

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        services.AddScoped<IModuleRegistrationGateway>(_ => new HttpModuleRegistrationGateway(new HttpClient()));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var siteJob = new SiteErasureJob(
            fixture.DataSource, provisioner, fixture.FileStorage, new MessageArchiveRepository(fixture.DataSource),
            cacheInvalidation, new UuidV7Generator(), clock, scopeFactory,
            new FixedModuleProvisioningSecretProvider(),
            Options.Create(options ?? new SiteErasureJobOptions()), NullLogger<SiteErasureJob>.Instance);

        return (conversationJob, siteJob);
    }

    private async Task<SiteId> SeedSiteWithModuleAsync(Uri moduleEntryPoint, bool revoked, bool expired)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, publicKey, ["https://shop.example"], "Erasure Module Gate Site"));
        await db.SaveChangesAsync();

        var enabledAt = Now.AddDays(-10);
        var module = new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), siteId, Calendar, ["/booking"], moduleEntryPoint,
            new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), enabledAt,
            grantedByOwner: true, expiresAt: expired ? Now.AddDays(-1) : null);

        if (revoked)
        {
            module = module.Revoke(Now.AddDays(-2));
        }

        db.EnabledModules.Add(module);
        await db.SaveChangesAsync();

        return siteId;
    }

    private async Task<Guid?> RequestErasureAsync(SiteId siteId, IClock clock)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: null));
            var roleId = Guid.NewGuid();
            db.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteErase.Value],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        var erasureRequests = new ErasureRequestRepository(fixture.DataSource);
        await using var permissionDb = fixture.CreateDbContext();
        var requestHandler = new RequestSiteErasureHandler(
            erasureRequests, new PermissionChecker(permissionDb), new UuidV7Generator(), clock);
        var requested = await requestHandler.HandleAsync(new RequestSiteErasure(siteId, operatorId), CancellationToken.None);
        Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid?>(
            "select erasure_record_id from sites where id = @siteId", new { siteId = siteId.Value });
    }

    private async Task<int> CountAsync(string table, Guid id, string column = "id")
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"select count(*) from {table} where {column} = @id", new { id });
    }

    private async Task<string> GetErasureRecordStatusAsync(Guid? recordId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            "select status from erasure_records where id = @id", new { id = recordId }) ?? string.Empty;
    }

    private async Task<string> GetErasureRecordFailureReasonAsync(Guid? recordId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            "select failure_reason from erasure_records where id = @id", new { id = recordId }) ?? string.Empty;
    }

    private sealed class FixedModuleProvisioningSecretProvider : IModuleProvisioningSecretProvider
    {
        public ModuleProvisioningSecret? TryGet() => new("a-provisioning-secret-of-sixteen-plus-chars");
    }

    /// <summary>A minimal Kestrel host answering only the erase-tenant-data route - the identical
    /// "not the real product's own routes" fake `ModuleRegistrationGatewayIntegrationTests.FakeModuleRegistrationServer`
    /// already establishes for the gateway-level suite, narrowed to the one route this suite needs and
    /// scriptable to answer "not confirmed" before flipping to "confirmed".</summary>
    private sealed class FakeCalendarEraseServer : IAsyncDisposable
    {
        private WebApplication? _app;
        private Uri? _fixedAddress;

        public Uri BaseAddress { get; private set; } = null!;

        public bool Confirmed { get; set; } = true;

        public List<string> ReceivedRequests { get; } = [];

        /// <summary>Stand-in for restarting on the same address - Kestrel does not let a stopped
        /// `WebApplication` be started again, so a "the module comes back" scenario needs a fresh
        /// instance; this records the address so a caller can rebind to it.</summary>
        public void RestartAt(Uri address) => _fixedAddress = address;

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(_fixedAddress?.ToString() ?? "http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.MapDelete("/api/v1/module-registrations/{siteId}/tenant-data", async context =>
            {
                lock (ReceivedRequests)
                {
                    ReceivedRequests.Add(context.Request.Path);
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    Confirmed ? """{"tenantExisted":true,"confirmed":true}""" : """{"tenantExisted":true,"confirmed":false}""");
            });

            await app.StartAsync();
            _app = app;

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            BaseAddress = _fixedAddress ?? new Uri(addresses.Addresses.First());
        }

        public async Task StopAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
