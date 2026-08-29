using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateCannedResponses;

/// <summary>
/// `18-03`: the console's write of a site's canned-response library.
///
/// <para>Same `site:configure` gate as the read beside it, same reasoning
/// (<see cref="Ago.Chat.Application.UseCases.GetCannedResponses.GetCannedResponsesHandler"/>'s own
/// remarks) - no new permission for a per-site list of text snippets.</para>
///
/// <para>Validation happens here, in Application, the same "value objects still throw defensively
/// whatever calls them; this handler is what turns an expected bad input into a <c>Result</c> failure"
/// split `UpdateOfflineAutoReplyHandler` draws for its own rules.</para>
///
/// <para><b>No outbox row.</b> Unlike every other `Site` write handler in this codebase, this one does
/// not depend on <c>IOutboxWriter</c>, <c>IIdGenerator</c> or <c>IClock</c> - <see cref="Site.
/// UpdateCannedResponses"/>'s own remarks explain why: nothing downstream needs telling, because
/// nothing downstream caches this value. The plain <c>SaveAsync</c> below is still the one write to the
/// aggregate's own row, and EF's <c>SaveChangesAsync</c> is still one transaction - "writes go through
/// the outbox" (`CLAUDE.md` rule 4) governs the case where a state change has an integration event to
/// publish atomically with it; this state change genuinely has none, the same shape
/// <c>Operator.GoOnline</c>/<c>GoOffline</c>/<c>ToggleSeat</c> already establish elsewhere in this
/// codebase for a mutation with no consumer waiting on it.</para>
/// </summary>
public sealed class UpdateCannedResponsesHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<CannedResponse>>> HandleAsync(
        UpdateCannedResponses command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to configure this site's canned responses.");
        }

        List<CannedResponse> responses;
        try
        {
            responses = command.Responses
                .Select(item => new CannedResponse(item.Title, item.Body))
                .ToList();
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.CannedResponseInvalid(ex.Message);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        try
        {
            site.UpdateCannedResponses(responses);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.CannedResponseInvalid(ex.Message);
        }

        await sites.SaveAsync(site, cancellationToken);

        return Result<IReadOnlyList<CannedResponse>>.Success(responses);
    }
}
