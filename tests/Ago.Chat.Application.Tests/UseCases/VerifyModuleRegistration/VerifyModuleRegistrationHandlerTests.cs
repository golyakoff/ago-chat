using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.VerifyModuleRegistration;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.VerifyModuleRegistration;

/// <summary>`22-11`'s own fourth Done-when: "the two sides cannot silently disagree: a registration
/// that exists on one side only is detectable." Proven here at the Application level with a fake
/// gateway scripted to disagree with Chat's own row - the real HTTP round trip against a real module
/// server lives in <c>ago-calendar</c>'s/<c>ago-faq</c>'s own integration suites, which is the only
/// place a real "module-only" row can exist to be detected against, since no single test can span two
/// repositories with no shared reference between them.</summary>
public class VerifyModuleRegistrationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private const string ValidProvisioningSecret = "a-provisioning-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        VerifyModuleRegistrationHandler Handler, FakeEnabledModuleRepository Modules, FakePermissionChecker Permissions,
        FakeModuleRegistrationGateway RegistrationGateway);

    private static Fixture CreateFixture(bool permitted = true)
    {
        var modules = new FakeEnabledModuleRepository();
        var permissions = new FakePermissionChecker();
        var registrationGateway = new FakeModuleRegistrationGateway();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var handler = new VerifyModuleRegistrationHandler(modules, permissions, registrationGateway);
        return new Fixture(handler, modules, permissions, registrationGateway);
    }

    private static Application.UseCases.VerifyModuleRegistration.VerifyModuleRegistration Command() =>
        new(OperatorId, SiteId, Calendar.Value, EntryPoint.ToString(), ValidProvisioningSecret);

    [Fact]
    public async Task HandleAsync_WhenBothSidesHaveARow_ReportsAgree()
    {
        var fixture = CreateFixture();
        await fixture.Modules.SaveAsync(
            new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint,
                new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), Now),
            CancellationToken.None);
        fixture.RegistrationGateway.StatusToReturn = new ModuleRegistrationRemoteStatus(Exists: true, Now, false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ChatHasRegistration);
        Assert.True(result.Value.ModuleHasRegistration);
        Assert.True(result.Value.Agree);
    }

    /// <summary>The item's own sharpest claim: chat has a row, the module does not (or vice versa) -
    /// exactly the drift a two-sided write without a distributed transaction can leave behind, and
    /// exactly what this check exists to surface rather than hide.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnlyChatHasARow_ReportsDisagree()
    {
        var fixture = CreateFixture();
        await fixture.Modules.SaveAsync(
            new EnabledModule(
                new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint,
                new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), Now),
            CancellationToken.None);
        fixture.RegistrationGateway.StatusToReturn = new ModuleRegistrationRemoteStatus(Exists: false, null, false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ChatHasRegistration);
        Assert.False(result.Value.ModuleHasRegistration);
        Assert.False(result.Value.Agree);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyTheModuleHasARow_ReportsDisagree()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.StatusToReturn = new ModuleRegistrationRemoteStatus(Exists: true, Now, false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.ChatHasRegistration);
        Assert.True(result.Value.ModuleHasRegistration);
        Assert.False(result.Value.Agree);
    }

    [Fact]
    public async Task HandleAsync_WhenNeitherSideHasARow_ReportsAgree()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.StatusToReturn = new ModuleRegistrationRemoteStatus(Exists: false, null, false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.ChatHasRegistration);
        Assert.False(result.Value.ModuleHasRegistration);
        Assert.True(result.Value.Agree);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheModuleIsUnreachable_ReturnsModuleRegistrationFailed()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.UnreachableOnGetStatus = true;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
    }
}
