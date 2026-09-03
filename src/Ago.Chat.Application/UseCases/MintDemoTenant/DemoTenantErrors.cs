using System.Globalization;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.MintDemoTenant;

/// <summary>
/// `8-07`: this use case's own expected failures. Its own class rather than four more members on
/// <c>ConversationErrors</c>, which is already the catch-all for six unrelated features - and none of
/// these is about a conversation.
/// </summary>
public static class DemoTenantErrors
{
    // `ago-root#347`: the exact marker `RateLimited` writes into its message and
    // `TryGetRateLimitedRetryAfterSeconds` reads back out of it - see that method's own remarks for why
    // this round trip exists at all instead of a structured field on `Error`.
    private const string RetryAfterMarker = "Retry after ";

    public static Error Disabled() =>
        new("demo.disabled", "Demo tenants are not enabled on this deployment.");

    /// <summary>The total cap. Names the number, because a viewer turned away deserves to know this is
    /// a limit rather than a fault - and because an operator reading the logs needs to know which of
    /// the two guards fired.</summary>
    public static Error CapacityReached(int maxLiveTenants) =>
        new("demo.capacity_reached",
            $"The demo is at its limit of {maxLiveTenants} simultaneous tenants. Try again shortly - each one expires on its own.");

    public static Error RateLimited(TimeSpan retryAfter) =>
        new("demo.rate_limited",
            $"Too many demo tenants requested from this address. {RetryAfterMarker}{retryAfter.TotalSeconds:0}s.");

    /// <summary>
    /// Recovers the whole seconds <see cref="RateLimited"/> just wrote into its own message, for
    /// <c>DemoEndpoints</c>'s <c>Retry-After</c> header (`ago-root#347`).
    ///
    /// <para><b>Why a round trip through prose rather than a structured field:</b>
    /// <see cref="Ago.Platform.Kernel.Error"/> is <c>(Code, Message)</c> and nothing else - every use
    /// case in this codebase returns failures through it, and widening it lives in `ago-platform`, out
    /// of scope for an ago-chat-only fix. The alternative this class's own sibling ports use for a
    /// number an HTTP layer needs untouched - `AuthEndpoints.HandleVisitorSessionAsync` computing its
    /// 429 by hand, outside <see cref="Result{T}"/> entirely - would mean checking this endpoint's per-IP
    /// limit a second time at the HTTP layer, and a token-bucket check that is ever allowed to run twice
    /// per request would silently halve the configured limit on every successful mint. Reading the
    /// number back out of the message this method's own producer just built is therefore the smaller
    /// risk: the two are colocated, and <c>DemoTenantErrorsTests</c> proves the round trip, so a wording
    /// change to <see cref="RateLimited"/> fails that test instead of silently dropping the header.</para>
    ///
    /// <para>Returns <see langword="null"/> for any other code, or if the message does not carry the
    /// marker this class itself controls - the caller falls back to no header rather than guessing.</para>
    /// </summary>
    public static int? TryGetRateLimitedRetryAfterSeconds(Error error)
    {
        if (error.Code != "demo.rate_limited")
        {
            return null;
        }

        var start = error.Message.IndexOf(RetryAfterMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += RetryAfterMarker.Length;
        var end = error.Message.IndexOf('s', start);
        if (end < 0)
        {
            return null;
        }

        return int.TryParse(
            error.Message.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    public static Error Unavailable() =>
        new("demo.unavailable", "Could not mint a demo tenant. Try again.");

    /// <summary>The identity provider refused - a real outcome the caller can act on (try again),
    /// distinct from it being unreachable, which throws and is the resilience pipeline's business.</summary>
    public static Error IdentityRejected(string reason) =>
        new("demo.identity_rejected", $"The identity provider refused the demo account: {reason}");
}
