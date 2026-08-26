using Ago.Chat.Domain;

namespace Ago.Chat.Domain.Tests;

public class ChannelIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Link_BindsTheAddressToTheGivenVisitor()
    {
        var visitorId = new VisitorId(Guid.NewGuid());

        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), ChannelKind.Sms,
            new ExternalChannelAddress("+70000000000"), visitorId, Now);

        Assert.Equal(visitorId, identity.VisitorId);
        Assert.Equal(ChannelKind.Sms, identity.Kind);
        Assert.Equal("+70000000000", identity.Address.Value);
        Assert.Equal(Now, identity.FirstSeenAt);
        Assert.Equal(Now, identity.LastSeenAt);
    }

    [Fact]
    public void Touch_MovesLastSeenButNeverFirstSeen()
    {
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), ChannelKind.Max,
            new ExternalChannelAddress("max-user-1"), new VisitorId(Guid.NewGuid()), Now);

        identity.Touch(Now.AddHours(3));

        Assert.Equal(Now, identity.FirstSeenAt);
        Assert.Equal(Now.AddHours(3), identity.LastSeenAt);
    }

    /// <summary>
    /// `adr/0055`'s identity decision, made mechanical: one <see cref="Visitor"/> may hold several
    /// channel identities at once - the same person messaging by MAX and by SMS - which is the fact
    /// that ruled out embedding this as a value object on <see cref="Visitor"/>. Nothing in the type
    /// resists it; the resistance is entirely to *inferring* it, which is <see cref="ReceiveChannelMessageHandler"/>'s
    /// side of the story (Ago.Chat.Application.Tests).
    /// </summary>
    [Fact]
    public void OneVisitor_CanHoldSeveralChannelIdentitiesAtOnce()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        var bySms = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms,
            new ExternalChannelAddress("+70000000000"), visitorId, Now);
        var byMax = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Max,
            new ExternalChannelAddress("max-user-1"), visitorId, Now);

        Assert.NotEqual(bySms.Id, byMax.Id);
        Assert.Equal(bySms.VisitorId, byMax.VisitorId);
    }
}

public class ExternalChannelAddressTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_EmptyOrWhitespace(string value) =>
        Assert.Throws<ArgumentException>(() => new ExternalChannelAddress(value));

    [Fact]
    public void Rejects_TooLong() =>
        Assert.Throws<ArgumentException>(
            () => new ExternalChannelAddress(new string('9', ExternalChannelAddress.MaxLength + 1)));

    [Fact]
    public void Trims_SurroundingWhitespace() =>
        Assert.Equal("+70000000000", new ExternalChannelAddress("  +70000000000  ").Value);

    /// <summary>
    /// The refusal this type is built around: no E.164 rewriting, no case folding, no per-channel
    /// canonicalisation of any kind. Two spellings of one phone number stay two addresses here, and
    /// the concrete adapter (`14-02`/`14-03`) is what must normalise before constructing this - see
    /// the type's own remarks for why guessing a provider's format rules in Domain is the worse
    /// failure.
    /// </summary>
    [Fact]
    public void DoesNot_CanonicaliseChannelSpecificFormats()
    {
        Assert.NotEqual(
            new ExternalChannelAddress("+7 000 000-00-00"),
            new ExternalChannelAddress("+70000000000"));
        Assert.NotEqual(
            new ExternalChannelAddress("AbC"),
            new ExternalChannelAddress("abc"));
    }
}
