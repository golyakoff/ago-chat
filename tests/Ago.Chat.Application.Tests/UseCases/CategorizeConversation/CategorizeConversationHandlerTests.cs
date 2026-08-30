using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CategorizeConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CategorizeConversation;

/// <summary>
/// `19-02`: the fakes-based half of this item's own proofs, the same shape
/// `GenerateReplyDraftHandlerTests` establishes for `19-01`. Covers this item's own Done-when directly:
/// a real seeded tag vocabulary gets picked from correctly, a zero-tag site produces zero AI-applied
/// tags, an already-tagged conversation is skipped entirely, and a categorizer answer naming something
/// outside the candidate set is discarded rather than trusted (the "never invent a tag" defence in
/// depth this handler's own remarks describe).
/// </summary>
public class CategorizeConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CategorizeConversationHandler Handler, FakeTagRepository Tags, FakeConversationCategorizer Categorizer, Conversation Conversation);

    private static Fixture CreateFixture(Action<FakeTagRepository, Conversation>? seed = null)
    {
        var readStore = new FakeConversationReadStore();
        var tags = new FakeTagRepository();
        var categorizer = new FakeConversationCategorizer();

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("do you ship to Kazan?"), Now);
        conversation.AssignTo(OperatorId, Now);
        conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("yes, 3-5 days"), Now.AddSeconds(1));
        conversation.Close(Now.AddSeconds(2));
        readStore.Seed(conversation);

        seed?.Invoke(tags, conversation);

        var handler = new CategorizeConversationHandler(
            readStore, tags, categorizer, new CategorizationOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CategorizeConversationHandler>.Instance);

        return new Fixture(handler, tags, categorizer, conversation);
    }

    /// <summary>This item's own Done-when: "a real conversation with no tags gets classified into zero
    /// or more of the site's own existing tags... proven against a real seeded site with a real tag
    /// vocabulary."</summary>
    [Fact]
    public async Task HandleAsync_WithASeededVocabulary_AppliesTheTagsTheCategorizerPicks_AsAiSourced()
    {
        Tag billing = null!, shipping = null!;
        var fixture = CreateFixture((tags, _) =>
        {
            billing = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
            shipping = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Shipping", Now);
            tags.Seed(billing);
            tags.Seed(shipping);
        });
        fixture.Categorizer.NextResult = new CategorizationResult.Success([shipping.Id]);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.Tagged, result.Value);
        var applied = Assert.Single(await fixture.Tags.GetForConversationAsync(fixture.Conversation.Id, CancellationToken.None));
        Assert.Equal(shipping.Id, applied.Tag.Id);
        Assert.Equal(TagSource.Ai, applied.Source);
    }

    /// <summary>This item's own Done-when: "a site with zero configured tags produces zero AI-applied
    /// tags, proven by a test, not left untested as 'probably fine.'"</summary>
    [Fact]
    public async Task HandleAsync_WhenTheSiteHasNoTagVocabularyAtAll_AppliesNothing_AndNeverCallsTheProvider()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.NoTagsConfigured, result.Value);
        Assert.Empty(await fixture.Tags.GetForConversationAsync(fixture.Conversation.Id, CancellationToken.None));
        // The provider is never even asked - a zero-tag site is a no-op, not "ask, then discard
        // everything the answer names."
        Assert.Null(fixture.Categorizer.LastRequest);
    }

    /// <summary>This item's own Done-when: "a conversation that already carries a manual tag is skipped
    /// by this item's own job, proven by a test." An operator's own prior judgment is never added
    /// alongside or overwritten.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheConversationAlreadyCarriesATag_SkipsIt_AndNeverCallsTheProvider()
    {
        Tag vip = null!, billing = null!;
        var fixture = CreateFixture((tags, conversation) =>
        {
            vip = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "VIP", Now);
            billing = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
            tags.Seed(vip);
            tags.Seed(billing);
            tags.SeedAssociation(conversation.Id, vip.Id, TagSource.Operator);
        });

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.AlreadyTagged, result.Value);
        var stillOnlyTag = Assert.Single(await fixture.Tags.GetForConversationAsync(fixture.Conversation.Id, CancellationToken.None));
        Assert.Equal(vip.Id, stillOnlyTag.Tag.Id);
        Assert.Equal(TagSource.Operator, stillOnlyTag.Source);
        Assert.Null(fixture.Categorizer.LastRequest);
    }

    /// <summary>The defence-in-depth half of "never invent a tag": even if
    /// <see cref="IConversationCategorizer"/> misbehaves and returns a <see cref="TagId"/> outside the
    /// candidate set this handler itself sent, that id is silently discarded rather than written -
    /// proven directly against the port's own contract, independent of whether the real YandexGPT
    /// client would ever actually do this (`Ago.Chat.Integration.Tests.YandexGptConversationCategorizerClientTests`
    /// is the complementary proof that it does not).</summary>
    [Fact]
    public async Task HandleAsync_WhenTheCategorizerReturnsATagOutsideTheCandidateSet_DiscardsIt()
    {
        var fixture = CreateFixture((tags, _) =>
        {
            var billing = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
            tags.Seed(billing);
        });
        var inventedTagId = new TagId(Guid.NewGuid());
        fixture.Categorizer.NextResult = new CategorizationResult.Success([inventedTagId]);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.NoMatch, result.Value);
        Assert.Empty(await fixture.Tags.GetForConversationAsync(fixture.Conversation.Id, CancellationToken.None));
    }

    /// <summary>A real, valid "none of these apply" answer - not converted into an error, and nothing
    /// is written.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheCategorizerPicksNone_AppliesNothing_ButIsStillSuccess()
    {
        var fixture = CreateFixture((tags, _) =>
        {
            var billing = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
            tags.Seed(billing);
        });
        fixture.Categorizer.NextResult = new CategorizationResult.Success([]);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.NoMatch, result.Value);
        Assert.Empty(await fixture.Tags.GetForConversationAsync(fixture.Conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTheProviderIsUnavailable_AppliesNothing_AndReportsUnavailable()
    {
        var fixture = CreateFixture((tags, _) =>
        {
            var billing = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
            tags.Seed(billing);
        });
        fixture.Categorizer.NextResult = new CategorizationResult.Unavailable("provider timed out");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.ProviderUnavailable, result.Value);
        Assert.Empty(await fixture.Tags.GetForConversationAsync(fixture.Conversation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(new ConversationId(Guid.NewGuid()), SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Only this conversation's own history reaches the provider - the identical
    /// context-minimalism proof `GenerateReplyDraftHandlerTests`'s own remarks describe for `19-01`,
    /// plus the candidate list carrying exactly this site's own vocabulary and nothing else.</summary>
    [Fact]
    public async Task HandleAsync_SendsThisConversationsOwnMessages_AndExactlyThisSitesOwnCandidateTags()
    {
        Tag billing = null!;
        var fixture = CreateFixture((tags, _) =>
        {
            billing = Tag.Create(new TagId(Guid.NewGuid()), SiteId, "Billing", Now);
            var otherSiteTag = Tag.Create(new TagId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), "OtherSiteTag", Now);
            tags.Seed(billing);
            tags.Seed(otherSiteTag);
        });

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CategorizeConversation.CategorizeConversation(fixture.Conversation.Id, SiteId), CancellationToken.None);

        Assert.NotNull(fixture.Categorizer.LastRequest);
        Assert.Contains(fixture.Categorizer.LastRequest!.RecentMessages, m => m.Body == "do you ship to Kazan?");
        Assert.Contains(fixture.Categorizer.LastRequest!.RecentMessages, m => m.Body == "yes, 3-5 days");
        var candidateNames = fixture.Categorizer.LastRequest!.CandidateTags.Select(c => c.Name).ToList();
        Assert.Equal(["Billing"], candidateNames);
    }
}
