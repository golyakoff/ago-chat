namespace Ago.Chat.Domain.Tests;

/// <summary>`23-03`: a pure factory plus one guarded mutation, no clock, no database and nothing to
/// fake (testing.md's domain-unit level), the same shape <see cref="ConversationNoteTests"/> uses.</summary>
public class ConversationAssignmentIntervalTests
{
    private static readonly ConversationAssignmentId Id = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Open_SetsProperties_AndStartsWithNoEndedAt()
    {
        var interval = ConversationAssignmentInterval.Open(
            Id, SiteId, ConversationId, OperatorId, ConversationAssignmentSource.Assigned, Now);

        Assert.Equal(Id, interval.Id);
        Assert.Equal(SiteId, interval.SiteId);
        Assert.Equal(ConversationId, interval.ConversationId);
        Assert.Equal(OperatorId, interval.OperatorId);
        Assert.Equal(ConversationAssignmentSource.Assigned, interval.Source);
        Assert.Equal(Now, interval.StartedAt);
        Assert.Null(interval.EndedAt);
    }

    [Fact]
    public void Close_SetsEndedAt()
    {
        var interval = ConversationAssignmentInterval.Open(
            Id, SiteId, ConversationId, OperatorId, ConversationAssignmentSource.Assigned, Now);
        var endedAt = Now.AddMinutes(10);

        interval.Close(endedAt);

        Assert.Equal(endedAt, interval.EndedAt);
    }

    /// <summary>`23-03`'s own Scope: "Nothing else ever updates it." The one invariant this type has -
    /// enforced here, not merely by convention, so a caller that forgets a conversation already has a
    /// closed interval fails loudly rather than silently overwriting the real end instant.</summary>
    [Fact]
    public void Close_WhenAlreadyClosed_Throws()
    {
        var interval = ConversationAssignmentInterval.Open(
            Id, SiteId, ConversationId, OperatorId, ConversationAssignmentSource.Assigned, Now);
        interval.Close(Now.AddMinutes(10));

        Assert.Throws<InvalidOperationException>(() => interval.Close(Now.AddMinutes(20)));
    }
}
