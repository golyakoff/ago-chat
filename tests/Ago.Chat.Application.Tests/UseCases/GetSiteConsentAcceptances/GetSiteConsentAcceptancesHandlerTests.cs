using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConsentAcceptances;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteConsentAcceptances;

public class GetSiteConsentAcceptancesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Fixture(GetSiteConsentAcceptancesHandler Handler, FakeAcceptanceRepository Acceptances, FakePermissionChecker Permissions);

    private static Fixture CreateFixture()
    {
        var acceptances = new FakeAcceptanceRepository();
        var permissions = new FakePermissionChecker();
        return new Fixture(new GetSiteConsentAcceptancesHandler(permissions, acceptances), acceptances, permissions);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSiteConsentAcceptances.GetSiteConsentAcceptances(SiteId, "Contact", OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownPurpose_ReturnsInvalidPurpose()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSiteConsentAcceptances.GetSiteConsentAcceptances(SiteId, "Upsell", OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.InvalidPurpose", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_NoAcceptancesYet_ReturnsEmptyList()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSiteConsentAcceptances.GetSiteConsentAcceptances(SiteId, "Contact", OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAcceptancesForThisPurposesKey_NewestFirst_WithNoClientIpOrUserAgent()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var key = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        var visitorA = new VisitorId(Guid.NewGuid());
        var visitorB = new VisitorId(Guid.NewGuid());
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(
                new AcceptanceRecordId(Guid.NewGuid()), visitorA, key, "v1", Now, clientIp: "203.0.113.1", userAgent: "TestAgent/1.0"),
            CancellationToken.None);
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(
                new AcceptanceRecordId(Guid.NewGuid()), visitorB, key, "v2", Now.AddMinutes(5)),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSiteConsentAcceptances.GetSiteConsentAcceptances(SiteId, "Contact", OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(visitorB.Value, result.Value[0].SubjectId); // newest first.
        Assert.Equal("v2", result.Value[0].DocumentVersion);
        Assert.Equal(visitorA.Value, result.Value[1].SubjectId);
        Assert.Equal("v1", result.Value[1].DocumentVersion);
        // No ClientIp/UserAgent property exists on this DTO at all - the design choice this item's own
        // remarks state, checked here at the type level rather than by asserting a null that a later
        // change could quietly widen.
        Assert.DoesNotContain("ClientIp", typeof(SiteConsentAcceptanceDto).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("UserAgent", typeof(SiteConsentAcceptanceDto).GetProperties().Select(p => p.Name));
    }

    /// <summary>The isolation proof: an acceptance recorded against a different site's own document key
    /// never appears in this site's own read, because the read filters on the derived key alone and the
    /// key is never caller-supplied.</summary>
    [Fact]
    public async Task HandleAsync_AcceptanceRecordedForAnotherSitesDocument_IsNeverReturned()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var otherSite = new SiteId(Guid.NewGuid());
        var otherKey = SiteConsentDocumentKey.For(otherSite, VisitorConsentPurpose.Contact);
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), new VisitorId(Guid.NewGuid()), otherKey, "v1", Now),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetSiteConsentAcceptances.GetSiteConsentAcceptances(SiteId, "Contact", OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }
}
