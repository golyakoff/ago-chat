using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RequestChannelLinkFromConsole;

/// <summary>
/// `14-12`/`adr/0079`: the console-initiated half of verified channel-identity linking. Generation only
/// - the confirmation and the actual <see cref="ChannelIdentity.Link"/> call happen later, in
/// <c>ReceiveChannelMessageHandler</c>'s new branch, once the visitor sends the code back from the
/// target channel. This handler's whole job is "mint a code, remember what it proves."
///
/// <para><b>Gated on <see cref="Permission.ConversationSend"/>, not a channel-management permission -
/// the backlog item's own instruction, stated plainly here.</b> Requesting a link only ever produces a
/// message an operator could have typed by hand anyway (the code, relayed as ordinary conversation
/// text) - it grants no access to a channel credential, no ability to read another visitor's history,
/// and no mutation happens until the visitor themselves proves control of the new address. An operator
/// already trusted to reply to this conversation is not being handed any new capability by also being
/// able to ask for a link code; requiring <see cref="Permission.ChannelManage"/> instead would gate this
/// on a permission about the shop's own bot credentials, which is a different, unrelated resource.</para>
/// </summary>
public sealed class RequestChannelLinkFromConsoleHandler(
    IConversationRepository conversations,
    IPendingChannelLinkRequestRepository pendingLinks,
    IPendingChannelLinkCodeGenerator codeGenerator,
    IPermissionChecker permissions,
    PendingChannelLinkRequestOptions options,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RequestedChannelLink>> HandleAsync(
        RequestChannelLinkFromConsole command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages in this conversation.");
        }

        if (!Enum.TryParse<ChannelKind>(command.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            return ConversationErrors.ChannelLinkInvalidKind(command.Kind);
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            // Wrong-tenant reads like no row - the same info-hiding shape every cross-tenant guard in
            // this codebase already uses (ConversationErrors.NotFound's own callers).
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var now = clock.UtcNow;
        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var request = PendingChannelLinkRequest.Request(
            new PendingChannelLinkRequestId(idGenerator.NewId(now)), command.SiteId, conversation.VisitorId,
            kind, codeHash, command.RequestedBy, now, options.ValidFor);
        await pendingLinks.SaveAsync(request, cancellationToken);

        return new RequestedChannelLink(code, request.ExpiresAt, kind);
    }
}
