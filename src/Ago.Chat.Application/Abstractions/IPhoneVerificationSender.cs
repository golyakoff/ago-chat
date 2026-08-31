using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-15`: the outbound half of phone verification - given a plaintext code and a phone number, places
/// the real SMS or voice call. Deliberately provider-neutral, the identical discipline
/// <see cref="IReplyDraftGenerator"/>'s own remarks state for its LLM provider and
/// <see cref="IYooKassaPaymentsClient"/>'s for ЮKassa: no gateway's own request/response shape, auth
/// scheme, or vendor name may appear on this interface or in <c>Ago.Chat.Application</c> at all. No
/// concrete gateway client exists yet in this codebase (this item's own backlog file: "the vendor/gateway
/// decision... this item's own Done-when does not require the account to exist, only the decision and the
/// port shape to be real") - <c>Ago.Chat.Module.PhoneVerification.UnconfiguredPhoneVerificationSender</c>
/// is what is actually registered today, and a real
/// <c>Ago.Chat.Infrastructure.&lt;Vendor&gt;.&lt;Vendor&gt;PhoneVerificationSenderClient</c> is what would
/// slot in behind this port the day an account exists, unchanged on this side.
///
/// <para><b>Called from a `Ago.Chat.Worker` consumer, never from the initiating HTTP request.</b> CLAUDE.md
/// rule 4 - "never publish from inside a request handler" - applies with extra force here: this call
/// costs real money per attempt and can hang against a third-party gateway, and a visitor-facing HTTP
/// request must never wait on either. <c>InitiatePhoneVerificationHandler</c> only ever persists the
/// pending verification and an outbox row; <c>PhoneVerificationDeliveryConsumer</c> is the one caller of
/// this port.</para>
///
/// <para><b>Throws, does not return a result type, unlike <see cref="IInboundChannelAdapter.SendAsync"/>'s
/// own <c>ChannelSendOutcome</c>.</b> That port's caller (<c>DeliverChannelMessageHandler</c>) treats a
/// provider-side refusal as a legitimate, non-retryable business outcome it must record. This port has no
/// such caller - <see cref="PhoneVerificationDeliveryConsumer"/> only ever acks or dead-letters, so there
/// is nothing for a third "refused but not thrown" state to be read by. A transient failure should be
/// thrown as any other exception (retried by <c>PhoneVerificationResiliencePipeline</c>, then by the
/// consumer's own retry/DLQ backstop - see <c>ResilientPhoneVerificationSender</c>'s own remarks on why it
/// lets a fault propagate rather than degrading it, unlike <c>ResilientReplyDraftGenerator</c>). A
/// terminal, retry-proof refusal (a phone number the gateway will never accept, a malformed request) should
/// throw <see cref="PhoneVerificationSenderRefusedException"/> instead, so the resilience pipeline can
/// exclude it from the retry/breaker budget - the identical shape
/// <see cref="ReplyDraftProviderRefusedException"/> already establishes, for the identical
/// cost-avoidance reason: retrying three times before giving up on a number that was never going to work
/// spends real money for an outcome no retry could change.</para>
/// </summary>
public interface IPhoneVerificationSender
{
    Task SendCodeAsync(PhoneVerificationDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>The only thing a sender needs to know - no <see cref="PendingPhoneVerificationId"/>, no
/// <see cref="SiteId"/>: a gateway call is "text/call this number with this code", nothing else, and a
/// port shaped around anything wider would leak this item's own persistence concerns into
/// Infrastructure.</summary>
public sealed record PhoneVerificationDelivery(string Phone, string Code, PhoneVerificationDeliveryMethod Method);
