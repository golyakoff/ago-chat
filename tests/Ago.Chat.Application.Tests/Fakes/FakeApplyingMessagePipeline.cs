using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// <see cref="FakeMessagePipeline"/>'s louder sibling: instead of recording the
/// <see cref="PendingMessage"/> and answering a canned sequence, this one actually applies it to the
/// conversation - load, <c>AddVisitorMessage</c>/<c>AddOperatorMessage</c>, save, return the assigned
/// sequence. A faithful miniature of <c>Ago.Chat.Api.Pipeline.MessageBatchWriter</c>, minus the
/// batching, the outbox and the channel.
///
/// <para><b>Why this exists rather than reusing the canned fake.</b> The behaviour `14-01` has to
/// prove - a redelivered inbound message does not become a second message - is enforced inside
/// <c>Conversation.AddMessage</c>, which the canned fake never reaches. A test built on the canned
/// fake could only assert "the same ClientMessageId was passed twice", which is a statement about the
/// handler's arguments, not about the outcome anyone cares about. testing.md's division still holds:
/// the real batch writer, the real channel and real Postgres are proven in
/// <c>Ago.Chat.Integration.Tests</c>; what this proves is that the mapping feeds that machinery the
/// right thing and that the machinery's own dedup then applies unchanged.</para>
/// </summary>
public sealed class FakeApplyingMessagePipeline(
    FakeConversationRepository conversations, IClock clock, IIdGenerator idGenerator) : IMessagePipeline
{
    private readonly List<PendingMessage> _enqueued = [];

    public IReadOnlyList<PendingMessage> Enqueued => _enqueued;

    public async Task<Result<int>> EnqueueAsync(PendingMessage message, CancellationToken cancellationToken)
    {
        _enqueued.Add(message);

        var conversation = await conversations.GetByIdAsync(message.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return new Error("conversation.not_found", $"Conversation {message.ConversationId.Value} was not found.");
        }

        var now = clock.UtcNow;
        var messageId = new MessageId(idGenerator.NewId(now));
        var written = message.AuthorKind == MessageAuthorKind.Visitor
            ? conversation.AddVisitorMessage(
                new VisitorId(message.AuthorId), messageId, message.Body, now,
                message.AttachmentId, message.ClientMessageId)
            : conversation.AddOperatorMessage(
                new OperatorId(message.AuthorId), messageId, message.Body, now,
                message.AttachmentId, message.ClientMessageId);

        await conversations.SaveAsync(conversation, cancellationToken);
        return written.Sequence;
    }
}
