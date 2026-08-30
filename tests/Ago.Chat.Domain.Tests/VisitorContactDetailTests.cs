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
}
