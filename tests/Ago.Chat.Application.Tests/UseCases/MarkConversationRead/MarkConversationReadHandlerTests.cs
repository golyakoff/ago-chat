using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.MarkConversationRead;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.MarkConversationRead;

/// <summary>`5-15`: the use-case level - permission, per-conversation authorization, the no-save
/// no-op, and the retry-once conflict policy. The domain arithmetic itself is
/// <c>ConversationTests</c>'s job, and the real race against Postgres is
/// <c>Ago.Chat.Concurrency.Tests</c>'s.</summary>
public class MarkConversationReadHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Conversation AssignedConversationWithUnread(
        ConversationId id, int visitorMessages, OperatorId? assignedTo = null)
    {
        var conversation = Conversation.Start(id, SiteId, VisitorId, Now);
        conversation.AssignTo(assignedTo ?? OperatorId, Now);
        for (var i = 0; i < visitorMessages; i++)
        {
            var message = conversation.AddVisitorMessage(
                VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("incoming"), Now);
            conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, message.Sequence);
        }

        conversation.ClearDomainEvents();
        return conversation;
    }

    private static FakePermissionChecker Permissions(bool granted)
    {
        var permissions = new FakePermissionChecker();
        if (granted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        return permissions;
    }

    [Fact]
    public async Task HandleAsync_TheAssignedOperator_ClearsTheCountAndSaves()
    {
        var conversation = AssignedConversationWithUnread(new ConversationId(Guid.NewGuid()), 3);
        var repository = new CountingConversationRepository(conversation);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: true));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(conversation.Id, OperatorId, SiteId, UpToSequence: 3), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.OperatorUnreadCount);
        Assert.Equal(3, result.Value.OperatorLastReadSequence);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_AlreadyReadToThere_SavesNothingAndStillReportsTheRealCount()
    {
        // `5-15`'s "marking an already-read conversation read is a no-op, not an error". Proven as
        // "no second write reached the repository", not merely "the call succeeded".
        var conversation = AssignedConversationWithUnread(new ConversationId(Guid.NewGuid()), 2);
        var repository = new CountingConversationRepository(conversation);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: true));
        var command = new Application.UseCases.MarkConversationRead.MarkConversationRead(conversation.Id, OperatorId, SiteId, UpToSequence: 2);

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(0, second.Value.OperatorUnreadCount);
        Assert.Equal(2, second.Value.OperatorLastReadSequence);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_ByAnOperatorWhoIsNotTheAssignedOne_IsForbiddenAndChangesNothing()
    {
        var conversation = AssignedConversationWithUnread(
            new ConversationId(Guid.NewGuid()), 2, assignedTo: new OperatorId(Guid.NewGuid()));
        var repository = new CountingConversationRepository(conversation);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: true));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(conversation.Id, OperatorId, SiteId, UpToSequence: 2), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        // The other operator's count is untouched - this is the case that would otherwise let one
        // operator silently clear another's badge.
        Assert.Equal(2, conversation.OperatorUnreadCount);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_WithoutTheConversationReadPermission_IsForbidden()
    {
        var conversation = AssignedConversationWithUnread(new ConversationId(Guid.NewGuid()), 1);
        var repository = new CountingConversationRepository(conversation);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: false));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(conversation.Id, OperatorId, SiteId, UpToSequence: 1), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var conversation = AssignedConversationWithUnread(new ConversationId(Guid.NewGuid()), 1);
        var repository = new CountingConversationRepository(conversation);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: true));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(new ConversationId(Guid.NewGuid()), OperatorId, SiteId, UpToSequence: 1),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ConflictOnTheFirstSave_RetriesOnceAndSucceeds()
    {
        var id = new ConversationId(Guid.NewGuid());
        var repository = new ConflictingConversationRepository(
            () => AssignedConversationWithUnread(id, 2), failNextSaves: 1);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: true));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(id, OperatorId, SiteId, UpToSequence: 2), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.OperatorUnreadCount);
        Assert.Equal(2, repository.SaveCount);
        // The retry re-read the row rather than re-saving the copy the first attempt had already
        // mutated - the same trap `6-08` found in EF's identity map.
        Assert.Equal(2, repository.LoadCount);
    }

    [Fact]
    public async Task HandleAsync_ConflictTwiceInARow_ReturnsConcurrencyConflictNotAnException()
    {
        // The operator must never see a raw failure for a race the server lost - `6-08`'s rule,
        // applied to this write.
        var id = new ConversationId(Guid.NewGuid());
        var repository = new ConflictingConversationRepository(
            () => AssignedConversationWithUnread(id, 2), failNextSaves: 2);
        var handler = new MarkConversationReadHandler(repository, Permissions(granted: true));

        var result = await handler.HandleAsync(
            new Application.UseCases.MarkConversationRead.MarkConversationRead(id, OperatorId, SiteId, UpToSequence: 2), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.ConcurrencyConflict", result.Error!.Value.Code);
        Assert.Equal(2, repository.SaveCount);
    }

    /// <summary>Holds exactly one conversation instance and counts saves - the ordinary case, where
    /// "reload" legitimately means "the same aggregate, nothing else has written to it".</summary>
    private sealed class CountingConversationRepository(Conversation conversation) : IConversationRepository
    {
        public int SaveCount { get; private set; }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult(id == conversation.Id ? conversation : null);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Makes the first <c>failNextSaves</c> saves lose an optimistic-concurrency race, and -
    /// crucially - hands back a <em>freshly built</em> aggregate on every load, the way a real reload
    /// after <c>ChangeTracker.Clear()</c> does. Reusing the already-mutated instance would let this
    /// test pass for the wrong reason: the retry would find the watermark already moved, take the
    /// no-op path, and never exercise a second save at all.</summary>
    private sealed class ConflictingConversationRepository(Func<Conversation> load, int failNextSaves)
        : IConversationRepository
    {
        private int _failuresRemaining = failNextSaves;

        public int SaveCount { get; private set; }

        public int LoadCount { get; private set; }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult<Conversation?>(load());
        }

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new ConversationConcurrencyConflictException(conversation.Id);
            }

            return Task.CompletedTask;
        }
    }
}
