using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetAcceptancesForSubject;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetTenantAgreementsForSite;

/// <summary>
/// `23-52`'s own Done-when: "the tenant can read what is on their account, including a superseded
/// version." <b>Not a second acceptance-recording path or a second store</b> - `24-01` already built
/// <see cref="IAcceptanceRepository"/> and its own subject-agnostic read
/// (<c>GetAcceptancesForSubjectHandler</c>), and `24-03`'s <c>RegisterSiteHandler</c> already writes
/// one <see cref="AcceptanceRecord"/> per required document at registration - this handler adds
/// exactly one thing neither of those does: a site-scoped, permission-gated read a tenant's own
/// operator can call, the "way to see them" `24-01`'s own read-back handler deliberately left
/// unauthenticated and endpoint-free (Scope: "showing anything to anybody" was `24-03`/`24-04`/`24-05`'s
/// job, and none of them built this particular read either).
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/>, calling <see cref="IAcceptanceRepository"/>
/// directly rather than composing <c>GetAcceptancesForSubjectHandler</c>.</b> The same shape
/// <c>GetAccessRecordsForSiteHandler</c> (`24-12`) and <c>GetContactRevealsForSiteHandler</c> (`23-11`)
/// already use for an identically compliance-shaped tenant read: this handler is the narrower filter on
/// an existing row set, not a new query shape (<c>GetOwnAnalyticsForOperatorHandler</c>'s own remarks
/// make the identical argument for reusing a shared read rather than restating it). Composing the
/// subject-agnostic handler instead would have bought nothing - it does no work beyond the repository
/// call this handler already needs to make - and would have left <c>TenantScopeExemptions</c>'s own
/// entry for it ("no host endpoint calls this yet") quietly false the moment this file shipped.</para>
///
/// <para><b>Why the subject id is always <see cref="SiteId"/>.Value, never a caller-supplied one.</b>
/// An <see cref="AcceptanceRecord"/> for <see cref="AcceptanceSubjectKind.Tenant"/> is written with
/// <c>SubjectId == siteId.Value</c> (<c>AcceptanceRecord.ForTenant</c>) - so filtering
/// <see cref="IAcceptanceRepository.GetForSubjectAsync"/> by <c>(Tenant, query.SiteId.Value)</c> can
/// only ever return this site's own rows, structurally, with no second check required to keep another
/// tenant's acceptances from leaking in.</para>
/// </summary>
public sealed class GetTenantAgreementsForSiteHandler(IAcceptanceRepository acceptances, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<AcceptanceRecordDto>>> HandleAsync(
        GetTenantAgreementsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's agreements.");
        }

        var records = await acceptances.GetForSubjectAsync(AcceptanceSubjectKind.Tenant, query.SiteId.Value, cancellationToken);

        // `Result<T>.Success(...)` explicitly - the same "an interface-typed T needs the named
        // factory, not the implicit operator" convention every other `Result<IReadOnlyList<...>>>`
        // handler in this codebase already follows (a user-defined implicit conversion cannot have an
        // interface as its source or target type, so `return dtos;` alone does not compile here).
        return Result<IReadOnlyList<AcceptanceRecordDto>>.Success(records
            .Select(r => new AcceptanceRecordDto(
                r.Id.Value, r.SubjectKind, r.SubjectId, r.DocumentKey, r.DocumentVersion, r.AcceptedAt, r.ClientIp, r.UserAgent))
            .ToList());
    }
}
