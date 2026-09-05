using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordAcceptance;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordAcceptance;

public class RecordAcceptanceHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(RecordAcceptanceHandler Handler, FakeAcceptanceRepository Acceptances);

    private static Fixture CreateFixture(DateTimeOffset? now = null)
    {
        var acceptances = new FakeAcceptanceRepository();
        var handler = new RecordAcceptanceHandler(acceptances, new FakeIdGenerator(), new FakeClock(now ?? Now));
        return new Fixture(handler, acceptances);
    }

    [Fact]
    public async Task HandleAsync_ForATenant_SavesOneRecordWithSubjectDocumentVersionAndTimestamp()
    {
        var fixture = CreateFixture();
        var tenantId = Guid.NewGuid();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordAcceptance.RecordAcceptance(
                AcceptanceSubjectKind.Tenant, tenantId, "privacy-policy", "v1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        var saved = Assert.Single(fixture.Acceptances.Saved);
        Assert.Equal(AcceptanceSubjectKind.Tenant, saved.SubjectKind);
        Assert.Equal(tenantId, saved.SubjectId);
        Assert.Equal("privacy-policy", saved.DocumentKey);
        Assert.Equal("v1", saved.DocumentVersion);
        Assert.Equal(Now, saved.AcceptedAt);

        Assert.Equal(saved.Id.Value, result.Value.Id);
        Assert.Equal(AcceptanceSubjectKind.Tenant, result.Value.SubjectKind);
        Assert.Equal(tenantId, result.Value.SubjectId);
    }

    [Fact]
    public async Task HandleAsync_ForAnOperator_SavesUnderTheOperatorSubjectKind()
    {
        var fixture = CreateFixture();
        var operatorId = Guid.NewGuid();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordAcceptance.RecordAcceptance(
                AcceptanceSubjectKind.Operator, operatorId, "terms-of-service", "v1"),
            CancellationToken.None);

        var saved = Assert.Single(fixture.Acceptances.Saved);
        Assert.Equal(AcceptanceSubjectKind.Operator, saved.SubjectKind);
        Assert.Equal(operatorId, saved.SubjectId);
    }

    [Fact]
    public async Task HandleAsync_ForAVisitor_CapturesClientIpAndUserAgent()
    {
        var fixture = CreateFixture();
        var visitorId = Guid.NewGuid();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordAcceptance.RecordAcceptance(
                AcceptanceSubjectKind.Visitor, visitorId, "processing-notice", "v1",
                ClientIp: "203.0.113.7", UserAgent: "TestAgent/1.0"),
            CancellationToken.None);

        var saved = Assert.Single(fixture.Acceptances.Saved);
        Assert.Equal(AcceptanceSubjectKind.Visitor, saved.SubjectKind);
        Assert.Equal("203.0.113.7", saved.ClientIp);
        Assert.Equal("TestAgent/1.0", saved.UserAgent);
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyDocumentKey_ReturnsInvalid_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordAcceptance.RecordAcceptance(
                AcceptanceSubjectKind.Tenant, Guid.NewGuid(), "   ", "v1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Acceptance.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Acceptances.Saved);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceForTheSameSubjectAndDocument_SavesTwoDistinctRecords()
    {
        var acceptances = new FakeAcceptanceRepository();
        var clock = new FakeClock(Now);
        var handler = new RecordAcceptanceHandler(acceptances, new FakeIdGenerator(), clock);
        var tenantId = Guid.NewGuid();

        await handler.HandleAsync(
            new Application.UseCases.RecordAcceptance.RecordAcceptance(AcceptanceSubjectKind.Tenant, tenantId, "privacy-policy", "v1"),
            CancellationToken.None);

        clock.UtcNow = Now.AddMonths(3);
        await handler.HandleAsync(
            new Application.UseCases.RecordAcceptance.RecordAcceptance(AcceptanceSubjectKind.Tenant, tenantId, "privacy-policy", "v2"),
            CancellationToken.None);

        // "What did they agree to in March" and "in June" both still have their own answer - the
        // second call is a second row, never an update of the first (24-01's own Done-when).
        Assert.Equal(2, acceptances.Saved.Count);
        Assert.Contains(acceptances.Saved, r => r.DocumentVersion == "v1" && r.AcceptedAt == Now);
        Assert.Contains(acceptances.Saved, r => r.DocumentVersion == "v2" && r.AcceptedAt == Now.AddMonths(3));
        Assert.NotEqual(acceptances.Saved[0].Id, acceptances.Saved[1].Id);
    }
}
