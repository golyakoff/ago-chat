using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConsentRequirement;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConsentRequirement;

public class GetConsentRequirementHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetConsentRequirementHandler Handler, ConversationId ConversationId, FakeDocumentRepository Documents,
        FakeAcceptanceRepository Acceptances, FakeSiteRepository Sites, Site Site);

    private static Fixture CreateFixture(bool requireContactConsent)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"site_{SiteId.Value:N}", ["https://example.test"], "Test Site", Now);
        if (requireContactConsent)
        {
            site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, requireContactConsent: true), Now);
        }

        sites.Seed(site);

        var documents = new FakeDocumentRepository();
        var acceptances = new FakeAcceptanceRepository();
        var handler = new GetConsentRequirementHandler(conversations, sites, documents, acceptances);

        return new Fixture(handler, conversation.Id, documents, acceptances, sites, site);
    }

    private static async Task PublishAsync(FakeDocumentRepository documents, string documentKey, string version)
    {
        var document = Document.Create(new DocumentId(Guid.NewGuid()), documentKey);
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Title", "Body", Now);
        await documents.SaveAsync(document, CancellationToken.None);
        _ = version; // the fake always mints "v1" for the first publish - kept as a parameter for readability at call sites.
    }

    [Fact]
    public async Task HandleAsync_SiteDoesNotRequireConsent_ReturnsNotRequired_WithNoDocuments()
    {
        var fixture = CreateFixture(requireContactConsent: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(fixture.ConversationId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.ContactRequired);
        Assert.Null(result.Value.Contact);
        Assert.False(result.Value.ContactAlreadyAccepted);
        Assert.Null(result.Value.Marketing);
        Assert.False(result.Value.MarketingAlreadyAccepted);
    }

    [Fact]
    public async Task HandleAsync_SiteRequiresConsent_ButNothingPublishedYet_ReturnsRequired_WithAnUnavailableSummary()
    {
        var fixture = CreateFixture(requireContactConsent: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(fixture.ConversationId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ContactRequired);
        Assert.NotNull(result.Value.Contact);
        Assert.Equal(SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact), result.Value.Contact!.DocumentKey);
        Assert.Null(result.Value.Contact.Version);
        Assert.Null(result.Value.Contact.Title);
        Assert.False(result.Value.ContactAlreadyAccepted);
    }

    [Fact]
    public async Task HandleAsync_SiteRequiresConsent_DocumentPublished_VisitorNotYetAccepted_ReturnsTheCurrentText()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, contactKey, "v1");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(fixture.ConversationId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ContactRequired);
        Assert.Equal("v1", result.Value.Contact!.Version);
        Assert.Equal("Title", result.Value.Contact.Title);
        Assert.False(result.Value.ContactAlreadyAccepted);
    }

    [Fact]
    public async Task HandleAsync_VisitorAlreadyAccepted_ReportsAccepted()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, contactKey, "v1");
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), VisitorId, contactKey, "v1", Now), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(fixture.ConversationId, VisitorId), CancellationToken.None);

        Assert.True(result.Value.ContactAlreadyAccepted);
    }

    [Fact]
    public async Task HandleAsync_MarketingIsNeverRequired_EvenWhenContactIs()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var marketingKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Marketing);
        await PublishAsync(fixture.Documents, marketingKey, "v1");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(fixture.ConversationId, VisitorId), CancellationToken.None);

        Assert.True(result.Value.ContactRequired);
        Assert.NotNull(result.Value.Marketing);
        Assert.Equal("v1", result.Value.Marketing!.Version);
        Assert.False(result.Value.MarketingAlreadyAccepted);
    }

    [Fact]
    public async Task HandleAsync_NotTheConversationsVisitor_ReturnsForbidden()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(fixture.ConversationId, someoneElse), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture(requireContactConsent: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConsentRequirement.GetConsentRequirement(new ConversationId(Guid.NewGuid()), VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
