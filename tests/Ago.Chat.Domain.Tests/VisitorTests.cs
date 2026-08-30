namespace Ago.Chat.Domain.Tests;

public class VisitorTests
{
    private static readonly DateTimeOffset FirstContact = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_SetsFirstSeenAndLastSeenToTheSameInstant()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);

        Assert.Equal(FirstContact, visitor.FirstSeenAt);
        Assert.Equal(FirstContact, visitor.LastSeenAt);
    }

    [Fact]
    public void Touch_UpdatesLastSeenAt_ButNeverFirstSeenAt()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);
        var returnVisit = FirstContact.AddDays(3);

        visitor.Touch(returnVisit);

        Assert.Equal(FirstContact, visitor.FirstSeenAt);
        Assert.Equal(returnVisit, visitor.LastSeenAt);
    }

    /// <summary>`14-13`: a freshly-constructed visitor has no preference - today's implicit
    /// "most-recently-seen channel" rule still applies until an operator sets one.</summary>
    [Fact]
    public void Constructor_LeavesPreferredChannelIdentityIdNull()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);

        Assert.Null(visitor.PreferredChannelIdentityId);
    }

    [Fact]
    public void SetPreferredChannelIdentity_StoresTheGivenId()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);
        var channelIdentityId = new ChannelIdentityId(Guid.NewGuid());

        visitor.SetPreferredChannelIdentity(channelIdentityId);

        Assert.Equal(channelIdentityId, visitor.PreferredChannelIdentityId);
    }

    /// <summary>The explicit "back to automatic" path - passing <see langword="null"/> clears a
    /// previously-set preference rather than being refused as a no-op.</summary>
    [Fact]
    public void SetPreferredChannelIdentity_WithNull_ClearsAPreviouslySetPreference()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);
        visitor.SetPreferredChannelIdentity(new ChannelIdentityId(Guid.NewGuid()));

        visitor.SetPreferredChannelIdentity(null);

        Assert.Null(visitor.PreferredChannelIdentityId);
    }
}
