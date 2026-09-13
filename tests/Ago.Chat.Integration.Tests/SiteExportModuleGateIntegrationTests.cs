using System.IO.Compression;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Module.Modules;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Resilience;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-31`: this item's own central claim, on AGO Chat's own side of the boundary - "an export carries
/// both halves, or fails loudly." Every piece here is real except the one process this repository
/// cannot run, the identical "real classes throughout, one hand-built stand-in for the module
/// deployment" shape <see cref="SiteErasureModuleGateIntegrationTests"/> already establishes for its own
/// sibling item: a real Postgres and MinIO (<see cref="AttachmentFixture"/>), the real
/// <see cref="SiteExportJob"/>/<see cref="SiteExportArchiveWriter"/>, the real
/// <see cref="EnabledModuleReadStore"/> and the real <see cref="HttpModuleRegistrationGateway"/>, and a
/// hand-built Kestrel host answering the `GET .../tenant-data` wire contract this item's own recon
/// establishes for the calendar side.
///
/// <para><b>What this suite is not.</b> It does not prove the calendar's own export endpoint is correct
/// - the sibling suite in `ago-calendar` proves that, against a real Postgres tenant. This suite proves
/// the other half: that <see cref="SiteExportJob"/> genuinely embeds a reachable module's bytes and
/// names it in <c>manifest.json</c>, and genuinely fails the whole export - no archive published - the
/// moment a module the site is known to have cannot be reached.</para>
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class SiteExportModuleGateIntegrationTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly HttpClient Http = new();

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>The item's own first Done-when: a tenant with the calendar add-on gets the calendar's
    /// half embedded, and <c>manifest.json</c> names it - proven by opening the real archive, not by
    /// asserting against the job's return value.</summary>
    [Fact]
    public async Task ExportingASite_WithACalendarModule_EmbedsItsBytes_AndNamesItInTheManifest()
    {
        await using var moduleServer = new FakeCalendarExportServer();
        var payload = "customers.jsonl\nevents.jsonl\n"u8.ToArray();
        moduleServer.ExportFormatVersion = 3;
        moduleServer.ExportPayload = payload;
        await moduleServer.StartAsync();

        var clock = new SettableClock(Now);
        var siteId = await SeedSiteWithModuleAsync(moduleServer.BaseAddress);
        var operatorId = await SeedOperatorAsync(siteId);
        var exportId = await RequestExportAsync(siteId, operatorId, clock);

        var job = CreateJob(clock);
        Assert.Equal(1, await job.SweepAsync(CancellationToken.None));

        Assert.Equal("Ready", await GetExportStatusAsync(exportId));
        Assert.True(moduleServer.ReceivedRequests.Count > 0);

        var downloadUrl = await GetDownloadUrlAsync(exportId, siteId, operatorId);
        using var archiveResponse = await Http.GetAsync(downloadUrl);
        archiveResponse.EnsureSuccessStatusCode();
        using var archiveStream = new MemoryStream(await archiveResponse.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        var moduleEntry = archive.GetEntry("modules/calendar.bin");
        Assert.NotNull(moduleEntry);
        await using (var entryStream = moduleEntry!.Open())
        using (var reader = new MemoryStream())
        {
            await entryStream.CopyToAsync(reader);
            Assert.Equal(payload, reader.ToArray());
        }

        var manifest = await ReadJsonAsync(archive, "manifest.json");
        var modules = manifest.GetProperty("modules").EnumerateArray().ToList();
        var moduleRecord = Assert.Single(modules);
        Assert.Equal("calendar", moduleRecord.GetProperty("moduleKey").GetString());
        Assert.True(moduleRecord.GetProperty("included").GetBoolean());
        Assert.Equal(3, moduleRecord.GetProperty("formatVersion").GetInt32());
        Assert.Equal(payload.Length, moduleRecord.GetProperty("byteCount").GetInt64());
    }

    /// <summary>The item's own second Done-when, word for word: "An export of a tenant whose calendar
    /// cannot be reached is Failed, naming the module, and no archive is published." The module server
    /// is stopped outright (connection refused, not merely a slow answer) - genuinely unreachable, not
    /// mocked away.</summary>
    [Fact]
    public async Task ExportingASite_WhenTheCalendarModuleIsUnreachable_MarksTheExportFailed_NamingTheModule_AndPublishesNoArchive()
    {
        await using var moduleServer = new FakeCalendarExportServer();
        await moduleServer.StartAsync();
        var entryPoint = moduleServer.BaseAddress;
        await moduleServer.StopAsync();

        var clock = new SettableClock(Now);
        var siteId = await SeedSiteWithModuleAsync(entryPoint);
        var operatorId = await SeedOperatorAsync(siteId);
        var exportId = await RequestExportAsync(siteId, operatorId, clock);

        var job = CreateJob(clock);
        Assert.Equal(0, await job.SweepAsync(CancellationToken.None));

        Assert.Equal("Failed", await GetExportStatusAsync(exportId));
        var reason = await GetFailureReasonAsync(exportId);
        Assert.Contains(Calendar.Value, reason, StringComparison.Ordinal);

        await using (var permissionDb = fixture.CreateDbContext())
        {
            var statusHandler = new GetSiteExportStatusHandler(
                new ExportRequestRepository(fixture.DataSource), fixture.FileStorage,
                new PermissionChecker(permissionDb), new SiteExportOptions());
            var status = await statusHandler.HandleAsync(
                new GetSiteExportStatus(exportId, siteId, operatorId), CancellationToken.None);
            Assert.True(status.IsSuccess);
            Assert.Equal(ExportStatus.Failed, status.Value.Status);
            Assert.Null(status.Value.DownloadUrl);
        }
    }

    /// <summary>The item's own third Done-when, the other half of the same distinction: a site that has
    /// never had a calendar at all still produces a Ready export, whose own `manifest.json` records an
    /// empty `modules` array - not merely the absence of the property, an explicit empty list, which is
    /// what lets a reader tell "no calendar" (this test) apart from "calendar's half is missing" (the
    /// test right above, which never reaches a manifest at all because the whole export fails
    /// first).</summary>
    [Fact]
    public async Task ExportingASite_WithNoCalendarModuleEverEnabled_ProducesAnEmptyModulesArray()
    {
        var clock = new SettableClock(Now);
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example"], "No Calendar Site"));
            await db.SaveChangesAsync();
        }

        var operatorId = await SeedOperatorAsync(siteId);
        var exportId = await RequestExportAsync(siteId, operatorId, clock);

        var job = CreateJob(clock);
        Assert.Equal(1, await job.SweepAsync(CancellationToken.None));
        Assert.Equal("Ready", await GetExportStatusAsync(exportId));

        var downloadUrl = await GetDownloadUrlAsync(exportId, siteId, operatorId);
        using var archiveResponse = await Http.GetAsync(downloadUrl);
        archiveResponse.EnsureSuccessStatusCode();
        using var archiveStream = new MemoryStream(await archiveResponse.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        var manifest = await ReadJsonAsync(archive, "manifest.json");
        Assert.Equal(0, manifest.GetProperty("modules").GetArrayLength());
    }

    // ------------------------------------------------------------------------------------------

    private SiteExportJob CreateJob(IClock clock)
    {
        var options = new SiteExportJobOptions { AttachmentUrlLifetime = TimeSpan.FromMinutes(30) };

        var services = new ServiceCollection();
        services.AddSingleton(fixture.DataSource);
        services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        services.AddScoped<IModuleRegistrationGateway>(_ => new HttpModuleRegistrationGateway(new HttpClient()));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // Generous enough that a slow-starting Kestrel fake never trips this pipeline's own timeout by
        // accident, but this file's own tests never rely on the retry actually firing.
        var exportPipelines = new ModuleExportResiliencePipelines(new ResiliencePipelineOptions
        {
            Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(10) },
            Retry = new ResilienceRetryOptions { MaxRetryAttempts = 1, Delay = TimeSpan.FromMilliseconds(50) },
        });

        var archiveWriter = new SiteExportArchiveWriter(
            fixture.FileStorage, options, new FixedModuleProvisioningSecretProvider(), exportPipelines);
        return new SiteExportJob(
            fixture.DataSource, fixture.FileStorage, archiveWriter, clock, scopeFactory,
            Options.Create(options), NullLogger<SiteExportJob>.Instance);
    }

    private async Task<SiteId> SeedSiteWithModuleAsync(Uri moduleEntryPoint)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example"], "Export Module Gate Site"));
        await db.SaveChangesAsync();

        var module = new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), siteId, Calendar, ["/booking"], moduleEntryPoint,
            new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), Now.AddDays(-10),
            grantedByOwner: true, expiresAt: null);
        db.EnabledModules.Add(module);
        await db.SaveChangesAsync();

        return siteId;
    }

    private async Task<OperatorId> SeedOperatorAsync(SiteId siteId)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"subject-{operatorId.Value:N}"));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteExport.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
        return operatorId;
    }

    private async Task<Guid> RequestExportAsync(SiteId siteId, OperatorId operatorId, IClock clock)
    {
        var exportRequests = new ExportRequestRepository(fixture.DataSource);
        await using var permissionDb = fixture.CreateDbContext();
        var requestHandler = new RequestSiteExportHandler(
            exportRequests, new FakeRateLimiter(), new PermissionChecker(permissionDb),
            new SiteExportRateLimitOptions(), new UuidV7Generator(), clock);
        var requested = await requestHandler.HandleAsync(new RequestSiteExport(siteId, operatorId), CancellationToken.None);
        Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error!.Value.ToString() : null);
        return requested.Value;
    }

    private async Task<Uri> GetDownloadUrlAsync(Guid exportId, SiteId siteId, OperatorId operatorId)
    {
        await using var permissionDb = fixture.CreateDbContext();
        var statusHandler = new GetSiteExportStatusHandler(
            new ExportRequestRepository(fixture.DataSource), fixture.FileStorage,
            new PermissionChecker(permissionDb), new SiteExportOptions());
        var status = await statusHandler.HandleAsync(new GetSiteExportStatus(exportId, siteId, operatorId), CancellationToken.None);
        Assert.True(status.IsSuccess, status.IsFailure ? status.Error!.Value.ToString() : null);
        return status.Value.DownloadUrl!;
    }

    private async Task<string> GetExportStatusAsync(Guid exportId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            "select status from export_requests where id = @id", new { id = exportId }) ?? string.Empty;
    }

    private async Task<string> GetFailureReasonAsync(Guid exportId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            "select failure_reason from export_requests where id = @id", new { id = exportId }) ?? string.Empty;
    }

    private static async Task<JsonElement> ReadJsonAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private sealed class FixedModuleProvisioningSecretProvider : IModuleProvisioningSecretProvider
    {
        public ModuleProvisioningSecret? TryGet() => new("a-provisioning-secret-of-sixteen-plus-chars");
    }

    /// <summary>A minimal Kestrel host answering only the export route - the identical "not the real
    /// product's own routes" fake <c>SiteErasureModuleGateIntegrationTests.FakeCalendarEraseServer</c>
    /// already establishes for its own sibling suite, narrowed to `GET .../tenant-data` and the format-
    /// version header <see cref="HttpModuleRegistrationGateway.ExportTenantDataAsync"/> reads.</summary>
    private sealed class FakeCalendarExportServer : IAsyncDisposable
    {
        private WebApplication? _app;

        public Uri BaseAddress { get; private set; } = null!;

        public byte[] ExportPayload { get; set; } = [1, 2, 3];

        public int ExportFormatVersion { get; set; } = 1;

        public List<string> ReceivedRequests { get; } = [];

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.MapGet("/api/v1/module-registrations/{siteId}/tenant-data", async context =>
            {
                lock (ReceivedRequests)
                {
                    ReceivedRequests.Add(context.Request.Path);
                }

                context.Response.Headers["X-Ago-Export-Format-Version"] = ExportFormatVersion.ToString();
                context.Response.ContentType = "application/octet-stream";
                await context.Response.Body.WriteAsync(ExportPayload);
            });

            await app.StartAsync();
            _app = app;

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            BaseAddress = new Uri(addresses.Addresses.First());
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
