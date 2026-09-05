using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetAcceptancesForSubject;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetAcceptancesForSubject;

public class GetAcceptancesForSubjectHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ReturnsEveryAcceptanceForThatSubject_OldestFirst()
    {
        var acceptances = new FakeAcceptanceRepository();
        var tenantId = new SiteId(Guid.NewGuid());

        await acceptances.SaveAsync(
            AcceptanceRecord.ForTenant(new AcceptanceRecordId(Guid.NewGuid()), tenantId, "privacy-policy", "v2", Now.AddMonths(3)),
            CancellationToken.None);
        await acceptances.SaveAsync(
            AcceptanceRecord.ForTenant(new AcceptanceRecordId(Guid.NewGuid()), tenantId, "privacy-policy", "v1", Now),
            CancellationToken.None);
        // A different subject - must not leak into this subject's own read-back.
        await acceptances.SaveAsync(
            AcceptanceRecord.ForTenant(new AcceptanceRecordId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "privacy-policy", "v1", Now),
            CancellationToken.None);

        var handler = new GetAcceptancesForSubjectHandler(acceptances);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAcceptancesForSubject.GetAcceptancesForSubject(AcceptanceSubjectKind.Tenant, tenantId.Value),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("v1", result[0].DocumentVersion);
        Assert.Equal(Now, result[0].AcceptedAt);
        Assert.Equal("v2", result[1].DocumentVersion);
        Assert.Equal(Now.AddMonths(3), result[1].AcceptedAt);
        Assert.All(result, r => Assert.Equal(tenantId.Value, r.SubjectId));
    }

    [Fact]
    public async Task HandleAsync_ForASubjectWithNoAcceptances_ReturnsEmpty()
    {
        var handler = new GetAcceptancesForSubjectHandler(new FakeAcceptanceRepository());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAcceptancesForSubject.GetAcceptancesForSubject(AcceptanceSubjectKind.Visitor, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Empty(result);
    }
}
