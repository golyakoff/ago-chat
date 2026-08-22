namespace Ago.Chat.Domain.Tests;

public class AttachmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    private static Attachment CreatePending(long sizeBytes = 100, string contentType = "image/png") =>
        Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), SiteId, ConversationId, "site/x/conv/y/z.png", contentType, sizeBytes, Now);

    [Fact]
    public void CreatePending_StartsInPendingState_WithNoMessageLinked()
    {
        var attachment = CreatePending();

        Assert.Equal(AttachmentState.Pending, attachment.State);
        Assert.Null(attachment.MessageId);
    }

    [Fact]
    public void ConfirmReady_WhenTheVerifiedMetadataMatches_TransitionsToReady()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");

        attachment.ConfirmReady(42, "image/png", Now);

        Assert.Equal(AttachmentState.Ready, attachment.State);
    }

    [Fact]
    public void ConfirmReady_WhenTheVerifiedSizeDoesNotMatch_ThrowsAndStaysPending()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");

        Assert.Throws<AttachmentVerificationMismatchException>(() => attachment.ConfirmReady(99, "image/png", Now));
        Assert.Equal(AttachmentState.Pending, attachment.State);
    }

    [Fact]
    public void ConfirmReady_WhenTheVerifiedContentTypeDoesNotMatch_ThrowsAndStaysPending()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");

        Assert.Throws<AttachmentVerificationMismatchException>(() => attachment.ConfirmReady(42, "image/jpeg", Now));
        Assert.Equal(AttachmentState.Pending, attachment.State);
    }

    [Fact]
    public void ConfirmReady_WhenAlreadyReady_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);

        Assert.Throws<InvalidAttachmentStateException>(() => attachment.ConfirmReady(42, "image/png", Now));
    }

    [Fact]
    public void LinkToMessage_WhenReadyAndBelongsToTheConversation_SetsMessageId()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);
        var messageId = new MessageId(Guid.NewGuid());

        attachment.LinkToMessage(messageId, ConversationId);

        Assert.Equal(messageId, attachment.MessageId);
    }

    [Fact]
    public void LinkToMessage_WhenStillPending_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending();

        Assert.Throws<InvalidAttachmentStateException>(() =>
            attachment.LinkToMessage(new MessageId(Guid.NewGuid()), ConversationId));
    }

    [Fact]
    public void LinkToMessage_ForADifferentConversation_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);
        var otherConversation = new ConversationId(Guid.NewGuid());

        Assert.Throws<InvalidAttachmentStateException>(() =>
            attachment.LinkToMessage(new MessageId(Guid.NewGuid()), otherConversation));
    }

    [Fact]
    public void LinkToMessage_WhenAlreadyLinked_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);
        attachment.LinkToMessage(new MessageId(Guid.NewGuid()), ConversationId);

        Assert.Throws<InvalidAttachmentStateException>(() =>
            attachment.LinkToMessage(new MessageId(Guid.NewGuid()), ConversationId));
    }

    [Fact]
    public void MarkDeleted_TransitionsToDeleted()
    {
        var attachment = CreatePending();

        attachment.MarkDeleted();

        Assert.Equal(AttachmentState.Deleted, attachment.State);
    }

    [Fact]
    public void MarkDeleted_WhenAlreadyDeleted_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending();
        attachment.MarkDeleted();

        Assert.Throws<InvalidAttachmentStateException>(() => attachment.MarkDeleted());
    }

    [Fact]
    public void ConfirmReady_RaisesAttachmentReady()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");

        attachment.ConfirmReady(42, "image/png", Now);

        var raised = Assert.Single(attachment.DomainEvents);
        var ready = Assert.IsType<AttachmentReady>(raised);
        Assert.Equal(attachment.Id, ready.AttachmentId);
        Assert.Equal(SiteId, ready.SiteId);
        Assert.Equal(ConversationId, ready.ConversationId);
        Assert.Equal(attachment.ObjectKey, ready.ObjectKey);
        Assert.Equal("image/png", ready.ContentType);
    }

    [Fact]
    public void ClearDomainEvents_RemovesEverythingRaisedSoFar()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);

        attachment.ClearDomainEvents();

        Assert.Empty(attachment.DomainEvents);
    }

    [Fact]
    public void SetThumbnail_WhenReady_SetsThumbnailKey()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);

        attachment.SetThumbnail("site/x/conv/y/z_thumb.jpg");

        Assert.Equal("site/x/conv/y/z_thumb.jpg", attachment.ThumbnailKey);
    }

    [Fact]
    public void SetThumbnail_WhenStillPending_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending();

        Assert.Throws<InvalidAttachmentStateException>(() => attachment.SetThumbnail("site/x/conv/y/z_thumb.jpg"));
    }

    [Fact]
    public void SetThumbnail_WhenAlreadySet_ThrowsInvalidAttachmentStateException()
    {
        var attachment = CreatePending(sizeBytes: 42, contentType: "image/png");
        attachment.ConfirmReady(42, "image/png", Now);
        attachment.SetThumbnail("site/x/conv/y/z_thumb.jpg");

        Assert.Throws<InvalidAttachmentStateException>(() => attachment.SetThumbnail("site/x/conv/y/z_thumb2.jpg"));
    }
}
