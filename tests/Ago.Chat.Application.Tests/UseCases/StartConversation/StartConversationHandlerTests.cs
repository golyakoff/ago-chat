using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Command = Ago.Chat.Application.UseCases.StartConversation.StartConversation;

namespace Ago.Chat.Application.Tests.UseCases.StartConversation;

public class StartConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (
        StartConversationHandler Handler, FakeVisitorRepository Visitors, FakeConversationRepository Conversations)
        CreateHandler(FakeSiteRepository? sites = null)
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var siteConfig = new GetSiteConfigByIdHandler(sites ?? new FakeSiteRepository(), new FakeCache());
        var handler = new StartConversationHandler(
            visitors, conversations, siteConfig, new FakeClock(Now), new FakeIdGenerator(),
            new FakeVisitorEmojiPairGenerator());
        return (handler, visitors, conversations);
    }

    /// <summary>`23-78`: a site whose own `WidgetConfig.AllowAttachmentUploadsByDefault` is
    /// <paramref name="allowAttachmentUploadsByDefault"/> - seeded into a fake repository so
    /// `GetSiteConfigByIdHandler` (the same cache-aside read `StartConversationHandler` composes
    /// through, per that handler's own remarks) has a real row to answer from.</summary>
    private static FakeSiteRepository CreateSites(bool allowAttachmentUploadsByDefault)
    {
        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"pk_{Guid.NewGuid():N}", []);
        site.UpdateWidgetConfig(
            new WidgetConfig(null, Position.BottomRight, allowAttachmentUploadsByDefault: allowAttachmentUploadsByDefault),
            Now);
        sites.Seed(site);
        return sites;
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorHasNoActiveConversation_StartsANewOne()
    {
        var (handler, _, _) = CreateHandler();

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsNew);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorAlreadyHasAWaitingConversation_ResumesIt()
    {
        var (handler, _, conversations) = CreateHandler();
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsNew);
        Assert.Equal(existing.Id, result.Value.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorAlreadyHasAClosedConversation_StartsANewOneInstead()
    {
        var (handler, _, conversations) = CreateHandler();
        var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        closed.Close(Now);
        conversations.Seed(closed);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsNew);
        Assert.NotEqual(closed.Id, result.Value.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorIsNew_CreatesTheVisitorRecord()
    {
        var (handler, visitors, _) = CreateHandler();

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var saved = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(Now, saved.FirstSeenAt);
    }

    // `25-56` decision 5: the pair is assigned once, at first contact, and a returning visitor's
    // *second* conversation must read back the identical pair the *first* one triggered - not a fresh
    // pick. The fake generator is seeded to hand out a different pair on every call, so this would fail
    // if the handler ever re-assigned on the "visitor already exists" branch.
    [Fact]
    public async Task HandleAsync_WhenTheSameVisitorStartsASecondConversation_TheEmojiPairIsUnchanged()
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var siteConfig = new GetSiteConfigByIdHandler(new FakeSiteRepository(), new FakeCache());
        var emojiPairs = new FakeVisitorEmojiPairGenerator("🐳", "🌭");
        var handler = new StartConversationHandler(
            visitors, conversations, siteConfig, new FakeClock(Now), new FakeIdGenerator(), emojiPairs);

        var first = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);
        var afterFirstContact = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);

        // The first conversation closes and a different pair is now on offer - if the handler's
        // "visitor already exists" branch ever called AssignEmojiPair again, the second conversation
        // below would pick this new pair up instead of keeping the first one.
        var firstConversation = await conversations.GetByIdAsync(first.Value.ConversationId, CancellationToken.None);
        firstConversation!.Close(Now);
        emojiPairs.Creature = "🐠";
        emojiPairs.Food = "🥝";

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);
        var afterSecondConversation = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);

        Assert.Equal("🐳", afterFirstContact!.EmojiCreature);
        Assert.Equal("🌭", afterFirstContact.EmojiFood);
        Assert.Equal(afterFirstContact.EmojiCreature, afterSecondConversation!.EmojiCreature);
        Assert.Equal(afterFirstContact.EmojiFood, afterSecondConversation.EmojiFood);
        Assert.Equal(1, emojiPairs.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorReturns_TouchesLastSeenAtWithoutChangingFirstSeenAt()
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var firstContact = Now;
        await visitors.SaveAsync(new Visitor(VisitorId, SiteId, firstContact), CancellationToken.None);

        var returnVisit = Now.AddDays(1);
        var siteConfig = new GetSiteConfigByIdHandler(new FakeSiteRepository(), new FakeCache());
        var handler = new StartConversationHandler(
            visitors, conversations, siteConfig, new FakeClock(returnVisit), new FakeIdGenerator(),
            new FakeVisitorEmojiPairGenerator());

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var saved = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(firstContact, saved.FirstSeenAt);
        Assert.Equal(returnVisit, saved.LastSeenAt);
    }

    /// <summary>`23-78`'s own Done-when: "a visitor cannot obtain an upload slot for a conversation
    /// with no grant" - the default case, and the one every site not yet configured otherwise gets.
    /// No site seeded at all here, deliberately: `GetSiteConfigByIdHandler` answers "not found" for an
    /// unseeded site, and the handler's own `config?.WidgetAllowAttachmentUploadsByDefault ?? false`
    /// must still land on the closed-by-default answer rather than throwing.</summary>
    [Fact]
    public async Task HandleAsync_WhenSiteHasNoAttachmentUploadDefault_StartsWithNoGrant()
    {
        var (handler, _, conversations) = CreateHandler();

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasAttachmentUploadGrant);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved.HasAttachmentUploadGrant);
        Assert.Null(saved.AttachmentUploadGrantedBy);
    }

    /// <summary>The reverse - a tenant that turned `WidgetConfig.AllowAttachmentUploadsByDefault` on
    /// gets a brand-new conversation that already carries the grant, with no operator behind it
    /// (`Conversation.Start`'s own remarks: a tenant default is not an operator's own act).</summary>
    [Fact]
    public async Task HandleAsync_WhenSiteAllowsAttachmentUploadsByDefault_StartsWithAGrantAndNoOperatorAttribution()
    {
        var sites = CreateSites(allowAttachmentUploadsByDefault: true);
        var (handler, _, conversations) = CreateHandler(sites);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.HasAttachmentUploadGrant);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.HasAttachmentUploadGrant);
        Assert.Equal(Now, saved.AttachmentUploadGrantedAt);
        Assert.Null(saved.AttachmentUploadGrantedBy);
    }

    /// <summary>The tenant default only ever seeds a *brand-new* conversation - a visitor resuming an
    /// existing one keeps whatever that conversation already had, even if the tenant's own default has
    /// since changed. This is not this handler re-deciding anything: it is
    /// `GetActiveForVisitorAsync`'s own early return, which never calls `Conversation.Start` at all.</summary>
    [Fact]
    public async Task HandleAsync_WhenResumingAnExistingConversation_TenantDefaultIsNeverConsulted()
    {
        var sites = CreateSites(allowAttachmentUploadsByDefault: true);
        var (handler, _, conversations) = CreateHandler(sites);
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsNew);
        Assert.False(result.Value.HasAttachmentUploadGrant);
    }
}
