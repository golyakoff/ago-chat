namespace Ago.Chat.Infrastructure.Vk;

/// <summary>
/// `25-175`: bounds a live <c>groups.getById</c> check with a timeout, and classifies the result into
/// exactly three outcomes a caller can render distinctly - <see cref="VkLiveCheckOutcome.Verified"/>,
/// <see cref="VkLiveCheckOutcome.ProviderUnreachable"/> and a refusal
/// (<see cref="VkLiveCheckOutcome.RefusalReason"/> set, both flags false). Mirrors
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveTokenCheck</c>/<c>Ago.Chat.Infrastructure.MaxBot.MaxLiveTokenCheck</c>'s
/// own shape exactly for the timeout/three-outcome scaffolding - see either type's own remarks for the
/// fuller reasoning this file does not repeat.
///
/// <para><b>The one real difference from either precedent - <see cref="VkApiClient.GetGroupInfoAsync"/>
/// throws rather than returning a result object with an <c>.Ok</c> flag.</b> Telegram's/MAX's own
/// <c>GetMeAsync</c> both return a result the caller inspects; VK's own client instead returns a plain
/// <see cref="VkGroupInfo"/> on success and throws <see cref="VkApiCallException"/> on VK's own explicit
/// rejection (<see cref="VkApiClient"/>'s own remarks on why - a bad/revoked token or a missing
/// permission, the identical "client-shaped errors are refusals" case Telegram/MAX report through a
/// field instead). So this method wraps the call in a <c>try</c>/<c>catch</c> rather than branching on a
/// result's own flag - the shape VK's own client actually has, not the shape copied wholesale from a
/// sibling that happens to look different.</para>
///
/// <para><b>Why <see cref="Timeout"/> is 5 seconds, not an invented number.</b> The identical value
/// <c>TelegramLiveTokenCheck.Timeout</c>/<c>MaxLiveTokenCheck.Timeout</c> use, for the identical reason:
/// matched to <c>ChatModule.ConfigureChannelResilienceDefaults</c>'s own boundary - an HTTP call to a
/// third-party provider this codebase does not control, with a tenant waiting on a page load, not a
/// background job's own recurring charge.</para>
/// </summary>
public static class VkLiveTokenCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<VkLiveCheckOutcome> RunAsync(
        VkApiClient client, string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await client.GetGroupInfoAsync(token, linkedCts.Token);
            return VkLiveCheckOutcome.Verified();
        }
        catch (VkApiCallException ex)
        {
            // VK itself looked at the token and refused it (bad/revoked token, or missing permission) -
            // VkApiClient.GetGroupInfoAsync's own remarks. This is the one case Telegram's/MAX's own
            // checks reach through a result object's own .RefusalReason field instead - VK's client
            // reaches it by throwing, so this is where the terminal-refusal branch actually lives here.
            return VkLiveCheckOutcome.Refused(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // linkedCts fired because timeoutCts did, not because the caller's own request was
            // aborted (that case is left to propagate normally, exactly as an unbounded call would).
            return VkLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            // VK itself, or this deployment's own outbound path, did not answer at all - the identical
            // transient case VkApiClient's own remarks already describe, just caught here instead of
            // left to propagate, because this caller has a distinct "unreachable" state to report rather
            // than an unhandled exception.
            return VkLiveCheckOutcome.ProviderUnreachable();
        }
    }
}

/// <summary>Exactly three shapes, never a fourth: <see cref="Verified"/> (the token works),
/// <see cref="Refused"/> (the provider looked at the token and said no - <see cref="RefusalReason"/>
/// is always set), <see cref="ProviderUnreachable()"/> (the provider was never actually asked in time,
/// or the call could not complete at all - <see cref="RefusalReason"/> is always
/// <see langword="null"/>, because nothing about the token itself is known). Mirrors
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveCheckOutcome</c>/<c>Ago.Chat.Infrastructure.MaxBot.MaxLiveCheckOutcome</c>
/// one for one, with one deliberate omission.
///
/// <para>The static factory is named <c>ProviderUnreachable</c>, not <c>Unreachable</c>, purely to
/// avoid colliding with the <see cref="Unreachable"/> property this record's own positional parameter
/// already generates (CS0102) - the property is what every caller outside this file actually reads.</para>
///
/// <para><b>Unlike <c>TelegramLiveCheckOutcome</c>/<c>MaxLiveCheckOutcome</c>, this record carries no
/// <c>Username</c> (or any other handle) field.</b> Both siblings ride a provider-reported handle along
/// on <c>Verified</c> so their own caller can backfill <see cref="Domain.ChannelCredential.PublicHandle"/>
/// - VK has nothing to backfill: `25-147`'s own decision is that VK's public link (`vk.me/club&lt;id&gt;`)
/// is fully derivable from <c>ProviderAccountId</c> alone, computed at read time by
/// <c>PublicChannelLinkReadStore</c>, so a second, redundant field here would only invite drift this
/// item was explicitly asked not to introduce.</para>
/// </summary>
public sealed record VkLiveCheckOutcome(bool Ok, bool Unreachable, string? RefusalReason)
{
    public static VkLiveCheckOutcome Verified() => new(true, false, null);

    public static VkLiveCheckOutcome Refused(string reason) => new(false, false, reason);

    public static VkLiveCheckOutcome ProviderUnreachable() => new(false, true, null);
}
