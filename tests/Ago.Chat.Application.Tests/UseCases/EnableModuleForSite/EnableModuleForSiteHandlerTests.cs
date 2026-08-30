using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.EnableModuleForSite;

/// <summary>
/// `20-07`'s own Done-when: "rejects at registration time if any trigger word case-insensitively
/// overlaps another *enabled* module on the same site... a real test proving rejection (not
/// first-match-wins)."
/// </summary>
public class EnableModuleForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Fixture(
        EnableModuleForSiteHandler Handler, FakeEnabledModuleRepository Modules, FakeEnabledModuleReadStore ReadStore,
        FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool permitted = true)
    {
        var modules = new FakeEnabledModuleRepository();
        var readStore = new FakeEnabledModuleReadStore();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var handler = new EnableModuleForSiteHandler(modules, readStore, permissions, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(handler, modules, readStore, permissions);
    }

    private static Application.UseCases.EnableModuleForSite.EnableModuleForSite Command(
        string moduleKey, params string[] triggerWords) =>
        new(OperatorId, SiteId, moduleKey, triggerWords, "https://module.example.com");

    [Fact]
    public async Task HandleAsync_WithNoConflict_RegistersTheModule()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("calendar", "/booking"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.Modules.All);
        Assert.Equal(new ModuleKey("calendar"), saved.ModuleKey);
        Assert.Equal(["/booking"], saved.TriggerWords);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden_AndRegistersNothing()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Command("calendar", "/booking"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    /// <summary>The exact Done-when case: a second module registering a trigger word an already-
    /// enabled module owns is rejected outright, not silently accepted to be resolved by
    /// first-match-wins at routing time.</summary>
    [Fact]
    public async Task HandleAsync_WhenATriggerWordAlreadyBelongsToAnotherEnabledModule_IsRejected()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(SiteId, new EnabledModuleSummary(
            new ModuleKey("calendar"), ["/booking"], new Uri("https://calendar.example.com")));

        var result = await fixture.Handler.HandleAsync(Command("taxi", "/booking"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordAlreadyRegistered", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    [Fact]
    public async Task HandleAsync_TheConflictCheckIsCaseInsensitive()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(SiteId, new EnabledModuleSummary(
            new ModuleKey("calendar"), ["/BOOKING"], new Uri("https://calendar.example.com")));

        var result = await fixture.Handler.HandleAsync(Command("taxi", "/booking"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordAlreadyRegistered", result.Error!.Value.Code);
    }

    /// <summary>Checked against *every* other enabled module on the site, not only the most recently
    /// registered one - the item's own "not first-match-wins" instruction, proven with three modules
    /// where only the third conflicts.</summary>
    [Fact]
    public async Task HandleAsync_ChecksEveryExistingModule_NotJustTheFirstOne()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(SiteId, new EnabledModuleSummary(new ModuleKey("first"), ["/first"], new Uri("https://a.example.com")));
        fixture.ReadStore.Seed(SiteId, new EnabledModuleSummary(new ModuleKey("second"), ["/second"], new Uri("https://b.example.com")));
        fixture.ReadStore.Seed(SiteId, new EnabledModuleSummary(new ModuleKey("third"), ["/third"], new Uri("https://c.example.com")));

        var result = await fixture.Handler.HandleAsync(Command("fourth", "/third"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordAlreadyRegistered", result.Error!.Value.Code);
        Assert.Contains("third", result.Error!.Value.Message);
    }

    [Fact]
    public async Task HandleAsync_DoesNotConflictWithItself_WhenTheSameModuleKeyReRegisters()
    {
        var fixture = CreateFixture();
        fixture.ReadStore.Seed(SiteId, new EnabledModuleSummary(
            new ModuleKey("calendar"), ["/booking"], new Uri("https://calendar.example.com")));

        var result = await fixture.Handler.HandleAsync(Command("calendar", "/booking"), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// <summary>`14-12`/`docs/conventions/text-commands.md`: a site registering a module trigger word
    /// that collides with Chat's own closed command vocabulary is refused, at registration time - the
    /// registration-time collision guard that document's own "Adding a new command" section asks every
    /// reserved word to be proven by. Checked ahead of the per-site overlap loop, so this is refused even
    /// when it is the very first module this site has ever enabled.</summary>
    [Fact]
    public async Task HandleAsync_WhenATriggerWordCollidesWithAReservedChatCommand_IsRejected()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("identity-helper", "/linkidentity"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordReserved", result.Error!.Value.Code);
        Assert.Empty(fixture.Modules.All);
    }

    [Fact]
    public async Task HandleAsync_TheReservedWordCheckIsCaseInsensitiveAndSlashTolerant()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("identity-helper", "LinkIdentity"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.TriggerWordReserved", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAnInvalidModuleKey_ReturnsModuleInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("Not Valid!", "/booking"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithANonHttpEntryPoint_ReturnsModuleInvalid()
    {
        var fixture = CreateFixture();
        var command = new Application.UseCases.EnableModuleForSite.EnableModuleForSite(
            OperatorId, SiteId, "calendar", ["/booking"], "ftp://module.example.com");

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.Invalid", result.Error!.Value.Code);
    }
}
