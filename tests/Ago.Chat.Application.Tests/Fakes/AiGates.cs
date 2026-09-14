using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `25-04`: builds a real <see cref="AiProcessingGate"/> over the real fakes, for the many existing
/// `19-01`/`19-02` tests whose subject is not the gate at all.
///
/// <para><b>The real gate, never a stub of it.</b> Those tests each assert something else (candidate
/// vocabularies, rate limits, prompt ordering), and if the gate were stubbed out for them, the gate
/// could be deleted entirely and only the handful of tests written for it would go red. Composing the
/// real thing here means every one of those existing tests would also fail if the gate stopped letting
/// a properly-enabled tenant through - which is the failure this helper's own existence would otherwise
/// have hidden.</para>
///
/// <para>The module key is a plain literal here, not configuration: `Ago.Chat.Architecture.Tests`'
/// module-key literal guard scans <c>src/</c> only, and it must - the deployment's own key has to be
/// spellable somewhere a test can set it.</para>
/// </summary>
public static class AiGates
{
    public const string ModuleKey = "ai";

    /// <summary>A gate that says yes for <paramref name="siteId"/> for every conversation created at or
    /// after <paramref name="effectiveFrom"/> (default: the beginning of time, so any conversation
    /// passes).</summary>
    public static AiProcessingGate Allowing(SiteId siteId, DateTimeOffset? effectiveFrom = null)
    {
        var enablements = new FakeAiAddOnEnablementRepository();
        var enablement = AiAddOnEnablement.ForSite(siteId);
        enablement.Enable(new OperatorId(Guid.NewGuid()), "ai-processing-addendum", "v1",
            effectiveFrom ?? DateTimeOffset.MinValue);
        enablements.Seed(enablement);

        var grants = new FakeModuleQuantityGrantStore();
        grants.Grants[(siteId, new ModuleKey(ModuleKey))] = 1;

        return new AiProcessingGate(enablements, grants, new AiAddOnOptions { ModuleKey = ModuleKey });
    }

    /// <summary>A gate with nothing bought and nothing enabled - every tenant's own starting state
    /// (decision 2).</summary>
    public static AiProcessingGate Denying() =>
        new(new FakeAiAddOnEnablementRepository(), new FakeModuleQuantityGrantStore(),
            new AiAddOnOptions { ModuleKey = ModuleKey });
}
