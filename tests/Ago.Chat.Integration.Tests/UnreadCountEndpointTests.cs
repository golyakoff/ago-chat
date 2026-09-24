using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Conversations;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-143`: proves `UnreadCountEndpoints.HandleGetUnreadCountAsync`'s own wiring end to end without a
/// hosting pipeline - the visitor claim read off the validated principal reaches
/// `GetConversationHistoryHandler.HandleUnreadCountAsVisitorAsync` unchanged, and that handler's own
/// `Result` maps onto the wire correctly. Same "call the public `Handle*Async` method directly with a
/// `DefaultHttpContext` and hand-built fakes, no hosting pipeline" shape
/// <see cref="RetryAfterOnRateLimitedEndpointsTests"/> already establishes in this same test project for
/// four sibling endpoints - the access-check *logic* itself (owns it / does not / does not exist) is
/// already proven at the Application layer by `GetConversationHistoryHandlerTests`
/// (`Ago.Chat.Application.Tests`); what this file adds is the one thing that layer cannot see - that the
/// HTTP verb, the claim extraction and the status-code mapping are actually wired together.
/// </summary>
public sealed class UnreadCountEndpointTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task WhenTheVisitorOwnsTheConversation_Returns200WithTheCount()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        var handler = new GetConversationHistoryHandler(
            new SingleConversationRepository(conversation), new CannedUnreadCountReadStore(3), new NeverCalledPermissionChecker());

        var httpContext = NewHttpContext();
        httpContext.User = VisitorPrincipal(visitorId);

        var result = await UnreadCountEndpoints.HandleGetUnreadCountAsync(
            conversation.Id.Value, afterSequence: 5, handler, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    /// <summary>The Scope's own default: "omit it entirely for a visitor who has never had one" - a
    /// missing `afterSequence` must reach the read store as zero, not fail model binding.</summary>
    [Fact]
    public async Task WhenAfterSequenceIsOmitted_TreatsItAsZero()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        var readStore = new RecordingUnreadCountReadStore(7);
        var handler = new GetConversationHistoryHandler(
            new SingleConversationRepository(conversation), readStore, new NeverCalledPermissionChecker());

        var httpContext = NewHttpContext();
        httpContext.User = VisitorPrincipal(visitorId);

        var result = await UnreadCountEndpoints.HandleGetUnreadCountAsync(
            conversation.Id.Value, afterSequence: null, handler, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal(0, readStore.LastAfterSequence);
    }

    [Fact]
    public async Task WhenTheConversationBelongsToAnotherVisitor_Returns403()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var owningVisitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, owningVisitorId, Now);
        var handler = new GetConversationHistoryHandler(
            new SingleConversationRepository(conversation), new NeverCalledUnreadCountReadStore(), new NeverCalledPermissionChecker());

        var httpContext = NewHttpContext();
        httpContext.User = VisitorPrincipal(new VisitorId(Guid.NewGuid()));

        var result = await UnreadCountEndpoints.HandleGetUnreadCountAsync(
            conversation.Id.Value, afterSequence: null, handler, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task WhenTheConversationDoesNotExist_Returns404()
    {
        var handler = new GetConversationHistoryHandler(
            new EmptyConversationRepository(), new NeverCalledUnreadCountReadStore(), new NeverCalledPermissionChecker());

        var httpContext = NewHttpContext();
        httpContext.User = VisitorPrincipal(new VisitorId(Guid.NewGuid()));

        var result = await UnreadCountEndpoints.HandleGetUnreadCountAsync(
            Guid.NewGuid(), afterSequence: null, handler, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        // `RetryAfterOnRateLimitedEndpointsTests.NewHttpContext`'s own precedent: Result.ExecuteAsync
        // (ProblemHttpResult included) resolves services off HttpContext.RequestServices to serialize
        // the response - DefaultHttpContext leaves it null by default.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new JsonOptions()));
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
    }

    private static ClaimsPrincipal VisitorPrincipal(VisitorId visitorId) =>
        new(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, visitorId.Value.ToString())], "TestScheme"));

    private sealed class SingleConversationRepository(Conversation conversation) : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult(id == conversation.Id ? conversation : null);

        public Task<IReadOnlyDictionary<ConversationId, Conversation>> GetByIdsAsync(
            IReadOnlyCollection<ConversationId> ids, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task SaveAsync(Conversation conversationToSave, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A read-only endpoint must never save.");
    }

    private sealed class EmptyConversationRepository : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(null);

        public Task<IReadOnlyDictionary<ConversationId, Conversation>> GetByIdsAsync(
            IReadOnlyCollection<ConversationId> ids, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task SaveAsync(Conversation conversationToSave, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A read-only endpoint must never save.");
    }

    private sealed class CannedUnreadCountReadStore(int count) : IConversationReadStore
    {
        public Task<ConversationHistoryPage> GetHistoryAsync(
            ConversationId conversationId, SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<int> GetUnreadCountAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken) =>
            Task.FromResult(count);

        public Task<ConversationListPage> GetAllForSiteAsync(
            SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<ConversationSummaryItem?> GetByIdAsync(ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<VisitorHistoryPage> GetVisitorHistoryAsync(
            VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<DateTimeOffset?> GetMostRecentCreatedAtAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<ConversationId>> ListAllForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyDictionary<ConversationId, LatestMessageSummary>> GetLatestMessagesAsync(
            SiteId siteId, IReadOnlyCollection<ConversationId> conversationIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");
    }

    /// <summary>Records the exact `afterSequence` the handler passed through, so
    /// <see cref="WhenAfterSequenceIsOmitted_TreatsItAsZero"/> can assert on it directly rather than
    /// inferring it from the response body.</summary>
    private sealed class RecordingUnreadCountReadStore(int count) : IConversationReadStore
    {
        public int? LastAfterSequence { get; private set; }

        public Task<ConversationHistoryPage> GetHistoryAsync(
            ConversationId conversationId, SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<int> GetUnreadCountAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken)
        {
            LastAfterSequence = afterSequence;
            return Task.FromResult(count);
        }

        public Task<ConversationListPage> GetAllForSiteAsync(
            SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<ConversationSummaryItem?> GetByIdAsync(ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<VisitorHistoryPage> GetVisitorHistoryAsync(
            VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<DateTimeOffset?> GetMostRecentCreatedAtAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<ConversationId>> ListAllForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyDictionary<ConversationId, LatestMessageSummary>> GetLatestMessagesAsync(
            SiteId siteId, IReadOnlyCollection<ConversationId> conversationIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");
    }

    private sealed class NeverCalledUnreadCountReadStore : IConversationReadStore
    {
        public Task<ConversationHistoryPage> GetHistoryAsync(
            ConversationId conversationId, SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<int> GetUnreadCountAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A caller refused before the read store must never reach it.");

        public Task<ConversationListPage> GetAllForSiteAsync(
            SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<ConversationSummaryItem?> GetByIdAsync(ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<VisitorHistoryPage> GetVisitorHistoryAsync(
            VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<DateTimeOffset?> GetMostRecentCreatedAtAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyList<ConversationId>> ListAllForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");

        public Task<IReadOnlyDictionary<ConversationId, LatestMessageSummary>> GetLatestMessagesAsync(
            SiteId siteId, IReadOnlyCollection<ConversationId> conversationIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of this endpoint's own read path.");
    }

    private sealed class NeverCalledPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A visitor-only read must never check operator permissions.");

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A visitor-only read must never check operator permissions.");

        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A visitor-only read must never check operator permissions.");

        public Task<IReadOnlyList<OperatorId>> ListNonRemovedHolderIdsAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A visitor-only read must never check operator permissions.");
    }
}
