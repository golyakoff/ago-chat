using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `11-01`'s Done-when: `UpdateWidgetConfigHandler` persists the change and writes a
/// `SiteSettingsChanged` outbox row in the same transaction (`CloseConversationOutboxTests`' own
/// "same `DbContext` instance the real handler and repository would share within one DI scope" proof
/// shape - `adr/0005`), `GetWidgetConfigHandler` returns the current values, and an operator without
/// `site:configure` gets a clean `Forbidden` from both - the same `IPermissionChecker` mechanism, and
/// the same real-Postgres-not-fakes bar, every other permission-gated handler test in this suite uses.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UpdateWidgetConfigOutboxTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task UpdateWidgetConfig_WhenPermitted_PersistsTheChangeAndWritesOneMatchingOutboxRow()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync(Permission.SiteConfigure);

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new UpdateWidgetConfigHandler(
                new SiteRepository(db), new PermissionChecker(db), new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(), new SystemClock());

            var result = await handler.HandleAsync(
                new UpdateWidgetConfig(
                    siteId, operatorId, "#336699", nameof(Position.BottomLeft), nameof(Locale.Ru),
                    "We read what you send us.", "https://tenant.example/privacy"),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
            Assert.Equal("#336699", result.Value.PrimaryColorHex);
            Assert.Equal(Position.BottomLeft, result.Value.Position);
            Assert.Equal(Locale.Ru, result.Value.Locale);
            Assert.Equal("We read what you send us.", result.Value.NoticeText);
            Assert.Equal("https://tenant.example/privacy", result.Value.NoticeUrl);
        }

        await using var verify = fixture.CreateDbContext();
        var siteRow = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("#336699", siteRow.WidgetConfig.PrimaryColorHex);
        Assert.Equal(Position.BottomLeft, siteRow.WidgetConfig.Position);
        Assert.Equal(Locale.Ru, siteRow.Locale);
        // `16-04`: proves the two new columns (widget_notice_text, widget_notice_url) round-trip
        // through the real EF mapping against a real Postgres, not just through an in-memory fake.
        Assert.Equal("We read what you send us.", siteRow.WidgetConfig.NoticeText);
        Assert.Equal("https://tenant.example/privacy", siteRow.WidgetConfig.NoticeUrl);

        // `11-10`: two rows now, not one - UpdateWidgetConfigHandler enqueues one SiteSettingsChanged
        // envelope per Site method it calls (UpdateWidgetConfig, UpdateLocale), same transaction, same
        // partition key. Filtered by this site's own id, same "shared, untruncated fixture" reasoning
        // CloseConversationOutboxTests already states for its own outbox-row assertion.
        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.PartitionKey == siteId.Value.ToString())
            .ToListAsync(CancellationToken.None);
        Assert.Equal(2, outboxRows.Count);
        Assert.All(outboxRows, row =>
        {
            Assert.Equal(nameof(SiteSettingsChanged), row.Type);
            Assert.Null(row.PublishedAt);
        });
    }

    [Fact]
    public async Task GetWidgetConfig_WhenPermitted_ReturnsTheCurrentValues()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync(Permission.SiteConfigure);

        await using (var db = fixture.CreateDbContext())
        {
            var updateHandler = new UpdateWidgetConfigHandler(
                new SiteRepository(db), new PermissionChecker(db), new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(), new SystemClock());
            var updated = await updateHandler.HandleAsync(
                new UpdateWidgetConfig(
                    siteId, operatorId, "#abcdef", nameof(Position.BottomRight), nameof(Locale.Ru),
                    "We read what you send us.", "https://tenant.example/privacy"),
                CancellationToken.None);
            Assert.True(updated.IsSuccess);
        }

        await using var db2 = fixture.CreateDbContext();
        var getHandler = new GetWidgetConfigHandler(new SiteRepository(db2), new PermissionChecker(db2));

        var result = await getHandler.HandleAsync(new GetWidgetConfig(siteId, operatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("#abcdef", result.Value.PrimaryColorHex);
        Assert.Equal(Position.BottomRight, result.Value.Position);
        Assert.Equal(Locale.Ru, result.Value.Locale);
        Assert.Equal("We read what you send us.", result.Value.NoticeText);
        Assert.Equal("https://tenant.example/privacy", result.Value.NoticeUrl);
    }

    [Fact]
    public async Task UpdateWidgetConfig_WithoutSiteConfigure_ReturnsForbidden_AndWritesNoOutboxRow()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync(Permission.ConversationRead);

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new UpdateWidgetConfigHandler(
                new SiteRepository(db), new PermissionChecker(db), new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(), new SystemClock());

            var result = await handler.HandleAsync(
                new UpdateWidgetConfig(
                    siteId, operatorId, "#336699", nameof(Position.BottomLeft), nameof(Locale.Ru),
                    "We read what you send us.", "https://tenant.example/privacy"),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        }

        await using var verify = fixture.CreateDbContext();
        var siteRow = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Null(siteRow.WidgetConfig.PrimaryColorHex); // untouched - WidgetConfig.Default
        Assert.Null(siteRow.WidgetConfig.NoticeText); // untouched - WidgetConfig.Default
        Assert.Null(siteRow.WidgetConfig.NoticeUrl); // untouched - WidgetConfig.Default
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(o => o.PartitionKey == siteId.Value.ToString(), CancellationToken.None));
    }

    [Fact]
    public async Task GetWidgetConfig_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync(Permission.ConversationRead);

        await using var db = fixture.CreateDbContext();
        var handler = new GetWidgetConfigHandler(new SiteRepository(db), new PermissionChecker(db));

        var result = await handler.HandleAsync(new GetWidgetConfig(siteId, operatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteAndOperatorAsync(Permission grantedPermission)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        seed.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [grantedPermission.Value],
        });
        seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await seed.SaveChangesAsync(CancellationToken.None);

        return (siteId, operatorId);
    }
}
