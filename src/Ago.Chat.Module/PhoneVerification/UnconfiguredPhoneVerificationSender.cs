using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.PhoneVerification;

/// <summary>
/// `14-15`: the "no SMS/voice gateway account exists for this deployment" case - what
/// <c>ChatModule</c> actually registers as <see cref="IPhoneVerificationSender"/> today, unconditionally.
///
/// <para><b>Throws, unlike <c>UnconfiguredReplyDraftGenerator</c>'s own silent degrade-to-<c>Unavailable</c>.</b>
/// That type's caller is one operator watching one HTTP request, who sees "suggestion unavailable"
/// immediately and loses nothing by the feature quietly stepping aside. This type's only caller
/// (<c>PhoneVerificationDeliveryConsumer</c>) has no such synchronous observer - a visitor is waiting for
/// a code that would otherwise simply never arrive, with nothing in the system ever surfacing why. Throwing
/// <see cref="PhoneVerificationSenderRefusedException"/> instead sends the message straight to the
/// consumer's own dead-letter queue (excluded from the retry/breaker budget by
/// <c>PhoneVerificationResiliencePipeline</c>'s own predicates, the identical shape
/// <c>ReplyDraftResiliencePipeline</c>'s own remarks describe for <c>ReplyDraftProviderRefusedException</c>) -
/// a loud, visible, immediately-dead-lettered failure with a runbook-legible reason, not a silent black
/// hole a visitor discovers only by never receiving their code.</para>
///
/// <para><b>Unlike <c>UnconfiguredReplyDraftGenerator</c>/<c>UnconfiguredConversationCategorizer</c>,
/// `ChatModule` registers this type unconditionally - there is no <c>if (configured)</c> branch to take
/// here at all.</b> Those two check a real, named provider's own API key at composition time (YandexGPT's
/// own <c>ApiKey</c>/<c>FolderId</c>) and could in principle be filled in by an operator with an account.
/// This item is at a strictly earlier point on the same "decide the shape, defer the account" spectrum
/// (`10-05`'s own discipline, restated in this item's own backlog file): no vendor has even been chosen
/// yet (see this item's own commit-prep report for the open comparison), so there is no options class
/// whose presence could flip a conditional - the honest statement of that gap <em>is</em> the
/// unconditional registration, not a branch that would always evaluate false.</para>
/// </summary>
public sealed class UnconfiguredPhoneVerificationSender : IPhoneVerificationSender
{
    public Task SendCodeAsync(PhoneVerificationDelivery delivery, CancellationToken cancellationToken) =>
        throw new PhoneVerificationSenderRefusedException(
            "No SMS/voice verification gateway is configured for this deployment.");
}
