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
        Conversation Conversation);

    private static Fixture CreateFixture(
        IRateLimiter? rateLimiter = null, bool grantOperatorPermission = true, bool assignOperator = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        if (assignOperator)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        conversations.Seed(conversation);

        var attachments = new FakeAttachmentRepository();
        var fileStorage = new FakeFileStorage();
        var permissions = new FakePermissionChecker();
        if (grantOperatorPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var handler = new CreateAttachmentHandler(
            conversations,
            attachments,
            fileStorage,
            rateLimiter ?? new FakeRateLimiter(),
            permissions,
            new AttachmentOptions(),
            new AttachmentRateLimitOptions(),
            new FakeIdGenerator(),
            new FakeClock(Now));

        return new Fixture(handler, conversations, attachments, fileStorage, conversation);
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
}
