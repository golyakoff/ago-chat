using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: the console's own read. Gated on <see cref="Permission.SiteConfigure"/> - the same
/// permission the three writes need, because the body carries the agreement text and the audit facts
/// (who declared, when), which is tenant-administrative information rather than something every
/// operator needs.
///
/// <para><b>Reads through the same ports the enable path checks, never a denormalised status column.</b>
/// A second place that says "accepted" could disagree with the acceptance table, and the whole value of
/// `24-01`'s records is that they are the only answer. The cost is four reads for one screen, which is
/// the right trade for a settings page opened rarely.</para>
/// </summary>
public sealed class GetAiAddOnStatusHandler(
    IPermissionChecker permissions,
    IModuleQuantityGrantStore grants,
    IDocumentRepository documents,
    IAcceptanceRepository acceptances,
    IAiProcessingBasisDeclarationRepository declarations,
    IAiAddOnEnablementRepository enablements,
    AiAddOnOptions options)
{
    public async Task<Result<AiAddOnStatus>> HandleAsync(GetAiAddOnStatus query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return AiAddOnErrors.Forbidden("Operator does not have permission to configure this site.");
        }

        var purchased = false;
        if (!string.IsNullOrWhiteSpace(options.ModuleKey))
        {
            purchased = await grants.GetQuantityAsync(
                query.SiteId, new ModuleKey(options.ModuleKey), cancellationToken) > 0;
        }

        var current = await documents.FindCurrentAsync(AiAddOnAgreement.DocumentKey, cancellationToken);

        AcceptanceRecord? acceptedCurrent = null;
        if (current is not null)
        {
            var tenantAcceptances = await acceptances.GetForSubjectAsync(
                AcceptanceSubjectKind.Tenant, query.SiteId.Value, cancellationToken);
            acceptedCurrent = tenantAcceptances
                .Where(a => string.Equals(a.DocumentKey, AiAddOnAgreement.DocumentKey, StringComparison.Ordinal)
                    && string.Equals(a.DocumentVersion, current.Version, StringComparison.Ordinal))
                .OrderByDescending(a => a.AcceptedAt)
                .FirstOrDefault();
        }

        var declaration = await declarations.GetLatestForSiteAsync(query.SiteId, cancellationToken);
        var enablement = await enablements.GetForSiteAsync(query.SiteId, cancellationToken);

        return new AiAddOnStatus(
            purchased,
            enablement?.IsEnabled ?? false,
            enablement?.EffectiveFrom,
            AiAddOnAgreement.DocumentKey,
            current?.Version,
            current?.Title,
            current?.Body,
            acceptedCurrent?.DocumentVersion,
            acceptedCurrent?.AcceptedAt,
            declaration?.DeclaredBy.Value,
            declaration?.DeclaredAt);
    }
}
