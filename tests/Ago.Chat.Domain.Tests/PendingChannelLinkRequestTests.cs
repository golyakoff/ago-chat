namespace Ago.Chat.Domain.Tests;

public class PendingChannelLinkRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private static PendingChannelLinkRequest Request(
        TimeSpan? validFor = null, OperatorId? requestedBy = null) =>
        PendingChannelLinkRequest.Request(
            new PendingChannelLinkRequestId(Guid.NewGuid()), SiteId, VisitorId, ChannelKind.Telegram,
            [1, 2, 3], requestedBy, Now, validFor ?? TimeSpan.FromMinutes(15));

    [Fact]
    public void Request_StartsUnconsumed()
    {
        var request = Request();

        Assert.Null(request.ConsumedAt);
    }

    [Fact]
    public void Request_SetsExpiresAtToNowPlusValidFor()
    {
        var request = Request(TimeSpan.FromMinutes(15));

        Assert.Equal(Now + TimeSpan.FromMinutes(15), request.ExpiresAt);
    }

    /// <summary>`adr/0079` decision 2: <see langword="null"/> for a visitor-initiated request.</summary>
    [Fact]
    public void Request_WithNoRequestedByOperatorId_LeavesItNull()
    {
        var request = Request(requestedBy: null);

        Assert.Null(request.RequestedByOperatorId);
    }

    [Fact]
    public void Request_WithARequestedByOperatorId_StoresIt()
    {
        var operatorId = new OperatorId(Guid.NewGuid());

        var request = Request(requestedBy: operatorId);

        Assert.Equal(operatorId, request.RequestedByOperatorId);
    }

    [Fact]
    public void IsLive_BeforeExpiryAndUnconsumed_IsTrue()
    {
        var request = Request(TimeSpan.FromMinutes(15));

        Assert.True(request.IsLive(Now.AddMinutes(1)));
    }

    /// <summary>`>=`, not `>` - the same boundary contract `OperatorInvite.IsExpired` already uses,
    /// mirrored here.</summary>
    [Fact]
    public void IsLive_AtExactlyExpiresAt_IsFalse()
    {
        var request = Request(TimeSpan.FromMinutes(15));

        Assert.False(request.IsLive(request.ExpiresAt));
    }

    [Fact]
    public void IsLive_AfterConsume_IsFalse()
    {
        var request = Request(TimeSpan.FromMinutes(15));

        request.Consume(Now.AddMinutes(1));

        Assert.False(request.IsLive(Now.AddMinutes(2)));
    }

    [Fact]
    public void Consume_MarksConsumedAt()
    {
        var request = Request(TimeSpan.FromMinutes(15));
        var consumedAt = Now.AddMinutes(5);

        request.Consume(consumedAt);

        Assert.Equal(consumedAt, request.ConsumedAt);
    }

    [Fact]
    public void Consume_WhenAlreadyConsumed_Throws()
    {
        var request = Request(TimeSpan.FromMinutes(15));
        request.Consume(Now.AddMinutes(1));

        Assert.Throws<InvalidPendingChannelLinkRequestStateException>(() => request.Consume(Now.AddMinutes(2)));
    }

    [Fact]
    public void Consume_WhenExpired_Throws()
    {
        var request = Request(TimeSpan.FromMinutes(15));

        Assert.Throws<InvalidPendingChannelLinkRequestStateException>(() => request.Consume(request.ExpiresAt));
    }
}
