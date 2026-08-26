using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-01`: the one shape every AGO Inbox channel implements - `14-02`'s MAX adapter, `14-03`'s SMS
/// adapter, and whichever of `14-05`'s candidates ship. It carries no MAX, Telegram or carrier concept
/// of any kind; `adr/0006`'s "largest common denominator that does not lie" is the standard it is held
/// to, exactly as <c>IEventPublisher</c> is held to it for RabbitMQ exchanges.
///
/// <para><b>Why the methods are outbound-only, when the type is named for the inbound direction.</b>
/// This is the decision most worth arguing with, and `adr/0055` records it. A port exists to invert a
/// dependency: it is needed exactly where inner code must call outer code. Sending a reply is that -
/// the application knows an operator answered and has no idea whether that means an HTTPS POST to MAX
/// or a carrier's SMPP session, so it must call through an abstraction. <em>Receiving</em> is already
/// pointing the right way: the concrete adapter is the outer thing, it is the one that gets woken by a
/// webhook or a long poll, and it calls inwards into
/// <c>UseCases.ReceiveChannelMessage.ReceiveChannelMessageHandler</c>. Putting a
/// <c>ParseInbound(rawBytes, headers)</c> method here to make the interface look symmetric would have
/// achieved the opposite of this port's purpose: it would have hard-coded "a channel is delivered over
/// HTTP", which is false for a long-polling adapter, and it would have dragged a transport shape above
/// the Infrastructure boundary. The inbound contract is therefore a <em>command</em>
/// (<c>ReceiveChannelMessage</c>), not a method - and it is just as binding, because it is the only
/// entry point a channel has.</para>
///
/// <para><b>How a second channel plugs in, with this file untouched.</b> Implement this interface,
/// return a new <see cref="ChannelKind"/> from <see cref="Kind"/>, register the implementation in the
/// host's DI so <see cref="IInboundChannelAdapterRegistry"/> can find it, and have whatever wakes it
/// (a webhook route, a hosted long-poll service) build a <c>ReceiveChannelMessage</c> and dispatch it.
/// Nothing in Domain, Application or the pipeline changes. That is the test this port was designed
/// against.</para>
///
/// <para><b>Resilience is not this interface's business.</b> An implementation is written as if the
/// provider always answers; timeout, retry, circuit breaker and bulkhead are applied by wrapping it
/// (<c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c> over
/// <c>Ago.Platform.Resilience</c>), the same "resilience hidden behind the port" shape
/// <see cref="IWebhookDeliveryClient"/>, <c>RedisCache</c> and <c>S3FileStorage</c> already use.
/// resilience.md's table now names "outbound channel provider APIs" as a boundary this covers.</para>
/// </summary>
public interface IInboundChannelAdapter
{
    /// <summary>Which channel this adapter serves. The registry keys on it, and it is the only
    /// discriminator anything above Infrastructure ever sees - no <c>is MaxAdapter</c>, no type
    /// switch.</summary>
    ChannelKind Kind { get; }

    /// <summary>
    /// Sends one operator reply back out through this channel.
    ///
    /// <para><b>The two failure directions are deliberately different.</b> A <em>terminal</em> refusal
    /// by the provider - unknown number, blocked recipient, deleted chat - is an expected outcome and
    /// comes back as <see cref="ChannelSendOutcome.Delivered"/> <see langword="false"/> with a reason;
    /// retrying it would never help. A <em>transient</em> fault - a timeout, a 5xx, a dropped
    /// connection - is thrown, because throwing is what the resilience pipeline wrapping this call
    /// acts on (coding-style.md's own rule: <c>Result</c> for expected failures, exceptions for
    /// infrastructure faults). An implementation that swallowed a timeout into
    /// <c>Delivered: false</c> would silently disable every retry and breaker built around it.</para>
    /// </summary>
    Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// One operator reply, in channel-neutral terms.
///
/// <para><see cref="MessageId"/> is carried so the adapter can hand the provider a stable idempotency
/// key of its own where the provider supports one - the same "idempotency for the receiver" rule
/// resilience.md already states for outbound webhooks, and the mirror image of what
/// <see cref="Domain.ExternalMessageId.ToClientMessageId"/> does on the way in.</para>
///
/// <para>There is no timestamp field, and that is not an oversight: see <c>ReceiveChannelMessage</c>'s
/// own remarks and `adr/0055`. Nothing about when a message happened travels on a channel boundary in
/// either direction.</para>
/// </summary>
public sealed record OutboundChannelMessage(
    ChannelKind Kind,
    ExternalChannelAddress Recipient,
    ConversationId ConversationId,
    MessageId MessageId,
    MessageBody Body);

/// <summary>The terminal result of one <see cref="IInboundChannelAdapter.SendAsync"/> call - see that
/// method's remarks on why a transient fault never appears here.
/// <paramref name="ProviderMessageId"/> is whatever the provider called it, kept only for support and
/// diagnostics; nothing routes on it.</summary>
public sealed record ChannelSendOutcome(bool Delivered, string? ProviderMessageId, string? FailureReason)
{
    public static ChannelSendOutcome Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static ChannelSendOutcome Refused(string reason) => new(false, null, reason);
}
