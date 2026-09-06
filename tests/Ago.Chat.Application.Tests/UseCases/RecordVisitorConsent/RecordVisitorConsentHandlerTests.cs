using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordVisitorConsent;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordVisitorConsent;

public class RecordVisitorConsentHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RecordVisitorConsentHandler Handler, ConversationId ConversationId, FakeDocumentRepository Documents,
        FakeAcceptanceRepository Acceptances);

    private static Fixture CreateFixture(Ago.Platform.Abstractions.IRateLimiter? rateLimiter = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var documents = new FakeDocumentRepository();
        var acceptances = new FakeAcceptanceRepository();
        var handler = new RecordVisitorConsentHandler(
            conversations, documents, acceptances, rateLimiter ?? new FakeRateLimiter(), new ConsentRateLimitOptions(),
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, conversation.Id, documents, acceptances);
    }

    private static async Task<string> PublishAsync(FakeDocumentRepository documents, string documentKey)
    {
        var document = Document.Create(new DocumentId(Guid.NewGuid()), documentKey);
        var version = document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Title", "Body", Now);
        await documents.SaveAsync(document, CancellationToken.None);
        return version.Version;
    }

    [Fact]
    public async Task HandleAsync_ContactPurpose_DocumentPublished_SavesAnAcceptanceAgainstTheCurrentVersion()
    {
        var fixture = CreateFixture();
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        var version = await PublishAsync(fixture.Documents, contactKey);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Contact"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(contactKey, result.Value.DocumentKey);
        Assert.Equal(version, result.Value.DocumentVersion);
        var saved = Assert.Single(fixture.Acceptances.Saved);
        Assert.Equal(AcceptanceSubjectKind.Visitor, saved.SubjectKind);
        Assert.Equal(VisitorId.Value, saved.SubjectId);
        Assert.Equal(contactKey, saved.DocumentKey);
        Assert.Equal(version, saved.DocumentVersion);
    }

    [Fact]
    public async Task HandleAsync_MarketingPurpose_ProducesADifferentDocumentKeyThanContact()
    {
        var fixture = CreateFixture();
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        var marketingKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Marketing);
        await PublishAsync(fixture.Documents, marketingKey);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Marketing"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(marketingKey, result.Value.DocumentKey);
        Assert.NotEqual(contactKey, marketingKey);
    }

    [Fact]
    public async Task HandleAsync_UnknownPurpose_ReturnsInvalidPurpose_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Newsletter"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.InvalidPurpose", result.Error!.Value.Code);
        Assert.Empty(fixture.Acceptances.Saved);
    }

    [Fact]
    public async Task HandleAsync_NoVersionEverPublishedUnderThisPurposesKey_ReturnsConsentDocumentUnavailable()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Contact"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.ConsentDocumentUnavailable", result.Error!.Value.Code);
        Assert.Empty(fixture.Acceptances.Saved);
    }

    [Fact]
    public async Task HandleAsync_NotTheConversationsVisitor_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture();
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, contactKey);
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, someoneElse, "Contact"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Acceptances.Saved);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(new ConversationId(Guid.NewGuid()), VisitorId, "Contact"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_RateLimited_ReturnsConsentRateLimited_AndSavesNothing()
    {
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(30)));
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, contactKey);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Contact"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Consent.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.Acceptances.Saved);
    }

    [Fact]
    public async Task HandleAsync_CalledTwice_SavesTwoDistinctAcceptanceRecords()
    {
        var fixture = CreateFixture();
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await PublishAsync(fixture.Documents, contactKey);

        await fixture.Handler.HandleAsync(new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Contact"), CancellationToken.None);
        await fixture.Handler.HandleAsync(new Application.UseCases.RecordVisitorConsent.RecordVisitorConsent(fixture.ConversationId, VisitorId, "Contact"), CancellationToken.None);

        Assert.Equal(2, fixture.Acceptances.Saved.Count);
    }
}
