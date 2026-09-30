using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetModuleTriggerWordsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetModuleTriggerWordsForSite;

/// <summary>
/// `26-320`'s own Done-when at the Application level: a tenant admin holding `site:configure` can replace an
/// already-enabled module's trigger words for their own site with no platform-owner action; the reserved-word
/// and cross-module-collision validation is the owner path's, applied against the other modules on the same
/// site; an owner-granted module keeps owner-managed triggers; and the call refuses without the permission
/// and never reaches a site the caller does not administer.
/// </summary>
public class SetModuleTriggerWordsForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId Admin = new(Guid.NewGuid());
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private static readonly ModuleCredential Credential = new("an-existing-secret-of-sixteen-plus");

    private sealed record Fixture(
        SetModuleTriggerWordsForSiteHandler Handler, FakePermissionChecker Permissions,
        FakeEnabledModuleRepository Modules, FakeEnabledModuleReadStore ReadStore);

    private static Fixture CreateFixture(bool granted = true, bool seedCalendar = true, bool ownerGranted = false)
    {
        var permissions = new FakePermissionChecker();
        if (granted)
        {
            permissions.Grant(Admin, SiteId, Permission.SiteConfigure);
        }

        var modules = new FakeEnabledModuleRepository();
        var readStore = new FakeEnabledModuleReadStore();

        if (seedCalendar)
        {
            var calendar = new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), SiteId, new ModuleKey("calendar"), ["/записаться"], EntryPoint,
                Credential, Now, grantedByOwner: ownerGranted);
            modules.SaveAsync(calendar, CancellationToken.None).GetAwaiter().GetResult();
            readStore.Seed(
                SiteId, new EnabledModuleSummary(
                    new ModuleKey("calendar"), ["/записаться"], EntryPoint, Credential, ownerGranted, ExpiresAt: null));
        }

        var handler = new SetModuleTriggerWordsForSiteHandler(permissions, modules, readStore, new FakeClock(Now));
        return new Fixture(handler, permissions, modules, readStore);
    }

    private static Application.UseCases.SetModuleTriggerWordsForSite.SetModuleTriggerWordsForSite Command(
        IReadOnlyList<string>? triggerWords = null, SiteId? siteId = null) =>
        new(Admin, siteId ?? SiteId, "calendar", triggerWords ?? ["/записаться", "/booking"]);

    [Fact]
    public async Task HandleAsync_WithPermission_ReplacesTheTriggerWords()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.Equal(["/записаться", "/booking"], saved.TriggerWords);
        // Every other fact about the row carries over unchanged - a re-word is not a re-grant.
        Assert.False(saved.GrantedByOwner);
        Assert.Null(saved.ExpiresAt);
        Assert.Equal(EntryPoint, saved.EntryPoint);
    }

    [Fact]
    public async Task HandleAsync_WithoutThePermission_ReturnsForbidden_AndChangesNothing()
    {
        var fixture = CreateFixture(granted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(["/записаться"], Assert.Single(fixture.Modules.All).TriggerWords);
    }

    /// <summary>"Only ever affects the caller's own site": permission is held on <see cref="SiteId"/>, but a
    /// command naming a different site is refused - the handler gates on the command's own site.</summary>
    [Fact]
    public async Task HandleAsync_ForASiteTheCallerDoesNotAdminister_ReturnsForbidden_AndChangesNothing()
    {
        var fixture = CreateFixture();
        var someoneElsesSite = new SiteId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            Command(siteId: someoneElsesSite), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(["/записаться"], Assert.Single(fixture.Modules.All).TriggerWords);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleIsNotEnabled_ReturnsModuleNotEnabled()
    {
        var fixture = CreateFixture(seedCalendar: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.NotEnabled", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    /// <summary>The mirror of `26-316`'s disable refusal: an owner-granted module keeps owner-managed
    /// triggers, so a tenant editing their own settings cannot re-point one.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheModuleIsOwnerGranted_ReturnsOwnerGrantRefused_AndChangesNothing()
    {
        var fixture = CreateFixture(ownerGranted: true);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordsOwnerGrantRefused", result.Error!.Value.Code);
        Assert.Equal(["/записаться"], Assert.Single(fixture.Modules.All).TriggerWords);
    }

    [Fact]
    public async Task HandleAsync_WhenATriggerWordIsReserved_IsRejected_AndChangesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command([$"/{ReservedChatCommands.LinkIdentity}"]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordReserved", result.Error!.Value.Code);
        Assert.Equal(["/записаться"], Assert.Single(fixture.Modules.All).TriggerWords);
    }

    [Fact]
    public async Task HandleAsync_WhenATriggerWordBelongsToAnotherEnabledModule_IsRejected_AndChangesNothing()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(
            SiteId, new EnabledModuleSummary(
                new ModuleKey("faq"), ["/faq"], new Uri("https://faq.example.com"),
                new ModuleCredential("a-faq-secret-of-sixteen-plus-chars"), GrantedByOwner: false, ExpiresAt: null));

        var result = await fixture.Handler.HandleAsync(Command(["/faq"]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordAlreadyRegistered", result.Error!.Value.Code);
        Assert.Equal(["/записаться"], Assert.Single(fixture.Modules.All).TriggerWords);
    }

    /// <summary>The aggregate's own shape invariant, surfaced as `Module.Invalid` - an empty replacement set
    /// is refused (a module needs at least one trigger word), the row untouched.</summary>
    [Fact]
    public async Task HandleAsync_WithAnEmptyList_ReturnsModuleInvalid_AndChangesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command([]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.Invalid", result.Error!.Value.Code);
        Assert.Equal(["/записаться"], Assert.Single(fixture.Modules.All).TriggerWords);
    }
}
