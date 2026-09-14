using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: the single place that answers "may this conversation's text reach an LLM vendor at all" -
/// asked by both AI paths (<c>GenerateReplyDraftHandler</c>, <c>CategorizeConversationHandler</c>)
/// before either one touches its provider port.
///
/// <para><b>An Application class, not a port.</b> It is a *policy* composed from three facts each of
/// which already has its own port (the module quantity grant, the enablement row, the deployment's own
/// module key) - `clean-architecture.md`'s own test: a port exists because an outer layer owns the
/// mechanism, and nothing about "bought AND enabled AND created after the cut-off" belongs to
/// infrastructure. The alternative - an <c>IAiProcessingGate</c> interface in Abstractions with a
/// Postgres implementation - would have put a business rule in <c>Infrastructure.Postgres</c>, where no
/// test could reach it without a database.</para>
///
/// <para><b>Three independent reasons to refuse, and all three are checked every call.</b> The
/// subscription lapsing (quantity back to zero) has to stop transmission just as hard as the tenant
/// switching it off does, which is why the entitlement is re-read rather than captured into the
/// enablement row at enable time - a snapshot there would keep sending for a tenant who stopped
/// paying.</para>
///
/// <para><b>Why this returns a reason rather than a bool.</b> The two call sites do different things
/// with a refusal: the reply-draft endpoint tells an operator *why* their button did nothing, while the
/// background sweep only counts it. A bool would have forced the first to re-derive the reason with a
/// second set of reads.</para>
/// </summary>
public sealed class AiProcessingGate(
    IAiAddOnReadStore addOns,
    IModuleQuantityGrantStore grants,
    AiAddOnOptions options)
{
    public async Task<AiProcessingDecision> EvaluateAsync(
        SiteId siteId, DateTimeOffset conversationCreatedAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ModuleKey))
        {
            // This deployment does not sell the add-on at all. Refusing is the only safe reading:
            // a deployment with no key configured cannot have granted anybody a quantity of it.
            return AiProcessingDecision.NotPurchased;
        }

        var quantity = await grants.GetQuantityAsync(siteId, new ModuleKey(options.ModuleKey), cancellationToken);
        if (quantity <= 0)
        {
            return AiProcessingDecision.NotPurchased;
        }

        var state = await addOns.GetForSiteAsync(siteId, cancellationToken);
        if (state is not { IsEnabled: true, EffectiveFrom: { } effectiveFrom })
        {
            return AiProcessingDecision.NotEnabled;
        }

        return conversationCreatedAt >= effectiveFrom
            ? AiProcessingDecision.Allowed
            : AiProcessingDecision.BeforeCutOff;
    }
}

/// <summary>`25-04`: every answer the gate can give. Only <see cref="Allowed"/> permits a provider call;
/// everything else is a refusal a caller must honour without reaching its provider port.</summary>
public enum AiProcessingDecision
{
    /// <summary>This site has bought the add-on, turned it on, and this conversation was created at or
    /// after the cut-off.</summary>
    Allowed,

    /// <summary>No effective module quantity for this site - never bought, or a lapsed subscription
    /// (`23-86`'s own <c>EffectiveQuantity</c> is what this reads, so a platform owner's unconditional
    /// grant counts).</summary>
    NotPurchased,

    /// <summary>Bought, but the tenant has not enabled it - the state every tenant is in until they
    /// accept the agreement, declare their basis and switch it on (decision 2).</summary>
    NotEnabled,

    /// <summary>On, but this conversation predates the cut-off - decision 6's own "the archive is not
    /// re-processed".</summary>
    BeforeCutOff,
}
