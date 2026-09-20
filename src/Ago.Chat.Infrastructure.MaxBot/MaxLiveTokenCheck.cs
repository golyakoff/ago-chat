namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `25-174`: bounds a live <c>GET /me</c> check with a timeout, and classifies the result into exactly
/// three outcomes a caller can render distinctly - <see cref="MaxLiveCheckOutcome.Verified"/>,
/// <see cref="MaxLiveCheckOutcome.ProviderUnreachable"/> and a refusal
/// (<see cref="MaxLiveCheckOutcome.RefusalReason"/> set, both flags false). Mirrors
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveTokenCheck</c>'s own shape exactly - see that class's
/// own remarks for the fuller reasoning this file does not repeat; the two differences worth naming here
/// are that this wraps <see cref="MaxApiClient.GetMeAsync"/> instead, and that this is a genuinely new
/// second call site (<c>MaxChannelEndpoints.HandleConnectAsync</c>'s own best-effort <c>GetMeAsync</c>
/// call stays exactly as it was - unbound, one-off, untouched by this file).
///
/// <para><b>Why <see cref="Timeout"/> is 5 seconds, not an invented number.</b> The identical value
/// <c>TelegramLiveTokenCheck.Timeout</c> uses, for the identical reason: matched to
/// <c>ChatModule.ConfigureChannelResilienceDefaults</c>'s own boundary - an HTTP call to a third-party
/// provider this codebase does not control, with a tenant waiting on a page load, not a background job's
/// own recurring charge.</para>
/// </summary>
public static class MaxLiveTokenCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<MaxLiveCheckOutcome> RunAsync(
        MaxApiClient client, string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var result = await client.GetMeAsync(token, linkedCts.Token);
            return result.Ok
                ? MaxLiveCheckOutcome.Verified(result.Username)
                : MaxLiveCheckOutcome.Refused(result.RefusalReason!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // linkedCts fired because timeoutCts did, not because the caller's own request was
            // aborted (that case is left to propagate normally, exactly as an unbounded call would).
            return MaxLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            // MAX itself, or this deployment's own outbound path, did not answer at all - the identical
            // transient case MaxApiClient.GetMeAsync's own remarks already describe, just caught here
            // instead of left to propagate, because this caller has a distinct "unreachable" state to
            // report rather than an unhandled exception.
            return MaxLiveCheckOutcome.ProviderUnreachable();
        }
    }
}

/// <summary>Exactly three shapes, never a fourth: <see cref="Verified"/> (the token works),
/// <see cref="Refused"/> (the provider looked at the token and said no - <see cref="RefusalReason"/>
/// is always set), <see cref="ProviderUnreachable()"/> (the provider was never actually asked in time,
/// or the call could not complete at all - <see cref="RefusalReason"/> is always
/// <see langword="null"/>, because nothing about the token itself is known). Mirrors
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveCheckOutcome</c> one for one.
///
/// <para>The static factory is named <c>ProviderUnreachable</c>, not <c>Unreachable</c>, purely to
/// avoid colliding with the <see cref="Unreachable"/> property this record's own positional parameter
/// already generates (CS0102) - the property is what every caller outside this file actually reads.</para>
///
/// <para><see cref="Username"/> rides along on <see cref="Verified"/> only - a refused or unreachable
/// check has nothing to say about the bot's own handle.</para>
/// </summary>
public sealed record MaxLiveCheckOutcome(bool Ok, bool Unreachable, string? RefusalReason, string? Username = null)
{
    public static MaxLiveCheckOutcome Verified(string? username) => new(true, false, null, username);

    public static MaxLiveCheckOutcome Refused(string reason) => new(false, false, reason);

    public static MaxLiveCheckOutcome ProviderUnreachable() => new(false, true, null);
}
