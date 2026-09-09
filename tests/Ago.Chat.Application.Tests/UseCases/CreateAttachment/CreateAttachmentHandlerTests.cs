using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.CreateAttachment;

public class CreateAttachmentHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CreateAttachmentHandler Handler,
        FakeConversationRepository Conversations,
        FakeAttachmentRepository Attachments,
        FakeFileStorage FileStorage,
        FakeConversationAttachmentBudget Budget,
        FakeUnitOfWork UnitOfWork,
        Conversation Conversation);

    private static Fixture CreateFixture(
        IRateLimiter? rateLimiter = null, bool grantOperatorPermission = true, bool assignOperator = true,
        AttachmentOptions? options = null, bool grantAttachmentUpload = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        if (assignOperator)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        // `23-78`: granted by default here, the same "existing tests keep proving what they always
        // proved" posture every additive fixture flag in this codebase takes - the gate itself gets
        // its own tests below with `grantAttachmentUpload: false`, rather than every pre-existing
        // "the checks pass" test having to learn about a control unrelated to what it is asserting.
        if (grantAttachmentUpload)
        {
            conversation.MarkAttachmentUploadGrantedForTesting(OperatorId, Now);
        }

        conversations.Seed(conversation);

        var attachments = new FakeAttachmentRepository();
        var fileStorage = new FakeFileStorage();
        var permissions = new FakePermissionChecker();
        if (grantOperatorPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var budget = new FakeConversationAttachmentBudget();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new CreateAttachmentHandler(
            conversations,
            attachments,
            fileStorage,
            rateLimiter ?? new FakeRateLimiter(),
            permissions,
            budget,
            unitOfWork,
            options ?? new AttachmentOptions(),
            new AttachmentRateLimitOptions(),
            new FakeIdGenerator(),
            new FakeClock(Now));

        return new Fixture(handler, conversations, attachments, fileStorage, budget, unitOfWork, conversation);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheChecksPass_PresignsAndSavesAPendingAttachment()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.FileStorage.CreateUploadCalls);
        var saved = await fixture.Attachments.GetByIdAsync(new AttachmentId(result.Value.AttachmentId), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(AttachmentState.Pending, saved.State);
        Assert.Equal(fixture.Conversation.Id, saved.ConversationId);

        // `23-75`: the reservation and the pending row's own save happen inside one committed
        // transaction - see the handler's own remarks for why (CLAUDE.md rule 8, and why presigning
        // is folded inside it rather than run before it).
        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
        Assert.Single(fixture.Budget.ReserveCalls);
        Assert.Equal((fixture.Conversation.Id, 1024L, new AttachmentOptions().MaxConversationBytes), fixture.Budget.ReserveCalls[0]);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNotTheConversationsVisitor_ReturnsForbidden_WithoutPresigning()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, someoneElse, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    // `23-78`: the control itself - a conversation with no attachment-upload grant refuses the
    // visitor-side presigned slot, `Attachment.UploadNotGranted`, distinct from every other Forbidden
    // this handler can return.
    [Fact]
    public async Task HandleAsVisitorAsync_WhenNoAttachmentUploadGrant_ReturnsUploadNotGranted_WithoutPresigning()
    {
        var fixture = CreateFixture(grantAttachmentUpload: false);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.UploadNotGranted", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    // `23-78`: checked before the rate limiter, not after - an anonymous flood against an ungranted
    // conversation must not cost this visitor's own rate-limit bucket anything (the handler's own
    // remarks). Proven here with a rate limiter that would deny anyway: if the grant check ran after
    // it, this would surface as `Message.RateLimited`, not `Attachment.UploadNotGranted`.
    [Fact]
    public async Task HandleAsVisitorAsync_WhenNoAttachmentUploadGrant_ReturnsUploadNotGranted_EvenIfTheRateLimiterWouldAlsoDeny()
    {
        var fixture = CreateFixture(grantAttachmentUpload: false, rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(5)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.UploadNotGranted", result.Error!.Value.Code);
    }

    // `23-78`'s own Scope: "an operator's own uploads are unaffected" - the identical conversation,
    // with no attachment-upload grant, still lets the assigned operator presign.
    [Fact]
    public async Task HandleAsOperatorAsync_WhenNoAttachmentUploadGrant_StillPresigns()
    {
        var fixture = CreateFixture(grantAttachmentUpload: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new CreateAttachmentAsOperator(fixture.Conversation.Id, OperatorId, SiteId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.FileStorage.CreateUploadCalls);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WithoutPermission_ReturnsForbidden_WithoutPresigning()
    {
        var fixture = CreateFixture(grantOperatorPermission: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new CreateAttachmentAsOperator(fixture.Conversation.Id, OperatorId, SiteId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenNotAssignedToTheConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture(assignOperator: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new CreateAttachmentAsOperator(fixture.Conversation.Id, OperatorId, SiteId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenThePerVisitorRateLimitIsExceeded_ReturnsRateLimited_WithoutPresigning()
    {
        var fixture = CreateFixture(new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(3)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenOnlyThePerSiteRateLimitIsExceeded_ReturnsRateLimited_WithoutPresigning()
    {
        var fixture = CreateFixture(new SelectiveFakeRateLimiter("site", TimeSpan.FromSeconds(3)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheContentTypeIsNotAllowed_ReturnsInvalidContentType()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "application/x-msdownload", 1024), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.InvalidContentType", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheDeclaredSizeExceedsTheCeiling_ReturnsTooLarge()
    {
        var fixture = CreateFixture();
        var options = new AttachmentOptions();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", options.MaxSizeBytes + 1), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.TooLarge", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }

    /// <summary>`23-75`'s own Done-when: a refusal names the remaining budget, not just "no" - and
    /// nothing committed. The fake reproduces the real store's compare-and-set faithfully (see its own
    /// remarks); the real concurrency proof lives in
    /// <c>Ago.Chat.Integration.Tests.ConversationAttachmentBudgetStoreTests</c> and
    /// <c>AttachmentConversationBudgetFlowTests</c>, against real Postgres.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheConversationBudgetIsExceeded_ReturnsBudgetExceeded_NamesTheRemainder_AndDoesNotPresignOrCommit()
    {
        var options = new AttachmentOptions { MaxConversationBytes = 1000 };
        var fixture = CreateFixture(options: options);
        fixture.Budget.SeedReserved(fixture.Conversation.Id, 900);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(fixture.Conversation.Id, VisitorId, "image/png", 200), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.ConversationBudgetExceeded", result.Error!.Value.Code);
        // 1000 - 900 remaining - the exact phrase, not a bare Contains("100") a wrong value like
        // "1000" or "100000" would also satisfy.
        Assert.Contains("100 byte(s) remaining", result.Error.Value.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
        Assert.Equal(0, fixture.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>`23-75`'s own Scope: "spent by visitor and operator alike" - the identical refusal for
    /// the operator entry point, proving the budget is not a visitor-only check.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheConversationBudgetIsExceeded_ReturnsBudgetExceeded_WithoutPresigning()
    {
        var options = new AttachmentOptions { MaxConversationBytes = 1000 };
        var fixture = CreateFixture(options: options);
        fixture.Budget.SeedReserved(fixture.Conversation.Id, 1000);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new CreateAttachmentAsOperator(fixture.Conversation.Id, OperatorId, SiteId, "image/png", 1), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.ConversationBudgetExceeded", result.Error!.Value.Code);
        Assert.Equal(0, fixture.FileStorage.CreateUploadCalls);
    }
}
