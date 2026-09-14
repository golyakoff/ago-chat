using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RecordAcceptance;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: records the tenant's acceptance of the AI add-on agreement, through `24-01`'s own
/// <see cref="RecordAcceptanceHandler"/> rather than a second acceptance-writing path.
///
/// <para><b>Why this wraps <see cref="RecordAcceptanceHandler"/> instead of replacing it.</b>
/// <see cref="RecordAcceptanceHandler"/>'s own remarks are explicit that it deliberately carries no
/// permission check and no tenant scoping - "which caller may invoke this at all is `24-03`/`24-04`/
/// `24-05`'s own concern once they build the endpoints that call it". This is that concern, for this
/// document: the two things this handler adds are the permission gate
/// (<see cref="Permission.SiteConfigure"/> - the same one every other "act for the tenant as a whole"
/// write uses) and the current-version check. The record itself is written by the existing machinery,
/// so an acceptance made here is indistinguishable in the table from one made at signup, which is what
/// keeps `24-01`'s own "what did they agree to in March" query working with no special case.</para>
///
/// <para><b>The subject is the <see cref="AcceptanceSubjectKind.Tenant"/>, not the operator who
/// clicked.</b> `adr/0076`: the agreement is between AGO and the shop, and it survives the operator who
/// signed it leaving. The individual is still recorded - on the *declaration*
/// (<see cref="AiProcessingBasisDeclaration.DeclaredBy"/>), which is the statement a person makes about
/// facts they personally assert. That asymmetry is deliberate rather than an oversight: "the company
/// agreed to our terms" and "this named person told us they have a basis" are different claims.</para>
/// </summary>
public sealed class AcceptAiAddOnAgreementHandler(
    IPermissionChecker permissions,
    IDocumentRepository documents,
    RecordAcceptanceHandler recordAcceptance)
{
    public async Task<Result<RecordedAcceptance>> HandleAsync(
        AcceptAiAddOnAgreement command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.AcceptedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return AiAddOnErrors.Forbidden("Operator does not have permission to configure this site.");
        }

        var current = await documents.FindCurrentAsync(AiAddOnAgreement.DocumentKey, cancellationToken);
        if (current is null)
        {
            return AiAddOnErrors.AgreementNotPublished(
                $"No version of '{AiAddOnAgreement.DocumentKey}' has been published in this deployment.");
        }

        if (!string.Equals(current.Version, command.Version?.Trim(), StringComparison.Ordinal))
        {
            return AiAddOnErrors.AgreementVersionStale(
                $"The current version of '{AiAddOnAgreement.DocumentKey}' is '{current.Version}'; "
                + "read it again before accepting.");
        }

        return await recordAcceptance.HandleAsync(
            new RecordAcceptance.RecordAcceptance(
                AcceptanceSubjectKind.Tenant, command.SiteId.Value, current.DocumentKey, current.Version,
                command.ClientIp, command.UserAgent),
            cancellationToken);
    }
}
