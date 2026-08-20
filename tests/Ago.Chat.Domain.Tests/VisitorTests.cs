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
}
