namespace Ago.Chat.Domain.Tests;

/// <summary>`24-01`: a pure factory method with no clock, no database and nothing to fake
/// (testing.md's domain-unit level), the same shape <see cref="ConversationNoteTests"/> uses.</summary>
public class AcceptanceRecordTests
{
    private static readonly AcceptanceRecordId Id = new(Guid.NewGuid());
    private static readonly SiteId TenantId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForTenant_WhenValid_SetsSubjectKindAndId()
    {
        var record = AcceptanceRecord.ForTenant(Id, TenantId, "privacy-policy", "v1", Now);

        Assert.Equal(AcceptanceSubjectKind.Tenant, record.SubjectKind);
        Assert.Equal(TenantId.Value, record.SubjectId);
        Assert.Equal("privacy-policy", record.DocumentKey);
        Assert.Equal("v1", record.DocumentVersion);
        Assert.Equal(Now, record.AcceptedAt);
        Assert.Null(record.ClientIp);
        Assert.Null(record.UserAgent);
    }

    [Fact]
    public void ForOperator_WhenValid_SetsSubjectKindAndId()
    {
        var record = AcceptanceRecord.ForOperator(Id, OperatorId, "terms-of-service", "v2", Now);

        Assert.Equal(AcceptanceSubjectKind.Operator, record.SubjectKind);
        Assert.Equal(OperatorId.Value, record.SubjectId);
    }

    [Fact]
    public void ForVisitor_WhenValid_SetsSubjectKindAndId()
    {
        var record = AcceptanceRecord.ForVisitor(
            Id, VisitorId, "processing-notice", "v3", Now, clientIp: "203.0.113.7", userAgent: "TestAgent/1.0");

        Assert.Equal(AcceptanceSubjectKind.Visitor, record.SubjectKind);
        Assert.Equal(VisitorId.Value, record.SubjectId);
        Assert.Equal("203.0.113.7", record.ClientIp);
        Assert.Equal("TestAgent/1.0", record.UserAgent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTenant_WithAnEmptyDocumentKey_Throws(string documentKey) =>
        Assert.Throws<ArgumentException>(() => AcceptanceRecord.ForTenant(Id, TenantId, documentKey, "v1", Now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTenant_WithAnEmptyDocumentVersion_Throws(string documentVersion) =>
        Assert.Throws<ArgumentException>(() => AcceptanceRecord.ForTenant(Id, TenantId, "privacy-policy", documentVersion, Now));

    [Fact]
    public void ForTenant_WithAnOversizedDocumentKey_Throws() =>
        Assert.Throws<ArgumentException>(
            () => AcceptanceRecord.ForTenant(Id, TenantId, new string('k', AcceptanceRecord.MaxDocumentKeyLength + 1), "v1", Now));

    [Fact]
    public void ForTenant_WithAnOversizedDocumentVersion_Throws() =>
        Assert.Throws<ArgumentException>(
            () => AcceptanceRecord.ForTenant(
                Id, TenantId, "privacy-policy", new string('v', AcceptanceRecord.MaxDocumentVersionLength + 1), Now));

    [Fact]
    public void ForVisitor_WithAnOversizedUserAgent_Throws() =>
        Assert.Throws<ArgumentException>(
            () => AcceptanceRecord.ForVisitor(
                Id, VisitorId, "processing-notice", "v1", Now, userAgent: new string('a', AcceptanceRecord.MaxUserAgentLength + 1)));

    [Fact]
    public void ForVisitor_WithAnOversizedClientIp_Throws() =>
        Assert.Throws<ArgumentException>(
            () => AcceptanceRecord.ForVisitor(
                Id, VisitorId, "processing-notice", "v1", Now, clientIp: new string('1', AcceptanceRecord.MaxClientIpLength + 1)));

    [Fact]
    public void ForTenant_TrimsDocumentKeyAndVersion() =>
        Assert.Equal(
            ("privacy-policy", "v1"),
            (AcceptanceRecord.ForTenant(Id, TenantId, "  privacy-policy  ", "  v1  ", Now).DocumentKey,
             AcceptanceRecord.ForTenant(Id, TenantId, "  privacy-policy  ", "  v1  ", Now).DocumentVersion));

    [Fact]
    public void TwoAcceptancesOfTheSameDocumentByTheSameSubject_AreDistinguishableRecords()
    {
        var first = AcceptanceRecord.ForOperator(new AcceptanceRecordId(Guid.NewGuid()), OperatorId, "terms-of-service", "v1", Now);
        var second = AcceptanceRecord.ForOperator(
            new AcceptanceRecordId(Guid.NewGuid()), OperatorId, "terms-of-service", "v2", Now.AddMonths(3));

        // Two rows, not one overwritten - each keeps its own id, version and instant. "What did they
        // agree to in March" and "what did they agree to in June" both have an answer.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("v1", first.DocumentVersion);
        Assert.Equal("v2", second.DocumentVersion);
        Assert.Equal(Now, first.AcceptedAt);
        Assert.Equal(Now.AddMonths(3), second.AcceptedAt);
    }
}
