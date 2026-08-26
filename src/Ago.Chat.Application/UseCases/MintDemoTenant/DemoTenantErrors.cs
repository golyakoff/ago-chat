using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.MintDemoTenant;

/// <summary>
/// `8-07`: this use case's own expected failures. Its own class rather than four more members on
/// <c>ConversationErrors</c>, which is already the catch-all for six unrelated features - and none of
/// these is about a conversation.
/// </summary>
public static class DemoTenantErrors
{
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
            $"Too many demo tenants requested from this address. Retry after {retryAfter.TotalSeconds:0}s.");

    public static Error Unavailable() =>
        new("demo.unavailable", "Could not mint a demo tenant. Try again.");

    /// <summary>The identity provider refused - a real outcome the caller can act on (try again),
    /// distinct from it being unreachable, which throws and is the resilience pipeline's business.</summary>
    public static Error IdentityRejected(string reason) =>
        new("demo.identity_rejected", $"The identity provider refused the demo account: {reason}");
}
