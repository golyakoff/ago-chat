namespace Ago.Chat.Domain.Tests;

/// <summary>`14-14`: a pure factory method with no clock, no database and nothing to fake
/// (testing.md's domain-unit level), the same shape <see cref="ConversationNoteTests"/> uses.</summary>
public class VisitorContactDetailTests
{
    private static readonly VisitorContactDetailId Id = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_WithAnEmptyValue_Throws(string value) =>
        Assert.Throws<ArgumentException>(
            () => VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, value, OperatorId, Now));

    [Fact]
    public void Record_WithAnOversizedValue_Throws() =>
        Assert.Throws<ArgumentException>(() => VisitorContactDetail.Record(
            Id, VisitorId, VisitorContactDetailKind.Phone, new string('9', VisitorContactDetail.MaxValueLength + 1),
            OperatorId, Now));

    [Fact]
    public void Record_WhenValid_SetsProperties()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);

        Assert.Equal(Id, detail.Id);
        Assert.Equal(VisitorId, detail.VisitorId);
        Assert.Equal(VisitorContactDetailKind.Phone, detail.Kind);
        Assert.Equal("+1 555 0100", detail.Value);
        Assert.Equal(OperatorId, detail.RecordedByOperatorId);
        Assert.Equal(Now, detail.RecordedAt);
    }

    [Fact]
    public void Record_TrimsValue() =>
        Assert.Equal(
            "+1 555 0100",
            VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "  +1 555 0100  ", OperatorId, Now).Value);

    [Fact]
    public void Record_AcceptsEveryClosedKindMember()
    {
        foreach (var kind in Enum.GetValues<VisitorContactDetailKind>())
        {
            var detail = VisitorContactDetail.Record(Id, VisitorId, kind, "a recorded fact", OperatorId, Now);
            Assert.Equal(kind, detail.Kind);
        }
    }

    [Fact]
    public void Record_SetsSourceOperator_AndVerifiedFalse()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);

        Assert.Equal(VisitorContactDetailSource.Operator, detail.Source);
        Assert.False(detail.Verified);
    }

    // ------------------------------------------------------------------------------------------
    // `23-09`: RecordFromVisitor - no OperatorId parameter exists at all, by construction.
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordFromVisitor_WithAnEmptyValue_Throws(string value) =>
        Assert.Throws<ArgumentException>(
            () => VisitorContactDetail.RecordFromVisitor(Id, VisitorId, VisitorContactDetailKind.Phone, value, Now));

    [Fact]
    public void RecordFromVisitor_WithAnOversizedValue_Throws() =>
        Assert.Throws<ArgumentException>(() => VisitorContactDetail.RecordFromVisitor(
            Id, VisitorId, VisitorContactDetailKind.Phone, new string('9', VisitorContactDetail.MaxValueLength + 1), Now));

    [Fact]
    public void RecordFromVisitor_WhenValid_SetsPropertiesWithNoOperatorAndSourceVisitorAndUnverified()
    {
        var detail = VisitorContactDetail.RecordFromVisitor(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0177", Now);

        Assert.Equal(Id, detail.Id);
        Assert.Equal(VisitorId, detail.VisitorId);
        Assert.Equal(VisitorContactDetailKind.Phone, detail.Kind);
        Assert.Equal("+1 555 0177", detail.Value);
        Assert.Null(detail.RecordedByOperatorId);
        Assert.Equal(VisitorContactDetailSource.Visitor, detail.Source);
        Assert.False(detail.Verified);
        Assert.Equal(Now, detail.RecordedAt);
    }

    [Fact]
    public void RecordFromVisitor_TrimsValue() =>
        Assert.Equal(
            "+1 555 0177",
            VisitorContactDetail.RecordFromVisitor(Id, VisitorId, VisitorContactDetailKind.Phone, "  +1 555 0177  ", Now).Value);

    [Fact]
    public void RecordFromVisitor_AcceptsEveryClosedKindMember()
    {
        foreach (var kind in Enum.GetValues<VisitorContactDetailKind>())
        {
            var detail = VisitorContactDetail.RecordFromVisitor(Id, VisitorId, kind, "a recorded fact", Now);
            Assert.Equal(kind, detail.Kind);
        }
    }
}
