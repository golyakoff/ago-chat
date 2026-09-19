using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ReceiveChannelAttachment;

/// <summary>
/// `25-161`: the attachment-carrying sibling of
/// <see cref="Ago.Chat.Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessage"/> - an inbound
/// message from an external channel (MAX today) whose content is bytes a visitor sent through that
/// platform's own native UI, not this system's widget. <see cref="ContentType"/>/<see cref="SizeBytes"/>
/// describe bytes the calling adapter has <em>already downloaded</em> from the provider by the time this
/// command exists - see <see cref="ReceiveChannelAttachmentHandler"/>'s own remarks for why the actual
/// bytes never cross into Application at all (CLAUDE.md rule 2: no <c>HttpClient</c> inside Application,
/// and uploading them is exactly that).
///
/// <para>No <see cref="ExternalMessageId"/> here, deliberately - unlike
/// <see cref="Ago.Chat.Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessage"/>, this command
/// only ever prepares an upload; the id that actually makes the resulting message idempotent is not
/// needed until <see cref="CompleteChannelAttachmentUpload"/>, once bytes are safely stored - see that
/// record's own remarks.</para>
/// </summary>
public sealed record PrepareChannelAttachmentUpload(
    SiteId SiteId, ChannelKind Kind, ExternalChannelAddress Sender, string ContentType, long SizeBytes);

/// <summary>
/// `25-161`: whether an inbound channel attachment may actually be uploaded - the two Done-when
/// outcomes this item's own backlog names. <see cref="Refused"/> means
/// <see cref="ReceiveChannelAttachmentHandler.PrepareAsync"/> has already told the visitor why (a system
/// message, over the same channel) - the caller has nothing left to do but stop, the same "the refusal
/// is already handled" shape a <see cref="Ready"/> outcome instead hands it a presigned URL to act on.
/// </summary>
public enum ChannelAttachmentPrepareOutcome
{
    Ready,
    Refused,
}

/// <summary>
/// `25-161`: <see cref="AttachmentId"/>/<see cref="VisitorId"/>/<see cref="ConversationId"/>/
/// <see cref="UploadUrl"/> are populated only when <see cref="Outcome"/> is
/// <see cref="ChannelAttachmentPrepareOutcome.Ready"/> - nullable rather than a second record type
/// because every caller already has to branch on <see cref="Outcome"/> first (the identical "one type,
/// nullable fields, the discriminator decides which are meaningful" shape
/// <c>Ago.Chat.Infrastructure.MaxBot.ParsedMaxMessage</c>'s own <c>Text</c>/<c>Contact</c>/<c>Image</c>
/// already use for the same reason).
/// </summary>
public sealed record PreparedChannelAttachmentUpload(
    ChannelAttachmentPrepareOutcome Outcome,
    AttachmentId? AttachmentId = null,
    VisitorId? VisitorId = null,
    ConversationId? ConversationId = null,
    Uri? UploadUrl = null);

/// <summary>
/// `25-161`: the second half of the protocol - called once the caller has actually uploaded the bytes
/// <see cref="PreparedChannelAttachmentUpload.UploadUrl"/> pointed at (a bare <c>HttpClient PUT</c>,
/// exactly as a browser would - <see cref="ReceiveChannelAttachmentHandler"/>'s own remarks on why that
/// step cannot happen inside Application). <see cref="ExternalMessageId"/>/<see cref="Kind"/> are what
/// let this step derive the same <c>ClientMessageId</c> idempotency key
/// <see cref="Ago.Chat.Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessageHandler"/> already
/// derives for an ordinary text message (<see cref="ExternalMessageId.ToClientMessageId"/>'s own
/// remarks) - a redelivered webhook or long-poll update that reaches this step twice produces one
/// message, not two, the same guarantee every other inbound channel message already has.
/// </summary>
public sealed record CompleteChannelAttachmentUpload(
    AttachmentId AttachmentId,
    VisitorId VisitorId,
    ConversationId ConversationId,
    ExternalMessageId ExternalMessageId,
    ChannelKind Kind);
