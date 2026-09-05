using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetAccessRecordsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetAccessRecordsForSite;

/// <summary>`24-12`'s own Scope: "reachable by the tenant for their own site, not only by AGO" -
/// proven here as an ordinary permission gate plus tenant isolation, the same shape every other
/// site-scoped read in this codebase is tested with.</summary>
public class GetAccessRecordsForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId UnprivilegedOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (GetAccessRecordsForSiteHandler Handler, FakeAccessRecordRepository AccessRecords) CreateFixture()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(AdminOperatorId, SiteId, Permission.AccessRecordRead);
        var accessRecords = new FakeAccessRecordRepository();
        return (new GetAccessRecordsForSiteHandler(accessRecords, permissions), accessRecords);
    }

    [Fact]
    public async Task HandleAsync_WithoutAccessRecordReadPermission_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAccessRecordsForSite.GetAccessRecordsForSite(SiteId, UnprivilegedOperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithPermission_ReturnsOnlyThisSitesOwnRecords_NeverAnotherSites()
    {
        var (handler, accessRecords) = CreateFixture();
        await accessRecords.RecordAsync(
            new AccessRecordToWrite(
                Guid.NewGuid(), Now, AccessRecordKind.CrossConversationHistoryRead, SiteId,
                AccessRecordActorKind.Operator, AdminOperatorId.Value.ToString(), AccessRecordResourceKind.Conversation,
                Guid.NewGuid()),
            CancellationToken.None);
        await accessRecords.RecordAsync(
            new AccessRecordToWrite(
                Guid.NewGuid(), Now, AccessRecordKind.CrossConversationHistoryRead, OtherSiteId,
                AccessRecordActorKind.Operator, Guid.NewGuid().ToString(), AccessRecordResourceKind.Conversation,
                Guid.NewGuid()),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAccessRecordsForSite.GetAccessRecordsForSite(SiteId, AdminOperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(AdminOperatorId.Value.ToString(), item.ActorId);
    }

    /// <summary>`24-12`'s own open question, answered: a tenant's own report includes AGO's own
    /// platform-owner accesses of their site, not only their own operators' accesses.</summary>
    [Fact]
    public async Task HandleAsync_IncludesThePlatformOwnersOwnAccessesOfThisSite()
    {
        var (handler, accessRecords) = CreateFixture();
        await accessRecords.RecordAsync(
            new AccessRecordToWrite(
                Guid.NewGuid(), Now, AccessRecordKind.OwnerSiteDetail, SiteId,
                AccessRecordActorKind.PlatformOwner, "keycloak-owner-subject", ResourceKind: null, ResourceId: null),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetAccessRecordsForSite.GetAccessRecordsForSite(SiteId, AdminOperatorId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(AccessRecordActorKind.PlatformOwner, item.ActorKind);
        Assert.Equal(AccessRecordKind.OwnerSiteDetail, item.AccessKind);
    }
}
