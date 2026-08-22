using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.ConfirmAttachment;

public class ConfirmAttachmentHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        ConfirmAttachmentHandler Handler, FakeAttachmentRepository Attachments, FakeFileStorage FileStorage, Attachment Attachment);

    private static Fixture CreateFixture()
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(conversation);

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), SiteId, conversation.Id, "site/x/conv/y/z.png", "image/png", 42, Now);
        var attachments = new FakeAttachmentRepository();
        attachments.Seed(attachment);

        var fileStorage = new FakeFileStorage();
        var handler = new ConfirmAttachmentHandler(
            attachments, conversations, fileStorage, new FakePermissionChecker(), new FakeClock(Now));

        return new Fixture(handler, attachments, fileStorage, attachment);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheUploadedObjectMatches_ConfirmsAndFlipsToReady()
    {
        var fixture = CreateFixture();
        fixture.FileStorage.SetMetadata(new ObjectKey(fixture.Attachment.ObjectKey), new ObjectMetadata(42, "image/png"));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(AttachmentState.Ready, saved!.State);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNoObjectWasEverUploaded_FailsAndStaysPending()
    {
        var fixture = CreateFixture();
        // No SetMetadata call - GetMetadataAsync returns null, exactly a HEAD against an object that
        // was never PUT.

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.VerificationFailed", result.Error!.Value.Code);
        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(AttachmentState.Pending, saved!.State);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheUploadedSizeDoesNotMatch_FailsAndStaysPending()
    {
        var fixture = CreateFixture();
        fixture.FileStorage.SetMetadata(new ObjectKey(fixture.Attachment.ObjectKey), new ObjectMetadata(999, "image/png"));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.VerificationFailed", result.Error!.Value.Code);
        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(AttachmentState.Pending, saved!.State);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNotAParticipant_ReturnsForbidden_WithoutCheckingStorage()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(fixture.Attachment.Id, someoneElse), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_ForAnUnknownAttachment_ReturnsAttachmentNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(new AttachmentId(Guid.NewGuid()), VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotFound", result.Error!.Value.Code);
    }
}
