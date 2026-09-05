namespace Ago.Chat.Domain.Tests;

/// <summary>`23-06`: the pure resolver behind the install screen's one headline state - see
/// <see cref="SiteInstallationStateResolver"/>'s own remarks for the reasoning each case below
/// exercises.</summary>
public class SiteInstallationStateResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolve_WithNothingRecordedAndNoRecentUse_ReturnsNotSeenYet() =>
        Assert.Equal(
            SiteInstallationState.NotSeenYet,
            SiteInstallationStateResolver.Resolve(lastSeenAt: null, lastRefusedOrigin: null, lastRefusedOriginAt: null, usedRecently: false));

    [Fact]
    public void Resolve_WithALastSeenTimestampAndNoRefusal_ReturnsSeenAndQuiet() =>
        Assert.Equal(
            SiteInstallationState.SeenAndQuiet,
            SiteInstallationStateResolver.Resolve(
                lastSeenAt: Now.AddDays(-3), lastRefusedOrigin: null, lastRefusedOriginAt: null, usedRecently: false));

    /// <summary>The classic `www.` vs. bare-domain case: never seen, but a refusal is on record - this
    /// must not read as <see cref="SiteInstallationState.NotSeenYet"/>.</summary>
    [Fact]
    public void Resolve_WhenNeverSeenButARefusalExists_ReturnsEveryRequestRefused() =>
        Assert.Equal(
            SiteInstallationState.EveryRequestRefused,
            SiteInstallationStateResolver.Resolve(
                lastSeenAt: null, lastRefusedOrigin: "https://www.tenant.example", lastRefusedOriginAt: Now, usedRecently: false));

    /// <summary>A refusal newer than the most recent success means the widget is *currently* blocked,
    /// even though it once worked.</summary>
    [Fact]
    public void Resolve_WhenTheRefusalIsNewerThanTheLastSighting_ReturnsEveryRequestRefused() =>
        Assert.Equal(
            SiteInstallationState.EveryRequestRefused,
            SiteInstallationStateResolver.Resolve(
                lastSeenAt: Now.AddDays(-10),
                lastRefusedOrigin: "https://www.tenant.example",
                lastRefusedOriginAt: Now.AddDays(-1),
                usedRecently: false));

    /// <summary>A refusal *older* than the most recent success is history, not a current problem - the
    /// tenant fixed it, and must not be told they are still broken.</summary>
    [Fact]
    public void Resolve_WhenTheRefusalIsOlderThanTheLastSighting_ReturnsSeenAndQuiet() =>
        Assert.Equal(
            SiteInstallationState.SeenAndQuiet,
            SiteInstallationStateResolver.Resolve(
                lastSeenAt: Now.AddDays(-1),
                lastRefusedOrigin: "https://www.tenant.example",
                lastRefusedOriginAt: Now.AddDays(-10),
                usedRecently: false));

    /// <summary>`decisions.md`'s two-facts amendment, at the resolver level: never seen, but the
    /// product is being used over a channel - must not read as <see cref="SiteInstallationState.NotSeenYet"/>.
    /// </summary>
    [Fact]
    public void Resolve_WhenNeverSeenButUsedRecently_ReturnsNeverSeenButInUse() =>
        Assert.Equal(
            SiteInstallationState.NeverSeenButInUse,
            SiteInstallationStateResolver.Resolve(lastSeenAt: null, lastRefusedOrigin: null, lastRefusedOriginAt: null, usedRecently: true));

    /// <summary>A concrete refusal is a more actionable finding than channel-only reassurance - see
    /// <see cref="SiteInstallationStateResolver"/>'s own "why a refusal can win even over a tenant
    /// currently in use".</summary>
    [Fact]
    public void Resolve_WhenNeverSeenWithBothARefusalAndRecentUse_PrefersEveryRequestRefused() =>
        Assert.Equal(
            SiteInstallationState.EveryRequestRefused,
            SiteInstallationStateResolver.Resolve(
                lastSeenAt: null, lastRefusedOrigin: "https://www.tenant.example", lastRefusedOriginAt: Now, usedRecently: true));

    /// <summary>Mutation-proving case, restated as a named test rather than left implicit: collapsing
    /// the two facts (widget-seen, product-used) into a single reading would make this case
    /// indistinguishable from <see cref="Resolve_WithNothingRecordedAndNoRecentUse_ReturnsNotSeenYet"/>
    /// above. The two must diverge, or "two facts, not one" has silently become one fact again.
    /// </summary>
    [Fact]
    public void Resolve_NeverSeenButInUseAndNotSeenYet_AreDifferentStates() =>
        Assert.NotEqual(
            SiteInstallationStateResolver.Resolve(lastSeenAt: null, lastRefusedOrigin: null, lastRefusedOriginAt: null, usedRecently: false),
            SiteInstallationStateResolver.Resolve(lastSeenAt: null, lastRefusedOrigin: null, lastRefusedOriginAt: null, usedRecently: true));
}
