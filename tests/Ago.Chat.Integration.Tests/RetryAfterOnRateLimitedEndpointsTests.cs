using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Attachments;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Api.PhoneVerification;
using Ago.Chat.Api.ReplyDraft;
using Ago.Chat.Api.Sites;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GenerateReplyDraft;
using Ago.Chat.Application.UseCases.InitiatePhoneVerification;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `ago-root#353`: proves the actual wiring for each of the five rate-limited HTTP endpoints - that a
/// `*.RateLimited` failure carries a `Retry-After` header whose value is
/// <see cref="RateLimitRetryAfter.Conservative"/> applied to the same options object the handler
/// itself used to make its decision. Each endpoint's own <c>Handle*Async</c> method, called directly
/// with a `DefaultHttpContext` and a real handler wired to fakes that deny on the first bucket
/// checked - `DemoEndpointRateLimitTests`' own precedent (no hosting pipeline, no Testcontainers).
///
/// <para><b>Why fakes reach the deny path with nothing else touched.</b> Every handler under test here
/// checks its rate limiter before any dependency a "NeverCalled" stub below stands in for - each
/// test's own comment names the one real (or canned) dependency the handler reaches on its way to that
/// check, and throws from everything past it. A stub that is unexpectedly reached fails the test with
/// its own message, not a null-reference a reader would have to trace back.</para>
///
/// <para><b>Proof that no path asks the rate limiter a second time.</b> None of the five `Handle*Async`
/// methods below - <c>AttachmentEndpoints.HandleCreateAsync</c>, <c>SitesEndpoints.HandleRegisterSiteAsync</c>/
/// <c>HandleRequestExportAsync</c>, <c>ReplyDraftEndpoints.HandleGenerateAsync</c>,
/// <c>PhoneVerificationEndpoints.HandleInitiateAsync</c> - take an <see cref="IRateLimiter"/> parameter
/// at all after this item's own change (`RateLimitRetryAfter.Conservative` is pure configuration math);
/// the *only* <see cref="IRateLimiter"/> in any of these tests is the one <see cref="RateLimitedFakeRateLimiter"/>
/// each handler itself holds, checked exactly as many times as the handler's own code already checked
/// it before this item - never touched by the endpoint layer this item changed.</para>
/// </summary>
public sealed class RetryAfterOnRateLimitedEndpointsTests
{
    [Fact]
    public async Task AttachmentCreate_VisitorBucketDenied_Returns429WithTheConservativeRetryAfter()
    {
        var conversationId = new ConversationId(Guid.NewGuid());
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        // The one real dependency HandleAsVisitorAsync reaches before its own rate-limit check: the
        // conversation lookup that proves this visitor is a participant.
        var conversation = Conversation.Start(conversationId, siteId, visitorId, DateTimeOffset.UtcNow);

        var rateLimitOptions = new AttachmentRateLimitOptions
        {
            PerVisitorCapacity = 5,
            PerVisitorRefillPerSecond = 5.0 / 60,
            PerOperatorCapacity = 20,
            PerOperatorRefillPerSecond = 20.0 / 60,
            PerSiteCapacity = 50,
            PerSiteRefillPerSecond = 50.0 / 60,
        };
        var handler = new CreateAttachmentHandler(
            new SingleConversationRepository(conversation),
            new NeverCalledAttachmentRepository(),
            new NeverCalledFileStorage(),
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(732)),
            new NeverCalledPermissionChecker(),
            new AttachmentOptions(),
            rateLimitOptions,
            new UuidV7Generator(),
            new SystemClock());

        var httpContext = NewHttpContext();
        httpContext.User = VisitorPrincipal(visitorId);

        var result = await AttachmentEndpoints.HandleCreateAsync(
            conversationId.Value,
            new AttachmentEndpoints.CreateAttachmentRequest("image/png", 1024),
            handler,
            rateLimitOptions,
            httpContext,
            CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        AssertRetryAfterEquals(
            httpContext,
            RateLimitRetryAfter.Conservative(
                rateLimitOptions.PerVisitorRefillPerSecond,
                rateLimitOptions.PerOperatorRefillPerSecond,
                rateLimitOptions.PerSiteRefillPerSecond));
    }

    [Fact]
    public async Task RegisterSite_SubjectBucketDenied_Returns429WithTheConservativeRetryAfter()
    {
        // Rate limit checked before any database work at all (RegisterSiteHandler's own remarks) -
        // ISiteRegistrationRepository is never called.
        var rateLimitOptions = new RegisterSiteRateLimitOptions
        {
            PerSubjectCapacity = 3,
            PerSubjectRefillPerSecond = 3.0 / 3600,
            PerIpCapacity = 10,
            PerIpRefillPerSecond = 10.0 / 3600,
        };
        var handler = new RegisterSiteHandler(
            new NeverCalledSiteRegistrationRepository(),
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(1199)),
            rateLimitOptions,
            new UuidV7Generator(),
            new SystemClock());

        var httpContext = NewHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, "keycloak-subject-under-test")], "TestScheme"));

        var result = await SitesEndpoints.HandleRegisterSiteAsync(
            new SitesEndpoints.RegisterSiteRequest("A Site", "https://example.test"),
            handler,
            rateLimitOptions,
            httpContext,
            CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        AssertRetryAfterEquals(
            httpContext,
            RateLimitRetryAfter.Conservative(rateLimitOptions.PerSubjectRefillPerSecond, rateLimitOptions.PerIpRefillPerSecond));
    }

    [Fact]
    public async Task RequestSiteExport_SiteBucketDenied_Returns429WithTheConservativeRetryAfter()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        // Permission checked before the rate limit here (RequestSiteExportHandler's own remarks, the
        // deliberately reversed ordering from RegisterSiteHandler/CreateAttachmentHandler) - the one
        // real dependency reached is IPermissionChecker, allowed unconditionally.
        var rateLimitOptions = new SiteExportRateLimitOptions { PerSiteCapacity = 3, PerSiteRefillPerSecond = 1.0 / 3600 };
        var handler = new RequestSiteExportHandler(
            new NeverCalledExportRequestRepository(),
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(3599)),
            new AllowAllPermissionChecker(),
            rateLimitOptions,
            new UuidV7Generator(),
            new SystemClock());

        var httpContext = NewHttpContext();
        httpContext.User = OperatorPrincipal(operatorId, siteId);

        var result = await SitesEndpoints.HandleRequestExportAsync(
            siteId.Value, handler, rateLimitOptions, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        AssertRetryAfterEquals(httpContext, RateLimitRetryAfter.Conservative(rateLimitOptions.PerSiteRefillPerSecond));
    }

    [Fact]
    public async Task GenerateReplyDraft_OperatorBucketDenied_Returns429WithTheConservativeRetryAfter()
    {
        var conversationId = new ConversationId(Guid.NewGuid());
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        // Permission, then the conversation lookup (assigned-operator check), both before the rate
        // limit (GenerateReplyDraftHandler's own ordering) - readStore/generator are never reached.
        var conversation = Conversation.Start(conversationId, siteId, visitorId, DateTimeOffset.UtcNow);
        conversation.AssignTo(operatorId, DateTimeOffset.UtcNow);

        var rateLimitOptions = new ReplyDraftRateLimitOptions
        {
            PerOperatorCapacity = 10,
            PerOperatorRefillPerSecond = 10.0 / 3600,
            PerSiteCapacity = 30,
            PerSiteRefillPerSecond = 30.0 / 3600,
        };
        var handler = new GenerateReplyDraftHandler(
            new SingleConversationRepository(conversation),
            new NeverCalledConversationReadStore(),
            new AllowAllPermissionChecker(),
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(359)),
            new NeverCalledReplyDraftGenerator(),
            new ReplyDraftOptions(),
            rateLimitOptions);

        var httpContext = NewHttpContext();
        httpContext.User = OperatorPrincipal(operatorId, siteId);

        var result = await ReplyDraftEndpoints.HandleGenerateAsync(
            conversationId.Value, handler, rateLimitOptions, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        AssertRetryAfterEquals(
            httpContext,
            RateLimitRetryAfter.Conservative(rateLimitOptions.PerOperatorRefillPerSecond, rateLimitOptions.PerSiteRefillPerSecond));
    }

    [Fact]
    public async Task InitiatePhoneVerification_PhoneBucketDenied_Returns429WithTheConservativeRetryAfter()
    {
        var conversationId = new ConversationId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        // Phone number parsing is the only work before the rate limit (InitiatePhoneVerificationHandler's
        // own remarks: "both checkable before any database read") - conversations/pendingVerifications/
        // codeGenerator/outbox are never reached.
        var rateLimitOptions = new PhoneVerificationRateLimitOptions
        {
            PerPhoneCapacity = 3,
            PerPhoneRefillPerSecond = 3.0 / 3600,
            PerVisitorCapacity = 5,
            PerVisitorRefillPerSecond = 5.0 / 3600,
            PerSiteCapacity = 100,
            PerSiteRefillPerSecond = 100.0 / 3600,
        };
        var handler = new InitiatePhoneVerificationHandler(
            new NeverCalledConversationRepository(),
            new NeverCalledPendingPhoneVerificationRepository(),
            new NeverCalledPendingChannelLinkCodeGenerator(),
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(1199)),
            new NeverCalledOutboxWriter(),
            new PhoneVerificationOptions(),
            rateLimitOptions,
            new UuidV7Generator(),
            new SystemClock());

        var httpContext = NewHttpContext();
        httpContext.User = VisitorPrincipal(visitorId);

        var result = await PhoneVerificationEndpoints.HandleInitiateAsync(
            conversationId.Value,
            new PhoneVerificationEndpoints.InitiatePhoneVerificationRequest("+15551234567"),
            handler,
            rateLimitOptions,
            httpContext,
            CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        AssertRetryAfterEquals(
            httpContext,
            RateLimitRetryAfter.Conservative(
                rateLimitOptions.PerPhoneRefillPerSecond,
                rateLimitOptions.PerVisitorRefillPerSecond,
                rateLimitOptions.PerSiteRefillPerSecond));
    }

    private static DefaultHttpContext NewHttpContext()
    {
        // Result.ExecuteAsync (ProblemHttpResult included) resolves services off
        // HttpContext.RequestServices to serialize the response - DefaultHttpContext leaves it null by
        // default, since this is normally supplied by the real ASP.NET Core pipeline
        // (RateLimitingTests'/DemoEndpointRateLimitTests' own precedent for this exact minimal set).
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

    private static ClaimsPrincipal OperatorPrincipal(OperatorId operatorId, SiteId siteId) =>
        new(new ClaimsIdentity(
            [
                new Claim(AgoClaimTypes.OperatorId, operatorId.Value.ToString()),
                new Claim(AgoClaimTypes.SiteId, siteId.Value.ToString()),
                new Claim(AgoClaimTypes.Kind, AgoClaimTypes.OperatorKind),
            ],
            "TestScheme"));

    private static void AssertRetryAfterEquals(DefaultHttpContext httpContext, TimeSpan expected)
    {
        var header = httpContext.Response.Headers.RetryAfter.FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(header), "Retry-After header was not set.");
        var expectedSeconds = Math.Max(1, (int)Math.Ceiling(expected.TotalSeconds));
        Assert.Equal(expectedSeconds.ToString(), header);
    }

    private sealed class SingleConversationRepository(Conversation conversation) : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult(id == conversation.Id ? conversation : null);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task SaveAsync(Conversation conversationToSave, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach a save.");
    }

    private sealed class NeverCalledConversationRepository : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A phone-bucket-denied caller must never reach the conversation lookup.");

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task SaveAsync(Conversation conversationToSave, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");
    }

    private sealed class NeverCalledAttachmentRepository : IAttachmentRepository
    {
        public Task<Attachment?> GetByIdAsync(AttachmentId id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach an attachment lookup.");

        public Task SaveAsync(Attachment attachment, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach a save.");
    }

    private sealed class NeverCalledFileStorage : IFileStorage
    {
        public Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach object storage.");

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");
    }

    private sealed class NeverCalledPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A visitor path must never reach the operator permission check.");

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        // `23-26`: same "a visitor path must never reach this" contract as HasPermissionAsync above.
        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");
    }

    private sealed class AllowAllPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        // `23-26`: not part of the rate-limited path under test either - RemoveOperator is not one of
        // the endpoints this suite exercises.
        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");
    }

    private sealed class NeverCalledSiteRegistrationRepository : ISiteRegistrationRepository
    {
        public Task<bool> TryRegisterAsync(SiteRegistration registration, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach registration.");
    }

    private sealed class NeverCalledExportRequestRepository : IExportRequestRepository
    {
        public Task<bool> CreateAsync(
            Guid exportId, SiteId siteId, OperatorId requestedBy, DateTimeOffset requestedAt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach export creation.");

        public Task<ExportRequestRecord?> GetAsync(Guid exportId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");
    }

    private sealed class NeverCalledConversationReadStore : IConversationReadStore
    {
        public Task<ConversationHistoryPage> GetHistoryAsync(
            ConversationId conversationId, SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach the read store.");

        public Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
            ConversationId conversationId, SiteId siteId, int afterSequence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<ConversationListPage> GetAllForSiteAsync(
            SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<ConversationSummaryItem?> GetByIdAsync(ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<VisitorHistoryPage> GetVisitorHistoryAsync(
            VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<DateTimeOffset?> GetMostRecentCreatedAtAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task<IReadOnlyList<ConversationId>> ListAllForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");
    }

    private sealed class NeverCalledReplyDraftGenerator : IReplyDraftGenerator
    {
        public Task<ReplyDraftGenerationResult> GenerateDraftAsync(ReplyDraftGenerationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach the LLM provider.");
    }

    private sealed class NeverCalledPendingPhoneVerificationRepository : IPendingPhoneVerificationRepository
    {
        public Task<PendingPhoneVerification?> GetByIdAsync(PendingPhoneVerificationId id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the rate-limited path under test.");

        public Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach a save.");
    }

    private sealed class NeverCalledPendingChannelLinkCodeGenerator : IPendingChannelLinkCodeGenerator
    {
        public string NewCode() => throw new InvalidOperationException("A rate-limited caller must never reach code generation.");
    }

    private sealed class NeverCalledOutboxWriter : IOutboxWriter
    {
        public void Enqueue(EventEnvelope envelope, string? traceContext = null) =>
            throw new InvalidOperationException("A rate-limited caller must never reach the outbox.");
    }
}
