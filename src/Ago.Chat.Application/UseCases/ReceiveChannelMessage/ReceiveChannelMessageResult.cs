using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ReceiveChannelMessage;

/// <summary>
/// What an adapter gets back once its inbound message has become an ordinary AGO Chat message.
///
/// <para><paramref name="Sequence"/> is the server-assigned per-conversation order
/// (<c>SendVisitorMessageHandler</c> forwards the pipeline's own result) - returned here so a channel
/// adapter can log or correlate against the only ordering this system recognises. On a redelivery it
/// is the <em>original</em> message's sequence, not a new one, which is the observable proof that
/// deduplication happened rather than a second write.</para>
/// </summary>
public sealed record ReceiveChannelMessageResult(
    VisitorId VisitorId, ConversationId ConversationId, int Sequence, bool VisitorWasNew);
