namespace Ago.Chat.Domain.Tests;

/// <summary>`23-07`: the pure resolver behind the install screen's own funnel advice - see
/// <see cref="WidgetFunnelAdviceResolver"/>'s own remarks for the reasoning each case below
/// exercises.</summary>
public class WidgetFunnelAdviceResolverTests
{
    [Fact]
    public void Resolve_WithZeroLoads_ReturnsFixInstall() =>
        Assert.Equal(
            WidgetFunnelAdvice.FixInstall,
            WidgetFunnelAdviceResolver.Resolve(SiteInstallationState.NotSeenYet, loads: 0, opens: 0, conversations: 0));

    [Fact]
    public void Resolve_WithLoadsButNoOpens_ReturnsImprovePlacement() =>
        Assert.Equal(
            WidgetFunnelAdvice.ImprovePlacement,
            WidgetFunnelAdviceResolver.Resolve(SiteInstallationState.SeenAndQuiet, loads: 40, opens: 0, conversations: 0));

    [Fact]
    public void Resolve_WithOpensButNoConversations_ReturnsConnectChannelsAndRespond() =>
        Assert.Equal(
            WidgetFunnelAdvice.ConnectChannelsAndRespond,
            WidgetFunnelAdviceResolver.Resolve(SiteInstallationState.SeenAndQuiet, loads: 40, opens: 12, conversations: 0));

    [Fact]
    public void Resolve_WithEveryStageHavingTraffic_ReturnsNone() =>
        Assert.Equal(
            WidgetFunnelAdvice.None,
            WidgetFunnelAdviceResolver.Resolve(SiteInstallationState.SeenAndQuiet, loads: 40, opens: 12, conversations: 3));

    /// <summary>`23-06`'s fourth state wins outright, even though every count reads zero the same way
    /// a broken install would - the item's own words: "None of the above applies, and the install
    /// advice must not be produced."</summary>
    [Fact]
    public void Resolve_WhenNeverSeenButInUse_ReturnsNone_NeverFixInstall() =>
        Assert.Equal(
            WidgetFunnelAdvice.None,
            WidgetFunnelAdviceResolver.Resolve(SiteInstallationState.NeverSeenButInUse, loads: 0, opens: 0, conversations: 0));

    /// <summary>Order matters: a site with zero loads necessarily has zero opens too (an open beacon
    /// cannot fire without a load beacon having fired first), and the resolver must not report the
    /// second-stage advice for what is really a first-stage problem.</summary>
    [Fact]
    public void Resolve_WithZeroLoadsAndZeroOpens_PrefersFixInstallOverImprovePlacement() =>
        Assert.Equal(
            WidgetFunnelAdvice.FixInstall,
            WidgetFunnelAdviceResolver.Resolve(SiteInstallationState.NotSeenYet, loads: 0, opens: 0, conversations: 0));

    [Theory]
    [InlineData(WidgetFunnelAdvice.None)]
    [InlineData(WidgetFunnelAdvice.FixInstall)]
    [InlineData(WidgetFunnelAdvice.ImprovePlacement)]
    [InlineData(WidgetFunnelAdvice.ConnectChannelsAndRespond)]
    public void IsReachableForTier_IsTrueForEveryAdviceOnEveryTierToday(WidgetFunnelAdvice advice)
    {
        Assert.True(WidgetFunnelAdviceResolver.IsReachableForTier(advice, "free"));
        Assert.True(WidgetFunnelAdviceResolver.IsReachableForTier(advice, "starter"));
        Assert.True(WidgetFunnelAdviceResolver.IsReachableForTier(advice, "growth"));
    }
}
