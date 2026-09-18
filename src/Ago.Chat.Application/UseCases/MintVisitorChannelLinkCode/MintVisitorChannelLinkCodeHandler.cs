using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.MintVisitorChannelLinkCode;

/// <summary>
/// `25-148`: the third originator of a <see cref="PendingChannelLinkRequest"/>, alongside `14-12`'s
/// console-initiated (<c>RequestChannelLinkFromConsoleHandler</c>) and visitor-initiated
/// (<c>HandleLinkIdentityCommandHandler</c>) paths - reusing the identical domain factory, code
/// generator and repository those two already established, per this item's own explicit instruction to
/// extend the existing mechanism rather than build a second one. What is genuinely new is only the
/// *caller*: <c>Ago.Chat.Api.Auth.AuthEndpoints</c>'s own visitor-session mint/renew handlers, minting a
/// code proactively, before any conversation exists at all - the one shape neither existing originator
/// could reuse as-is, since both are structured around resolving a target <see cref="VisitorId"/> from an
/// already-open <see cref="Conversation"/> (`RequestChannelLinkFromConsoleHandler`'s own
/// <see cref="IConversationRepository"/> lookup; `HandleLinkIdentityCommandHandler`'s own trigger message).
/// A visitor session mint/renew already *has* its own <see cref="VisitorId"/> directly - freshly generated
/// on a mint, or read off the validated token on a renewal - so this handler needs no conversation lookup
/// of its own at all, which is what makes it a smaller handler than either precedent rather than a third
/// copy of one.
///
/// <para><b>No permission check, unlike <c>RequestChannelLinkFromConsoleHandler</c>.</b> That handler
/// gates on <see cref="Permission.ConversationSend"/> because an *operator* is acting on a *visitor's*
/// behalf, and needs to be shown to be entitled to act in that conversation at all. Here the only actor is
/// the visitor's own already-authenticated session (the widget handshake itself, gated by
/// <c>AuthEndpoints</c>'s own site/origin/rate-limit checks) minting a code for itself - there is no
/// second party whose authority needs proving, the same reason <c>HandleLinkIdentityCommandHandler</c>'s
/// own visitor-initiated path needs no permission check either.</para>
///
/// <para><b>Always <c>RequestedByOperatorId: null</c> - the visitor-initiated shape, not the
/// console-initiated one</b> (`adr/0079` decision 2's own "two symmetric originators" - this is
/// structurally the identical shape as `/linkidentity`'s own visitor-typed request, just minted by the
/// system on the visitor's behalf at session time instead of waiting for the visitor to type a command).
/// </para>
///
/// <para><b>Channel-neutral by construction, Telegram-only by its one caller's own choice.</b> Nothing in
/// this handler or in <see cref="PendingChannelLinkRequest"/> itself is Telegram-specific - the command
/// takes any <see cref="ChannelKind"/> - but `25-148`'s own scope is explicit that only Telegram's `url`
/// carries a `?start=` code in this item ("MAX and VK do not get a `?start=` code in this item... do not
/// build a generic 'every channel gets a linking code' mechanism speculatively"). So the only real caller,
/// <c>AuthEndpoints</c>, only ever passes <see cref="Domain.ChannelKind.Telegram"/> - this handler simply
/// has no reason to refuse another kind by itself, the identical "channel-neutral port, one caller's own
/// narrower policy" shape <see cref="Domain.ChannelCredential.Register"/>'s own optional parameters
/// already have relative to their own, narrower call sites.</para>
///
/// <para><b>Returns <see langword="null"/>, mints nothing, when <see cref="MintVisitorChannelLinkCode.VisitorId"/>
/// has no persisted <see cref="Visitor"/> row yet - found live while testing this item, not assumed.</b>
/// `pending_channel_link_requests.visitor_id` is a real foreign key to `visitors` - both existing
/// originators (`RequestChannelLinkFromConsoleHandler`, `HandleLinkIdentityCommandHandler`) satisfy it for
/// free, because each resolves its <see cref="VisitorId"/> from an already-loaded <see cref="Conversation"/>,
/// which cannot exist without its own visitor row already persisted. A visitor-session <em>mint</em> has
/// no such guarantee: <c>AuthEndpoints.HandleVisitorSessionAsync</c> generates a brand-new
/// <see cref="Domain.VisitorId"/> and issues a token for it without ever writing a <see cref="Visitor"/>
/// row - that row is created lazily, the first time the visitor actually sends a message
/// (<c>ReceiveChannelMessageHandler</c>/<c>StartConversationHandler</c>'s own precedent) - and a
/// <em>renewal</em> is no safer: it fires on the widget's own background timer, independent of whether the
/// visitor ever opened the chat panel at all. Creating a <see cref="Visitor"/> row here just to satisfy the
/// foreign key was considered and rejected: it would make an anonymous page load that never engages with
/// chat permanently count as a "visitor" on every site with Telegram connected, silently inflating visitor
/// counts and creating a personal-data-bearing row for someone who never interacted - a real, unwanted side
/// effect of a purely additive feature. Skipping the mint instead is the correct reading of what a linking
/// code is <em>for</em>: continuing an identity that does not exist yet has nothing to continue, so
/// <c>AuthEndpoints</c>' own caller falls back to a plain Telegram link with no `?start=` code - a visitor
/// opening it before ever messaging through the widget simply starts an ordinary, unlinked Telegram
/// conversation, which is a graceful degradation, not a failure.</para>
/// </summary>
public sealed class MintVisitorChannelLinkCodeHandler(
    IVisitorRepository visitors,
    IPendingChannelLinkRequestRepository pendingLinks,
    IPendingChannelLinkCodeGenerator codeGenerator,
    PendingChannelLinkRequestOptions options,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<MintedVisitorChannelLinkCode?> HandleAsync(
        MintVisitorChannelLinkCode command, CancellationToken cancellationToken)
    {
        if (await visitors.GetByIdAsync(command.VisitorId, cancellationToken) is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var request = PendingChannelLinkRequest.Request(
            new PendingChannelLinkRequestId(idGenerator.NewId(now)), command.SiteId, command.VisitorId,
            command.Kind, codeHash, requestedByOperatorId: null, now, options.ValidFor);

        // Committed immediately, not staged - this handler owns its own unit of work exactly the way
        // RequestChannelLinkFromConsoleHandler's own standalone call does (IPendingChannelLinkRequestRepository's
        // own remarks on why Stage exists only for the MessageAccepted-driven path, which this is not).
        await pendingLinks.SaveAsync(request, cancellationToken);

        return new MintedVisitorChannelLinkCode(code, request.ExpiresAt);
    }
}
