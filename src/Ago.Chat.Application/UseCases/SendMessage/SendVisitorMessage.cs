using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary>
/// `14-06`: <see cref="ContentKind"/>/<see cref="Payload"/>/<see cref="Actions"/> arrive as the raw
/// strings a caller sent, not as a built <c>MessageContent</c> - the same shape <see cref="Body"/>
/// already has, and for the same reason: a command is what crossed the wire, and turning it into a
/// validated value object is the handler's job so that a malformed one is an ordinary rejection
/// rather than an exception crossing a layer.
///
/// <para>This is also the whole of the <b>return direction</b> for an action. A client that answered
/// a choice sends an ordinary message carrying its own structured content, with the chosen action's
/// value inside the producer's own payload. There is no second endpoint, because a second endpoint
/// would have to know which product to route to - the same knowledge in a different place.</para>
/// </summary>
/// <summary>
/// `23-64`/`adr/0148`: <paramref name="MaterializeAutoGreeting"/> is <see langword="true"/> for
/// exactly one call shape - <c>VisitorHub.SendMessageWithAutoGreetingAsync</c>, which the widget uses
/// instead of the ordinary send method for the one message per conversation that follows an
/// auto-opened panel it never connected for. This handler does no materialising itself - it only
/// forwards the flag onto <see cref="PendingMessage"/>, exactly as it already forwards every other
/// field, because the real decision (is this conversation still genuinely empty, does the site still
/// have a greeting configured) needs the fresh, in-transaction state <c>MessageBatchWriter</c> has and
/// this handler does not.
/// </summary>
public sealed record SendVisitorMessage(
    ConversationId ConversationId, VisitorId AuthorId, string Body, AttachmentId? AttachmentId = null,
    Guid? ClientMessageId = null, string? TraceParent = null,
    string? ContentKind = null, string? Payload = null, IReadOnlyList<MessageActionInput>? Actions = null,
    bool MaterializeAutoGreeting = false);
