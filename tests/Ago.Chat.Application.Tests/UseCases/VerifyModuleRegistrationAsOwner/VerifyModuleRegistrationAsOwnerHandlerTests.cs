using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.VerifyModuleRegistrationAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.VerifyModuleRegistrationAsOwner;

/// <summary>
/// `23-83`/`adr/0151`: the platform owner's own half of `22-11`'s fourth Done-when - the identical
/// behaviour <c>VerifyModuleRegistrationHandlerTests</c> proved for the deleted tenant handler, minus
/// the permission check that handler had and plus the "provisioning secret not configured" case.
/// </summary>
public class VerifyModuleRegistrationAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");

    private sealed record Fixture(
        VerifyModuleRegistrationAsOwnerHandler Handler, FakeEnabledModuleRepository Modules,
        FakeModuleRegistrationGateway RegistrationGateway, FakeModuleProvisioningSecretProvider ProvisioningSecrets);

    private static Fixture CreateFixture()
    {
        var modules = new FakeEnabledModuleRepository();
        var registrationGateway = new FakeModuleRegistrationGateway();
        var provisioningSecrets = new FakeModuleProvisioningSecretProvider();

        var handler = new VerifyModuleRegistrationAsOwnerHandler(modules, registrationGateway, provisioningSecrets);
        return new Fixture(handler, modules, registrationGateway, provisioningSecrets);
    }

    private static Application.UseCases.VerifyModuleRegistrationAsOwner.VerifyModuleRegistrationAsOwner Command() =>
        new(SiteId, Calendar.Value, EntryPoint.ToString());

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

    /// <summary>The item's own sharpest claim: chat has a row, the module does not - exactly the
    /// drift a two-sided write without a distributed transaction can leave behind.</summary>
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
    public async Task HandleAsync_WhenTheModuleIsUnreachable_ReturnsModuleRegistrationFailed()
    {
        var fixture = CreateFixture();
        fixture.RegistrationGateway.UnreachableOnGetStatus = true;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.RegistrationFailed", result.Error!.Value.Code);
    }

    /// <summary>`adr/0150`'s own deployment-state case, extended to this second owner caller: no
    /// configured secret means no gateway call at all, chat-side or module-side.</summary>
    [Fact]
    public async Task HandleAsync_WhenNoProvisioningSecretIsConfigured_ReturnsModuleProvisioningNotConfigured()
    {
        var fixture = CreateFixture();
        fixture.ProvisioningSecrets.Secret = null;

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.ProvisioningNotConfigured", result.Error!.Value.Code);
        Assert.Empty(fixture.RegistrationGateway.RegisterCalls);
    }
}
