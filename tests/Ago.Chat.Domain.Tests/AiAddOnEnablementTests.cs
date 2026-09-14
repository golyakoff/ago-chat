namespace Ago.Chat.Domain.Tests;

/// <summary>`25-04`: the cut-off's own invariants, at the level they are actually decided - the
/// aggregate. Every one of these is a rule the two AI call sites rely on without re-deriving.</summary>
public sealed class AiAddOnEnablementTests
{
    private static readonly SiteId Site = new(Guid.NewGuid());
    private static readonly OperatorId Actor = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Decision 2: off by default, for everyone, always - a freshly created row covers
    /// nothing at all.</summary>
    [Fact]
    public void ANewEnablement_IsOff_AndCoversNothing()
    {
        var enablement = AiAddOnEnablement.ForSite(Site);

        Assert.False(enablement.IsEnabled);
        Assert.Null(enablement.EffectiveFrom);
    }

    /// <summary>Decision 6: the cut-off is the moment of enabling, exactly - the instant
    /// `AiProcessingGate` compares every conversation's own creation against.</summary>
    [Fact]
    public void Enable_MakesTheCutOffTheInstantOfEnabling()
    {
        var enablement = AiAddOnEnablement.ForSite(Site);
        enablement.Enable(Actor, "ai-processing-addendum", "v1", Now);

        Assert.True(enablement.IsEnabled);
        Assert.Equal(Now, enablement.EffectiveFrom);
    }

    /// <summary>Point 6 of the agreement: from the moment of disabling, transmission stops. This is the
    /// half this aggregate owns - a disabled row yields <b>no cut-off at all</b>, so a caller cannot
    /// compare a conversation against a stale one and conclude it is still covered.</summary>
    [Fact]
    public void Disable_LeavesNoCutOffToCompareAgainst()
    {
        var enablement = AiAddOnEnablement.ForSite(Site);
        enablement.Enable(Actor, "ai-processing-addendum", "v1", Now);
        Assert.Equal(Now, enablement.EffectiveFrom);

        enablement.Disable(Actor, Now.AddHours(2));

        Assert.False(enablement.IsEnabled);
        Assert.Null(enablement.EffectiveFrom);
    }

    /// <summary>The history survives a disable - this is what lets an audit answer "when was this last
    /// on", and it is why <c>EnabledAt</c> and <c>IsEnabled</c> are two fields rather than one nullable
    /// one.</summary>
    [Fact]
    public void Disable_KeepsTheEnablingHistory()
    {
        var enablement = AiAddOnEnablement.ForSite(Site);
        enablement.Enable(Actor, "ai-processing-addendum", "v1", Now);
        enablement.Disable(Actor, Now.AddHours(2));

        Assert.Equal(Now, enablement.EnabledAt);
        Assert.Equal(Actor, enablement.EnabledBy);
        Assert.Equal("v1", enablement.AcceptedDocumentVersion);
        Assert.Equal(Now.AddHours(2), enablement.DisabledAt);
        Assert.Equal(Actor, enablement.DisabledBy);
    }

    /// <summary>Re-enabling moves the cut-off forward, so conversations created while the add-on was off
    /// are never swept up by the re-enable - the conservative direction decision 6 argues for.</summary>
    [Fact]
    public void ReEnabling_MovesTheCutOffForward_AndNeverCoversTheOffPeriod()
    {
        var enablement = AiAddOnEnablement.ForSite(Site);
        enablement.Enable(Actor, "ai-processing-addendum", "v1", Now);
        enablement.Disable(Actor, Now.AddHours(1));
        enablement.Enable(Actor, "ai-processing-addendum", "v2", Now.AddHours(3));

        // The cut-off moved forward past the whole off period, so nothing created during it is covered.
        Assert.Equal(Now.AddHours(3), enablement.EffectiveFrom);
        Assert.Equal("v2", enablement.AcceptedDocumentVersion);
    }

    [Fact]
    public void Enable_RefusesABlankAcceptedVersion()
    {
        var enablement = AiAddOnEnablement.ForSite(Site);

        Assert.Throws<ArgumentException>(() => enablement.Enable(Actor, "ai-processing-addendum", "  ", Now));
        Assert.False(enablement.IsEnabled);
    }
}
