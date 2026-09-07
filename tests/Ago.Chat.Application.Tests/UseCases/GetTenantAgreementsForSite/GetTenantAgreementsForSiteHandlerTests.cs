using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetTenantAgreementsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetTenantAgreementsForSite;

/// <summary>
/// `23-52`'s own Done-when: "the tenant can read what is on their account... without asking us"
/// (`23-37`'s own wording, pointed at AGO's documents here). The same shape
/// <c>GetAccessRecordsForSiteHandlerTests</c> already proves for a different compliance-shaped read:
/// a permission gate, and structural isolation from another tenant's own rows.
/// </summary>
public class GetTenantAgreementsForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId UnprivilegedOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (GetTenantAgreementsForSiteHandler Handler, FakeAcceptanceRepository Acceptances) CreateFixture()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(AdminOperatorId, SiteId, Permission.SiteConfigure);
        var acceptances = new FakeAcceptanceRepository();
        return (new GetTenantAgreementsForSiteHandler(acceptances, permissions), acceptances);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigurePermission_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTenantAgreementsForSite.GetTenantAgreementsForSite(SiteId, UnprivilegedOperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>Deliberately not through <c>RegisterSiteHandler</c> - this handler only has to read
    /// back whatever <see cref="AcceptanceRecord.ForTenant"/> produced, regardless of who wrote it.
    /// `23-52`'s own Done-when #1 is proven end to end by `RegisterSiteHandlerTests`
    /// (`24-03`'s own tests, unchanged by this item) plus this handler's own read - the two together
    /// are the whole round trip, neither alone.</summary>
    [Fact]
    public async Task HandleAsync_WithPermission_ReturnsThisSitesOwnAcceptance_NamingTheAcceptedVersion()
    {
        var (handler, acceptances) = CreateFixture();
        await acceptances.SaveAsync(
            AcceptanceRecord.ForTenant(
                new AcceptanceRecordId(Guid.NewGuid()), SiteId, "tenant-terms", "v1", Now, "203.0.113.1", "TestBrowser/1.0"),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTenantAgreementsForSite.GetTenantAgreementsForSite(SiteId, AdminOperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value);
        Assert.Equal(AcceptanceSubjectKind.Tenant, item.SubjectKind);
        Assert.Equal(SiteId.Value, item.SubjectId);
        Assert.Equal("tenant-terms", item.DocumentKey);
        Assert.Equal("v1", item.DocumentVersion);
        Assert.Equal(Now, item.AcceptedAt);
    }

    /// <summary>The structural half of tenant isolation: filtering by <c>(Tenant, siteId.Value)</c>
    /// alone, with no second check, is what keeps another site's own acceptance from ever appearing
    /// here - the same guarantee <c>GetAccessRecordsForSiteHandlerTests</c>' own
    /// "NeverAnotherSites" test proves for a different store.</summary>
    [Fact]
    public async Task HandleAsync_NeverReturnsAnotherSitesOwnAcceptance()
    {
        var (handler, acceptances) = CreateFixture();
        await acceptances.SaveAsync(
            AcceptanceRecord.ForTenant(new AcceptanceRecordId(Guid.NewGuid()), OtherSiteId, "tenant-terms", "v1", Now),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTenantAgreementsForSite.GetTenantAgreementsForSite(SiteId, AdminOperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenNothingWasAccepted_ReturnsEmpty_NotAnError()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetTenantAgreementsForSite.GetTenantAgreementsForSite(SiteId, AdminOperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }
}
