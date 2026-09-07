using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConsentDocuments;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteConsentDocuments;

public class GetSiteConsentDocumentsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Fixture(
        GetSiteConsentDocumentsHandler Handler, FakeDocumentRepository Documents, FakeSiteRepository Sites,
        FakePermissionChecker Permissions, Site Site);

    private static Fixture CreateFixture(bool requireContactConsent = false)
    {
        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"site_{SiteId.Value:N}", ["https://example.test"], "Test Site", Now);
        if (requireContactConsent)
        {
            site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, requireContactConsent: true), Now);
        }

        sites.Seed(site);

        var documents = new FakeDocumentRepository();
        var permissions = new FakePermissionChecker();
        var handler = new GetSiteConsentDocumentsHandler(sites, permissions, documents);

        return new Fixture(handler, documents, sites, permissions, site);
    }

    private static async Task PublishAsync(FakeDocumentRepository documents, string documentKey, string title, string body)
    {
        var existing = await documents.GetByKeyAsync(documentKey, CancellationToken.None);
        var document = existing ?? Document.Create(new DocumentId(Guid.NewGuid()), documentKey);
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), title, body, Now);
        await documents.SaveAsync(document, CancellationToken.None);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetSiteConsentDocuments.GetSiteConsentDocuments(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownSite_ReturnsSiteNotFound()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var unknownSite = new SiteId(Guid.NewGuid());
        fixture.Permissions.Grant(OperatorId, unknownSite, Permission.SiteConfigure);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetSiteConsentDocuments.GetSiteConsentDocuments(unknownSite, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_NothingPublishedYet_ReturnsBothPurposesWithEmptyVersionLists()
    {
        var fixture = CreateFixture();
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetSiteConsentDocuments.GetSiteConsentDocuments(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        Assert.Equal(SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact), result.Value.Contact.DocumentKey);
        Assert.Empty(result.Value.Contact.Versions);
        Assert.Equal(SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Marketing), result.Value.Marketing.DocumentKey);
        Assert.Empty(result.Value.Marketing.Versions);
        Assert.False(result.Value.ContactConsentRequired);
    }

    [Fact]
    public async Task HandleAsync_WithPublishedVersions_ReturnsThemNewestFirst()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        fixture.Permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var key = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, key, "Contact consent v1", "First draft.");
        await PublishAsync(fixture.Documents, key, "Contact consent v2", "Second draft.");

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetSiteConsentDocuments.GetSiteConsentDocuments(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ContactConsentRequired);
        Assert.Equal(2, result.Value.Contact.Versions.Count);
        Assert.Equal("v2", result.Value.Contact.Versions[0].Version); // newest first.
        Assert.Equal("v1", result.Value.Contact.Versions[1].Version);
        Assert.Empty(result.Value.Marketing.Versions); // the two purposes never share a key.
    }

    /// <summary>The isolation proof this item's own report names: an operator holding `site:configure`
    /// only on their own site can never read another site's consent documents through this handler,
    /// because <see cref="Permission.SiteConfigure"/> is checked against the query's own
    /// <see cref="Domain.SiteId"/>, never against anything the caller's token happens to carry
    /// elsewhere.</summary>
    [Fact]
    public async Task HandleAsync_OperatorHoldsPermissionOnlyForAnotherSite_ReturnsForbidden_NeverTheOtherSitesDocuments()
    {
        var fixture = CreateFixture();
        var otherSite = new SiteId(Guid.NewGuid());
        var otherSiteEntity = new Site(otherSite, $"site_{otherSite.Value:N}", ["https://other.test"], "Other Site", Now);
        fixture.Sites.Seed(otherSiteEntity);
        var otherKey = SiteConsentDocumentKey.For(otherSite, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, otherKey, "Other tenant's own consent text", "Body.");

        // The caller holds site:configure on the OTHER site only - never on SiteId, the one it asks for.
        fixture.Permissions.Grant(OperatorId, otherSite, Permission.SiteConfigure);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetSiteConsentDocuments.GetSiteConsentDocuments(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
