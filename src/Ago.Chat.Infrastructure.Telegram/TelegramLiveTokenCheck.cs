namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `23-36`: bounds a live <c>getMe</c> check with a timeout, and classifies the result into exactly
/// three outcomes a caller can render distinctly - <see cref="TelegramLiveCheckOutcome.Verified"/>,
/// <see cref="TelegramLiveCheckOutcome.ProviderUnreachable"/> and a refusal
/// (<see cref="TelegramLiveCheckOutcome.RefusalReason"/> set, both flags false).
///
/// <para><b>Why a bound is needed at all.</b> <c>ChatModule</c> registers <see cref="TelegramApiClient"/>'s
/// own <c>HttpClient</c> with a base address, a SOCKS proxy handler and the token-redacting log/trace
/// handlers - and no <c>Timeout</c>, so it inherits <c>HttpClient</c>'s own 100-second default. That is
/// tolerable for <c>Ago.Chat.Api.Channels.TelegramChannelEndpoints.HandleConnectAsync</c>, a deliberate
/// action an operator just took once; it is not tolerable for a status <em>read</em>, which runs on
/// every load of the console's Telegram screen. This deployment reaches Telegram through a SOCKS5 relay
/// (<see cref="TelegramProxyOptions"/>), so "Telegram is unreachable" here includes "the proxy is down"
/// - a real state in which an unbound status read would hang the screen for up to a minute and a half
/// before rendering anything, which is exactly backwards for a screen whose whole point is telling the
/// tenant the truth quickly.</para>
///
/// <para><b>Why <see cref="Timeout"/> is 5 seconds, not an invented number.</b> Matched to
/// <c>ChatModule.ConfigureChannelResilienceDefaults</c>'s own <c>Timeout</c> - the closest existing
/// boundary of the same shape: an HTTP call to a third-party provider this codebase does not control,
/// with a real person waiting on the other end (there, an operator's reply; here, a tenant's page
/// load), rather than a background job's own recurring charge (which is where the longer 8-10 second
/// defaults on that same page belong instead).</para>
///
/// <para><b>Why this does not reuse that whole resilience pipeline.</b> <c>ConfigureChannelResilienceDefaults</c>
/// pairs its timeout with three retry attempts and a circuit breaker, wired around the *send* path via
/// <c>ResilientInboundChannelAdapter</c>. Retrying a status read the same way would make an unreachable
/// provider's page load slower, not more useful - a tenant watching the screen gets one honest "could
/// not reach Telegram just now" in a few seconds, not up to three attempts' worth of backoff first. A
/// bare timeout is the only piece of that pipeline this call needs.</para>
///
/// <para><b>Unreachable versus refused - two different facts, rendered differently on purpose.</b> A
/// timeout or a transient <see cref="HttpRequestException"/> (<see cref="TelegramApiClient.GetMeAsync"/>'s
/// own remarks on why a transient fault throws rather than returning a refusal) means this codebase
/// does not know whether the token is good - the honest answer is "try again", not "get a new token".
/// Collapsing the two into one <c>Verified: false</c> would tell a tenant with a perfectly good bot,
/// caught behind a momentary proxy hiccup, to go generate a new token for no reason.</para>
/// </summary>
public static class TelegramLiveTokenCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<TelegramLiveCheckOutcome> RunAsync(
        TelegramApiClient client, string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var result = await client.GetMeAsync(token, linkedCts.Token);
            return result.Ok ? TelegramLiveCheckOutcome.Verified() : TelegramLiveCheckOutcome.Refused(result.RefusalReason!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // linkedCts fired because timeoutCts did, not because the caller's own request was
            // aborted (that case is left to propagate normally, exactly as an unbounded call would).
            return TelegramLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            // Telegram itself, or this deployment's own outbound relay, did not answer at all - the
            // identical transient case TelegramApiClient.GetMeAsync's own remarks already describe,
            // just caught here instead of left to propagate, because this caller has a distinct
            // "unreachable" state to report rather than an unhandled exception to let a connect-time
            // rollback react to.
            return TelegramLiveCheckOutcome.ProviderUnreachable();
        }
    }
}

/// <summary>Exactly three shapes, never a fourth: <see cref="Verified()"/> (the token works),
/// <see cref="Refused"/> (the provider looked at the token and said no - <see cref="RefusalReason"/>
/// is always set), <see cref="ProviderUnreachable()"/> (the provider was never actually asked in time,
/// or the call could not complete at all - <see cref="RefusalReason"/> is always
/// <see langword="null"/>, because nothing about the token itself is known).
///
/// <para>The static factory is named <c>ProviderUnreachable</c>, not <c>Unreachable</c>, purely to
/// avoid colliding with the <see cref="Unreachable"/> property this record's own positional parameter
/// already generates (CS0102) - the property is what every caller outside this file actually reads.</para>
/// </summary>
public sealed record TelegramLiveCheckOutcome(bool Ok, bool Unreachable, string? RefusalReason)
{
    public static TelegramLiveCheckOutcome Verified() => new(true, false, null);

    public static TelegramLiveCheckOutcome Refused(string reason) => new(false, false, reason);

    public static TelegramLiveCheckOutcome ProviderUnreachable() => new(false, true, null);
}
