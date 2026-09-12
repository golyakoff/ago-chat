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

    // ------------------------------------------------------------------------------------------
    // `25-58`: EditValue - real inline editing, never a rewrite of Source/RecordedByOperatorId.
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EditValue_WithAnEmptyValue_Throws(string value)
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);

        Assert.Throws<ArgumentException>(() => detail.EditValue(value));
    }

    [Fact]
    public void EditValue_WithAnOversizedValue_Throws()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);

        Assert.Throws<ArgumentException>(() => detail.EditValue(new string('9', VisitorContactDetail.MaxValueLength + 1)));
    }

    [Fact]
    public void EditValue_WhenValid_ReplacesValue_AndTrims()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);

        detail.EditValue("  +1 555 0199  ");

        Assert.Equal("+1 555 0199", detail.Value);
    }

    /// <summary>The backlog item's own explicit warning: editing changes the existing row, not the
    /// source. A visitor-submitted entry corrected by an operator must stay `Source.Visitor` - getting
    /// this backwards would misattribute a visitor's own data to an operator.</summary>
    [Fact]
    public void EditValue_NeverChangesSourceOrRecordedByOperatorId()
    {
        var operatorRecorded = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);
        operatorRecorded.EditValue("+1 555 0199");
        Assert.Equal(VisitorContactDetailSource.Operator, operatorRecorded.Source);
        Assert.Equal(OperatorId, operatorRecorded.RecordedByOperatorId);

        var visitorRecorded = VisitorContactDetail.RecordFromVisitor(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0177", Now);
        visitorRecorded.EditValue("+1 555 0188");
        Assert.Equal(VisitorContactDetailSource.Visitor, visitorRecorded.Source);
        Assert.Null(visitorRecorded.RecordedByOperatorId);
    }

    [Fact]
    public void EditValue_ResetsAnAlreadySetAssessmentBackToUnset()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);
        detail.SetAssessment(VisitorContactDetailAssessment.Confirmed);

        detail.EditValue("+1 555 0199");

        Assert.Equal(VisitorContactDetailAssessment.Unset, detail.Assessment);
    }

    // ------------------------------------------------------------------------------------------
    // `25-58`: SetAssessment - Phone/Email only, never Other.
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(VisitorContactDetailAssessment.Confirmed)]
    [InlineData(VisitorContactDetailAssessment.Invalid)]
    [InlineData(VisitorContactDetailAssessment.Unset)]
    public void SetAssessment_OnPhone_SetsAssessment(VisitorContactDetailAssessment assessment)
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);

        detail.SetAssessment(assessment);

        Assert.Equal(assessment, detail.Assessment);
    }

    [Fact]
    public void SetAssessment_OnEmail_SetsAssessment()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Email, "visitor@example.com", OperatorId, Now);

        detail.SetAssessment(VisitorContactDetailAssessment.Confirmed);

        Assert.Equal(VisitorContactDetailAssessment.Confirmed, detail.Assessment);
    }

    /// <summary>A name or a free-text note has no channel to confirm or invalidate the way a phone
    /// number or an email address does - the backlog item's own decision, enforced here as defence in
    /// depth behind the Application layer's own, earlier check.</summary>
    [Fact]
    public void SetAssessment_OnOther_Throws()
    {
        var detail = VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Other, "prefers to be called Alex", OperatorId, Now);

        Assert.Throws<InvalidVisitorContactDetailStateException>(
            () => detail.SetAssessment(VisitorContactDetailAssessment.Confirmed));
    }

    [Fact]
    public void Record_DefaultsAssessmentToUnset() =>
        Assert.Equal(
            VisitorContactDetailAssessment.Unset,
            VisitorContactDetail.Record(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now).Assessment);

    [Fact]
    public void RecordFromVisitor_DefaultsAssessmentToUnset() =>
        Assert.Equal(
            VisitorContactDetailAssessment.Unset,
            VisitorContactDetail.RecordFromVisitor(Id, VisitorId, VisitorContactDetailKind.Phone, "+1 555 0177", Now).Assessment);
}
