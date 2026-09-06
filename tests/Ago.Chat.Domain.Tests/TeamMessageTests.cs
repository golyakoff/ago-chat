namespace Ago.Chat.Domain.Tests;

/// <summary>`23-33`: <see cref="TeamMessage.Remove"/> - the tombstone transition. Everything else
/// about <see cref="TeamMessage"/> is exercised at the Application/Integration level (its own remarks
/// explain why there is no in-memory aggregate to unit test in isolation); this method is the one
/// piece of real domain logic `23-33` adds.</summary>
public class TeamMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId AuthorId = new(Guid.NewGuid());

    private static TeamMessage CreateMessage() => new(
        new TeamMessageId(Guid.NewGuid()), SiteId, AuthorId, authorIsAdmin: false, new MessageBody("hello team"),
        sequence: 1, clientMessageId: null, Now);

    [Fact]
    public void RemovedAt_IsNull_ForAMessageNobodyHasRemoved()
    {
        var message = CreateMessage();

        Assert.Null(message.RemovedAt);
    }

    [Fact]
    public void Remove_SetsRemovedAt()
    {
        var message = CreateMessage();
        var removedAt = Now.AddMinutes(5);

        message.Remove(removedAt);

        Assert.Equal(removedAt, message.RemovedAt);
    }

    [Fact]
    public void Remove_WhenAlreadyRemoved_Throws()
    {
        var message = CreateMessage();
        message.Remove(Now.AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() => message.Remove(Now.AddMinutes(10)));
    }

    [Fact]
    public void Remove_DoesNotChangeTheBody()
    {
        // `23-33`: removal is a read-side redaction (TeamMessageReadStore), never a write to the
        // stored text - Ago.Chat.Domain.TeamMessage.RemovedAt's own remarks explain why. Pinning this
        // here catches a future change that tried to scrub Body on this domain type instead.
        var message = CreateMessage();

        message.Remove(Now.AddMinutes(5));

        Assert.Equal("hello team", message.Body.Value);
    }
}
