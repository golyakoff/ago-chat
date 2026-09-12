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

    /// <summary>`25-56` decision 5: a freshly-constructed visitor has no pair until something assigns
    /// one - matching the "assigned once at first contact" framing, not "assigned by construction".
    /// </summary>
    [Fact]
    public void Constructor_LeavesEmojiPairNull()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);

        Assert.Null(visitor.EmojiCreature);
        Assert.Null(visitor.EmojiFood);
    }

    [Fact]
    public void AssignEmojiPair_StoresBothValues()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);

        visitor.AssignEmojiPair("🐔", "🍊");

        Assert.Equal("🐔", visitor.EmojiCreature);
        Assert.Equal("🍊", visitor.EmojiFood);
    }

    /// <summary>The invariant decision 5 states in words - "permanent for that visitor" - enforced by
    /// throwing on a second call rather than silently overwriting or silently no-op'ing either of
    /// which would hide a caller bug that violates the one-time assignment this type exists to
    /// guarantee.</summary>
    [Fact]
    public void AssignEmojiPair_CalledASecondTime_Throws()
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);
        visitor.AssignEmojiPair("🐔", "🍊");

        Assert.Throws<InvalidOperationException>(() => visitor.AssignEmojiPair("🐠", "🥝"));

        // Never reassigned - the failed second call left the original pair untouched.
        Assert.Equal("🐔", visitor.EmojiCreature);
        Assert.Equal("🍊", visitor.EmojiFood);
    }

    // ArgumentNullException/ArgumentException, per ArgumentException.ThrowIfNullOrWhiteSpace's own
    // contract for a null vs. an empty/whitespace value - both are ArgumentException, which is the
    // invariant this test actually cares about (a missing half is refused, whichever way it is
    // missing), so the assertion is against the common base rather than either concrete type.
    [Theory]
    [InlineData(null, "🍊")]
    [InlineData("", "🍊")]
    [InlineData("🐔", null)]
    [InlineData("🐔", "")]
    public void AssignEmojiPair_WithAMissingHalf_Throws(string? creature, string? food)
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), FirstContact);

        Assert.ThrowsAny<ArgumentException>(() => visitor.AssignEmojiPair(creature!, food!));
    }
}
