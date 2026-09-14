using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AiAddOn;

/// <summary>`25-04`: the gate's four answers, each for its own reason. These are the rules both AI call
/// sites inherit without restating, so they are tested once, here, at the level they live.</summary>
public sealed class AiProcessingGateTests
{
    private static readonly SiteId Site = new(Guid.NewGuid());
    private static readonly OperatorId Actor = new(Guid.NewGuid());
    private static readonly DateTimeOffset EnabledAt = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Decision 2: every tenant, until they act.</summary>
    [Fact]
    public async Task ASiteThatHasDoneNothing_IsRefusedAsNotPurchased()
    {
        var gate = Build(purchased: false, enabled: false);

        Assert.Equal(AiProcessingDecision.NotPurchased, await gate.EvaluateAsync(Site, EnabledAt, CancellationToken.None));
    }

    [Fact]
    public async Task ASiteThatBoughtButNeverEnabled_IsRefusedAsNotEnabled()
    {
        var gate = Build(purchased: true, enabled: false);

        Assert.Equal(AiProcessingDecision.NotEnabled, await gate.EvaluateAsync(Site, EnabledAt, CancellationToken.None));
    }

    /// <summary>Decision 6, at the gate: the archive is not re-processed.</summary>
    [Fact]
    public async Task AConversationCreatedBeforeTheCutOff_IsRefused()
    {
        var gate = Build(purchased: true, enabled: true);

        Assert.Equal(
            AiProcessingDecision.BeforeCutOff,
            await gate.EvaluateAsync(Site, EnabledAt.AddTicks(-1), CancellationToken.None));
        Assert.Equal(
            AiProcessingDecision.Allowed,
            await gate.EvaluateAsync(Site, EnabledAt, CancellationToken.None));
    }

    /// <summary>The entitlement is re-read on every call rather than captured at enable time, so a
    /// lapsed subscription stops transmission even for a site that is still switched on.</summary>
    [Fact]
    public async Task AnEnabledSiteWhoseGrantWentToZero_IsRefusedAsNotPurchased()
    {
        var gate = Build(purchased: false, enabled: true);

        Assert.Equal(
            AiProcessingDecision.NotPurchased,
            await gate.EvaluateAsync(Site, EnabledAt.AddDays(1), CancellationToken.None));
    }

    /// <summary>A deployment that has declared no module key cannot have sold the add-on to anybody -
    /// refusing is the only safe reading of a blank.</summary>
    [Fact]
    public async Task ADeploymentWithNoModuleKeyConfigured_RefusesEverything()
    {
        var enablements = new FakeAiAddOnEnablementRepository();
        var enablement = AiAddOnEnablement.ForSite(Site);
        enablement.Enable(Actor, AiAddOnAgreement.DocumentKey, "v1", EnabledAt);
        enablements.Seed(enablement);
        var grants = new FakeModuleQuantityGrantStore();
        grants.Grants[(Site, new ModuleKey(AiGates.ModuleKey))] = 1;

        var gate = new AiProcessingGate(enablements, grants, new AiAddOnOptions { ModuleKey = "  " });

        Assert.Equal(
            AiProcessingDecision.NotPurchased,
            await gate.EvaluateAsync(Site, EnabledAt.AddDays(1), CancellationToken.None));
    }

    private static AiProcessingGate Build(bool purchased, bool enabled)
    {
        var enablements = new FakeAiAddOnEnablementRepository();
        if (enabled)
        {
            var enablement = AiAddOnEnablement.ForSite(Site);
            enablement.Enable(Actor, AiAddOnAgreement.DocumentKey, "v1", EnabledAt);
            enablements.Seed(enablement);
        }

        var grants = new FakeModuleQuantityGrantStore();
        if (purchased)
        {
            grants.Grants[(Site, new ModuleKey(AiGates.ModuleKey))] = 1;
        }

        return new AiProcessingGate(enablements, grants, new AiAddOnOptions { ModuleKey = AiGates.ModuleKey });
    }
}
