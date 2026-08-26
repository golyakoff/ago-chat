using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ReceiveChannelMessage;

/// <summary>
/// `14-01`: one inbound message from any external channel, in channel-neutral terms - the inbound half
/// of the contract <see cref="Abstractions.IInboundChannelAdapter"/> describes (see that interface's
/// remarks on why the inbound half is a command and not a method).
///
/// <para><see cref="SiteId"/> is <b>never</b> caller-supplied. It is the tenant whose credentials
/// received this message - the site that owns the MAX bot token, or the site that rents the SMS long
/// number - resolved by the concrete adapter from its own configuration before it constructs this. A
/// provider payload cannot name a site, which is exactly why this handler's entry in
/// <c>TenantScopeExemptions</c> is safe; the exemption states the same claim.</para>
///
/// <para><b>There is deliberately no provider-timestamp field.</b> Every channel provider stamps its
/// deliveries with a time, and every one of them is tempting to sort by. CLAUDE.md rules 6 and 11 say
/// per-conversation order is the server-assigned <c>Message.Sequence</c> and never a clock - so the
/// safest place to enforce that is the boundary the value would have to cross to become dangerous.
/// This record has no slot for it, <c>PendingMessage</c> has no slot for it, and
/// <c>ChannelPortTests.ReceiveChannelMessage_CarriesNoTimestamp</c> fails if anyone adds one. A future
/// item that genuinely needs "the provider says it was sent at ..." for <em>display</em> should add it
/// under a name that says so, and read `adr/0055` first.</para>
/// </summary>
/// <param name="ExternalMessageId">The provider's own id for this message - the idempotency key. See
/// <see cref="Domain.ExternalMessageId.ToClientMessageId"/> for what is done with it and why.</param>
public sealed record ReceiveChannelMessage(
    SiteId SiteId,
    ChannelKind Kind,
    ExternalChannelAddress Sender,
    ExternalMessageId ExternalMessageId,
    string Body);
