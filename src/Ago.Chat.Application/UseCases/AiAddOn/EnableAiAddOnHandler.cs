using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: the one place the AI add-on is ever turned on. Four preconditions, each with its own
/// refusal, checked in the order a tenant encounters them:
///
/// <list type="number">
/// <item><see cref="Permission.SiteConfigure"/> - the same gate every other act-for-the-tenant write uses.</item>
/// <item><b>Bought.</b> An effective module quantity (`22-07`/`23-86`) for this deployment's own AI
/// module key. Decision 1: the add-on is paid, and "enabled but never purchased" must not be reachable.</item>
/// <item><b>The current version of the agreement is accepted</b>, by this tenant, as a `24-01` record
/// naming that version. Read from the real acceptance table, never from a flag somebody could set.</item>
/// <item><b>A basis declaration exists</b> - decision 5's separate fact, with its own separate refusal.</item>
/// </list>
///
/// <para><b>Why the acceptance check reads the records rather than a column.</b> The acceptance table is
/// insert-only and version-bearing; a boolean "has accepted" cached anywhere would answer the wrong
/// question the moment a new version is published, which is exactly when the answer matters. Comparing
/// <see cref="IDocumentRepository.FindCurrentAsync"/>'s own version against the tenant's own records
/// means republishing the agreement automatically puts every tenant back into
/// <see cref="AiAddOnErrors.AgreementNotAccepted"/> for the next enable - at the cost, accepted here,
/// that a republish does not *disable* anybody already running (that would be a silent outage from a
/// documentation edit; `25-04`'s Open questions leave the re-consent policy to the lawyer).</para>
///
/// <para><b>The cut-off is <see cref="IClock.UtcNow"/> at the moment of the write.</b> Not the
/// acceptance's timestamp, not the declaration's, and never a caller-supplied instant - decision 6's
/// "only conversations from the moment of enabling", made unforgeable by there being no parameter to
/// forge.</para>
/// </summary>
public sealed class EnableAiAddOnHandler(
    IPermissionChecker permissions,
    IModuleQuantityGrantStore grants,
    IDocumentRepository documents,
    IAcceptanceRepository acceptances,
    IAiProcessingBasisDeclarationRepository declarations,
    IAiAddOnEnablementRepository enablements,
    AiAddOnOptions options,
    IClock clock)
{
    public async Task<Result<AiAddOnEnabled>> HandleAsync(EnableAiAddOn command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.EnabledBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return AiAddOnErrors.Forbidden("Operator does not have permission to configure this site.");
        }

        if (string.IsNullOrWhiteSpace(options.ModuleKey))
        {
            return AiAddOnErrors.NotPurchased(
                $"This deployment has declared no '{AiAddOnOptions.SectionName}:{nameof(AiAddOnOptions.ModuleKey)}', "
                + "so the AI add-on cannot be sold or enabled here.");
        }

        var quantity = await grants.GetQuantityAsync(command.SiteId, new ModuleKey(options.ModuleKey), cancellationToken);
        if (quantity <= 0)
        {
            return AiAddOnErrors.NotPurchased("This site has not bought the AI add-on.");
        }

        var current = await documents.FindCurrentAsync(AiAddOnAgreement.DocumentKey, cancellationToken);
        if (current is null)
        {
            return AiAddOnErrors.AgreementNotPublished(
                $"No version of '{AiAddOnAgreement.DocumentKey}' has been published in this deployment.");
        }

        var tenantAcceptances = await acceptances.GetForSubjectAsync(
            AcceptanceSubjectKind.Tenant, command.SiteId.Value, cancellationToken);
        var acceptedCurrent = tenantAcceptances.Any(a =>
            string.Equals(a.DocumentKey, AiAddOnAgreement.DocumentKey, StringComparison.Ordinal)
            && string.Equals(a.DocumentVersion, current.Version, StringComparison.Ordinal));
        if (!acceptedCurrent)
        {
            return AiAddOnErrors.AgreementNotAccepted(
                $"Version '{current.Version}' of '{AiAddOnAgreement.DocumentKey}' has not been accepted for this site.");
        }

        // Decision 5's own fact, looked up separately and refused separately - see this handler's own
        // remarks, and AiProcessingBasisDeclaration's, for why this is not the same check as the one
        // immediately above it.
        var declaration = await declarations.GetLatestForSiteAsync(command.SiteId, cancellationToken);
        if (declaration is null)
        {
            return AiAddOnErrors.BasisNotDeclared(
                "This site has not declared it holds a lawful basis for its visitors' data reaching the provider.");
        }

        var now = clock.UtcNow;
        var enablement = await enablements.GetForSiteAsync(command.SiteId, cancellationToken)
            ?? AiAddOnEnablement.ForSite(command.SiteId);
        enablement.Enable(command.EnabledBy, current.DocumentKey, current.Version, now);
        await enablements.SaveAsync(enablement, cancellationToken);

        return new AiAddOnEnabled(command.SiteId.Value, now, current.DocumentKey, current.Version);
    }
}

/// <summary>What the caller gets back - chiefly <paramref name="EffectiveFrom"/>, the cut-off, which is
/// the one fact a console must show a tenant immediately ("nothing before this moment is ever sent").</summary>
public sealed record AiAddOnEnabled(
    Guid SiteId, DateTimeOffset EffectiveFrom, string AcceptedDocumentKey, string AcceptedDocumentVersion);
