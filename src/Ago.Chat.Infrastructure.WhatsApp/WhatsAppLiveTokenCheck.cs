namespace Ago.Chat.Infrastructure.WhatsApp;

/// <summary>
/// `25-176`: bounds a live <c>GET /{phone-number-id}</c> check with a timeout, and classifies the result
/// into exactly three outcomes a caller can render distinctly - <see cref="WhatsAppLiveCheckOutcome.Verified"/>,
/// <see cref="WhatsAppLiveCheckOutcome.ProviderUnreachable"/> and a refusal
/// (<see cref="WhatsAppLiveCheckOutcome.RefusalReason"/> set, both flags false). Mirrors
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveTokenCheck</c>/<c>Ago.Chat.Infrastructure.MaxBot.MaxLiveTokenCheck</c>/
/// <c>Ago.Chat.Infrastructure.Vk.VkLiveTokenCheck</c>'s own shape exactly for the timeout/three-outcome
/// scaffolding - see any of those types' own remarks for the fuller reasoning this file does not repeat.
///
/// <para><b>This file's own shape is a combination of two precedents, not a repeat of either alone.</b>
/// <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/> throws <see cref="WhatsAppApiCallException"/> on
/// WhatsApp's own explicit rejection rather than returning a result object with an <c>.Ok</c> flag - the
/// identical shape <c>VkApiClient.GetGroupInfoAsync</c> has, so this method's own <c>try</c>/<c>catch</c>
/// is <c>VkLiveTokenCheck.RunAsync</c>'s own shape, not <c>MaxLiveTokenCheck.RunAsync</c>'s branch-on-a-flag
/// shape. But unlike VK - which has nothing to backfill (<c>VkLiveCheckOutcome</c>'s own remarks) -
/// WhatsApp has a real <see cref="Domain.ChannelCredential.PublicHandle"/> to keep current
/// (`display_phone_number`, the same public fact `25-147` already captures at connect time), so
/// <see cref="WhatsAppLiveCheckOutcome"/> carries a handle on <see cref="WhatsAppLiveCheckOutcome.Verified"/>
/// the same way <c>MaxLiveCheckOutcome.Verified(string? username)</c> does. So: VK's own exception-catching
/// control flow, combined with MAX's own carries-a-handle outcome shape.</para>
///
/// <para><b>The extra <paramref name="phoneNumberId"/> parameter neither Telegram's, MAX's nor VK's own
/// live-check signature needs.</b> Telegram's `getMe`/MAX's `getMe`/VK's `groups.getById` all
/// self-discover their target from the token alone; WhatsApp's token does not - a WhatsApp Business
/// Account can hold more than one phone number, so which one to ask about has to be supplied on every
/// call, not just at connect time. <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own remarks have
/// the full contrast.</para>
///
/// <para><b>Why <see cref="Timeout"/> is 5 seconds, not an invented number.</b> The identical value
/// <c>TelegramLiveTokenCheck.Timeout</c>/<c>MaxLiveTokenCheck.Timeout</c>/<c>VkLiveTokenCheck.Timeout</c>
/// use, for the identical reason: matched to <c>ChatModule.ConfigureChannelResilienceDefaults</c>'s own
/// boundary - an HTTP call to a third-party provider this codebase does not control, with a tenant
/// waiting on a page load, not a background job's own recurring charge.</para>
/// </summary>
public static class WhatsAppLiveTokenCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<WhatsAppLiveCheckOutcome> RunAsync(
        WhatsAppApiClient client, string token, string phoneNumberId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var info = await client.GetPhoneNumberAsync(token, phoneNumberId, linkedCts.Token);
            return WhatsAppLiveCheckOutcome.Verified(info.DisplayPhoneNumber);
        }
        catch (WhatsAppApiCallException ex)
        {
            // WhatsApp itself looked at the (token, phone_number_id) pair and refused it (a bad/expired
            // token, a token lacking the messaging permission, or a phone number id the token is not
            // authorized for) - WhatsAppApiClient.GetPhoneNumberAsync's own remarks. This is the one case
            // Telegram's/MAX's own checks reach through a result object's own .RefusalReason field
            // instead - WhatsApp's client reaches it by throwing, exactly as VK's does, so this is where
            // the terminal-refusal branch actually lives here.
            return WhatsAppLiveCheckOutcome.Refused(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // linkedCts fired because timeoutCts did, not because the caller's own request was
            // aborted (that case is left to propagate normally, exactly as an unbounded call would).
            return WhatsAppLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            // WhatsApp itself, or this deployment's own outbound path, did not answer at all - the
            // identical transient case WhatsAppApiClient's own remarks already describe, just caught here
            // instead of left to propagate, because this caller has a distinct "unreachable" state to
            // report rather than an unhandled exception.
            return WhatsAppLiveCheckOutcome.ProviderUnreachable();
        }
    }
}

/// <summary>Exactly three shapes, never a fourth: <see cref="Verified"/> (the token works, for this
/// specific phone number id), <see cref="Refused"/> (the provider looked at the token and said no -
/// <see cref="RefusalReason"/> is always set), <see cref="ProviderUnreachable()"/> (the provider was never
/// actually asked in time, or the call could not complete at all - <see cref="RefusalReason"/> is always
/// <see langword="null"/>, because nothing about the token itself is known). Mirrors
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveCheckOutcome</c>/<c>Ago.Chat.Infrastructure.MaxBot.MaxLiveCheckOutcome</c>
/// one for one, including the handle field VK's own equivalent deliberately omits - see
/// <see cref="WhatsAppLiveTokenCheck"/>'s own remarks for why WhatsApp is in MAX's camp on this point, not
/// VK's.
///
/// <para>The static factory is named <c>ProviderUnreachable</c>, not <c>Unreachable</c>, purely to
/// avoid colliding with the <see cref="Unreachable"/> property this record's own positional parameter
/// already generates (CS0102) - the property is what every caller outside this file actually reads.</para>
///
/// <para><see cref="DisplayPhoneNumber"/> rides along on <see cref="Verified"/> only - a refused or
/// unreachable check has nothing to say about the number's own current display format.</para>
/// </summary>
public sealed record WhatsAppLiveCheckOutcome(bool Ok, bool Unreachable, string? RefusalReason, string? DisplayPhoneNumber = null)
{
    public static WhatsAppLiveCheckOutcome Verified(string? displayPhoneNumber) => new(true, false, null, displayPhoneNumber);

    public static WhatsAppLiveCheckOutcome Refused(string reason) => new(false, false, reason);

    public static WhatsAppLiveCheckOutcome ProviderUnreachable() => new(false, true, null);
}
