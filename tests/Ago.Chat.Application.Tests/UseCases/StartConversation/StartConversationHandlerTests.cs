using Ago.Chat.Application.Tests.Fakes;
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
        CreateHandler()
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var handler = new StartConversationHandler(visitors, conversations, new FakeClock(Now), new FakeIdGenerator());
        return (handler, visitors, conversations);
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

    [Fact]
    public async Task HandleAsync_WhenVisitorReturns_TouchesLastSeenAtWithoutChangingFirstSeenAt()
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var firstContact = Now;
        await visitors.SaveAsync(new Visitor(VisitorId, SiteId, firstContact), CancellationToken.None);

        var returnVisit = Now.AddDays(1);
        var handler = new StartConversationHandler(
            visitors, conversations, new FakeClock(returnVisit), new FakeIdGenerator());

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var saved = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(firstContact, saved.FirstSeenAt);
        Assert.Equal(returnVisit, saved.LastSeenAt);
    }
}
