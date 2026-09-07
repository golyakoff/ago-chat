using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteConsentAcceptances;

/// <summary>
/// `23-37`. Same isolation shape as <see cref="GetSiteConsentDocuments.GetSiteConsentDocumentsHandler"/>
/// right beside it: <see cref="Permission.SiteConfigure"/>, checked against <c>query.SiteId</c>, is the
/// entire access-control story - it runs before the purpose string is even parsed, so a caller with no
/// standing on the named site learns nothing about whether the purpose they sent was valid either.
/// </summary>
public sealed class GetSiteConsentAcceptancesHandler(IPermissionChecker permissions, IAcceptanceRepository acceptances)
{
    public async Task<Result<IReadOnlyList<SiteConsentAcceptanceDto>>> HandleAsync(
        GetSiteConsentAcceptances query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's consent acceptances.");
        }

        if (!Enum.TryParse<VisitorConsentPurpose>(query.Purpose, ignoreCase: true, out var purpose) || !Enum.IsDefined(purpose))
        {
            return PublishedDocumentErrors.InvalidPurpose(
                $"'{query.Purpose}' is not a valid consent purpose - expected '{nameof(VisitorConsentPurpose.Contact)}' or "
                + $"'{nameof(VisitorConsentPurpose.Marketing)}'.");
        }

        var documentKey = SiteConsentDocumentKey.For(query.SiteId, purpose);
        var records = await acceptances.GetForDocumentKeyAsync(documentKey, cancellationToken);

        IReadOnlyList<SiteConsentAcceptanceDto> dtos = records
            .Select(r => new SiteConsentAcceptanceDto(r.SubjectKind.ToString(), r.SubjectId, r.DocumentVersion, r.AcceptedAt))
            .ToList();
        return Result<IReadOnlyList<SiteConsentAcceptanceDto>>.Success(dtos);
    }
}
