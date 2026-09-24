namespace Ago.Chat.Contracts;

/// <summary>
/// `5-07`: the wire shape for one row of the console's queue view - deliberately thin (no message
/// body, no full history) since the queue view lists conversations, it does not read them; opening
/// one goes through the existing `JoinConversationAsync`/`GetHistoryAsync` hub methods, which already
/// answer "what did this conversation actually say."
/// </summary>
/// <summary>
/// `5-08`: <paramref name="OperatorId"/> is additive - <see langword="null"/> for any caller that
/// never populates it (`api-design.md`'s additive-only wire-contract rule, same as `MessageDto`'s own
/// `5-07` additions). The queue view's two lists never needed it (`Waiting` has none by definition,
/// `AssignedToMe` is always the caller's own id); the admin's site-wide list is the first caller that
/// does, since "who (if anyone) is handling this conversation" is the whole point of that view.
///
/// <para>`23-02`: <paramref name="OperatorName"/> is that operator's own display name, additive the
/// same way - <see langword="null"/> for the queue view (which never joins it in) and for a row that
/// predates the column. The console falls back to the id, never the other way round.</para>
/// </summary>
/// <summary>
/// `23-78`: <see cref="HasAttachmentUploadGrant"/>/<see cref="AttachmentUploadGrantedAt"/>/
/// <see cref="AttachmentUploadGrantedByOperatorId"/> join additively, the identical rule
/// <see cref="OperatorId"/>/<see cref="OperatorName"/> above already establish - <see langword="null"/>
/// (or <see langword="false"/>) for a row that predates these fields. <c>GetOperatorQueueHandler</c>
/// loads full <see cref="Domain.Conversation"/> aggregates for this DTO's own two lists (unlike
/// <c>GetAllConversationsForSiteHandler</c>'s <c>ConversationSummaryItem</c>, a genuinely paginated
/// read store projection), so the grant fields are free from the same row already in hand - the
/// identical "no second query" reasoning <see cref="Domain.Conversation.IsBlocked"/>'s own consumer in
/// that same handler already relies on. This is also what feeds `ago-console`'s <c>ConversationPage</c>
/// (via <c>useWorkspace().conversation</c>) the "who/when" attribution its own attachment-upload-grant
/// toggle shows.
/// </summary>
/// <summary>
/// `25-56`: <see cref="EmojiCreature"/>/<see cref="EmojiFood"/> are additive the same way every field
/// above them already is - <see langword="null"/> for a row that predates the visitor's own pair being
/// assigned, which after `Stage25AddVisitorEmojiPair`'s backfill migration is no caller at all. The
/// console renders them beside the short code this DTO's own <c>VisitorId</c> is sliced from
/// (`ConversationList`/`ConversationPage` - the two locations `25-56`'s backlog item names and no
/// others), never in place of it.
/// </summary>
/// <summary>
/// `25-56`'s own second half: <see cref="VisitorName"/> - the visitor's own name, from a
/// <c>VisitorContactDetailKind.Name</c> row (`25-62`'s rename of what was <c>Other</c>), the widget's
/// own contact-capture form being its one real writer. Additive/nullable the identical way
/// <see cref="EmojiCreature"/>/<see cref="EmojiFood"/> already are on this DTO - absent whenever the
/// visitor has not given one (most visitors, most of the time) or the row predates this field, never a
/// placeholder string. Renders between the emoji pair and the short code
/// (`{emoji}{emoji} {name} {shortCode}`) in the same two locations and no others.
/// </summary>
/// <summary>
/// `26-29`: <see cref="LastMessagePreview"/>/<see cref="LastMessageAt"/> - the pair this DTO's own
/// header used to justify staying without: "deliberately thin ... since the queue view lists
/// conversations, it does not read them." That reasoning held only until both the console and Android
/// needed a client-renderable snippet under the visitor's name, the way every real chat list shows one -
/// at which point "thin" had started to mean "wrong", not "simple". Additive/nullable the identical
/// rule every field above already follows.
///
/// <para><b>Both null together means exactly one thing: this conversation has no messages at all</b>
/// (a <see cref="Domain.ConversationState.Pending"/> conversation - `25-221` - or any other edge case
/// with an empty <c>messages</c> row set). <c>GetOperatorQueueHandler</c> populates both from one
/// batched <see cref="Application.Abstractions.IConversationReadStore.GetLatestMessagesAsync"/> call,
/// never from the <c>Conversation.Messages</c> EF navigation (an unbounded read on a screen the
/// console polls) and never one query per row.</para>
///
/// <para><b><see cref="LastMessagePreview"/> can be <see langword="null"/> even when a last message
/// exists.</b> A system message counts as the last message when it genuinely is one - hiding it would
/// make this field and <see cref="LastMessageAt"/> disagree about whether anything was said since
/// <see cref="LastMessageAt"/>. What *does* suppress the preview is the message's own shape, not its
/// author: `26-76`: only a message that references an attachment has no <see cref="LastMessagePreview"/>
/// - an attachment caption really is client-supplied, opaque, unverifiable text, and inventing a
/// server-composed label ("[attachment]") would be exactly the kind of localized string this contract
/// must not own, since the same wire shape also feeds an English console locale. A module step (a
/// non-null <see cref="LastMessageContentKind"/>) is the opposite case, not the same one - its
/// <see cref="LastMessagePreview"/> is the server's own already-rendered plain text, not a guess dressed
/// up as one, so it is shown rather than withheld; see <see cref="LastMessageContentKind"/>'s own
/// remarks. <see cref="LastMessageAt"/> is still populated in the attachment case - the timestamp is
/// honest regardless of whether the words themselves are safe to preview.</para>
///
/// <para><b><see cref="LastMessageContentKind"/></b> - `26-76`: the raw <c>MessageContentKind</c> value
/// of the latest message, exposed exactly as this DTO's own remarks above already anticipated ("a
/// localized client-side label is a client concern; the DTO should be honest about the content kind
/// rather than sending a server-composed Russian string"). <see langword="null"/> for plain prose and
/// for an attachment-only message, non-null for a module step - a client renders its own icon/prefix
/// from this rather than trying to infer "structured" from <see cref="LastMessagePreview"/> alone, which
/// can no longer distinguish "structured, suppressed" from "structured, shown" now that a module step's
/// preview is populated rather than withheld. Populated in <c>GetOperatorQueueHandler.ToSummary</c> from
/// <c>latestMessage?.ContentKind</c> - no read-model change, that field was already selected.</para>
///
/// <para><b>Truncated to 80 characters, collapsed to one line.</b> A list row has room for a
/// few dozen characters, never a full 8000-character <c>MessageBody</c>, so
/// <c>GetOperatorQueueHandler</c> truncates (and flattens embedded newlines - the whole point is a
/// row that reads as one line) before this DTO is built; nothing this thin needs the client to
/// re-truncate for wire-size reasons, only for whatever width its own layout actually has.</para>
/// </summary>
/// <summary>
/// `26-90`: <see cref="MessageCount"/> - how many messages this conversation holds in total, from
/// first to last, including system-authored ones. Additive/defaulted the identical way every field
/// above already is: <c>0</c> for any caller that does not populate it, which today is every caller
/// but <c>GetAllConversationsForSiteHandler</c> (the admin site-wide list, the one screen that shows
/// "how long is this conversation" as a fact about a conversation the reader has never opened).
///
/// <para><b>It is a total, never an unread count.</b> <see cref="OperatorUnreadCount"/> keeps its
/// existing meaning untouched and is not reused for this - the two answer different questions ("is
/// there something here for me to read" versus "how big is this thing"), and the admin list shows the
/// second one with no badge at all, because a supervisor scanning a site's whole history has no
/// personal read position in a conversation that was never theirs.</para>
///
/// <para><b>No sibling "how many conversations are there" field, here or anywhere.</b> `26-90`'s own
/// Out of scope, decided against a live site holding tens of thousands of closed conversations: a
/// per-state tally is a <c>COUNT(*)</c> over the whole history on every open of the screen, for a
/// number nobody acts on. This per-row count is the opposite case - it is bounded by one
/// conversation's own length and served by the index the row's own last-message lookup already
/// uses.</para>
/// </summary>
public sealed record ConversationSummaryDto(
    Guid ConversationId, Guid VisitorId, string State, DateTimeOffset CreatedAt, int OperatorUnreadCount,
    Guid? OperatorId = null, string? OperatorName = null, bool HasAttachmentUploadGrant = false,
    DateTimeOffset? AttachmentUploadGrantedAt = null, Guid? AttachmentUploadGrantedByOperatorId = null,
    string? EmojiCreature = null, string? EmojiFood = null, string? VisitorName = null,
    string? LastMessagePreview = null, DateTimeOffset? LastMessageAt = null,
    string? LastMessageContentKind = null, int MessageCount = 0);

/// <summary>
/// `GET /api/v1/conversations/queue`'s response body. Two lists rather than one filterable list: the
/// two halves answer genuinely different questions for an operator (`docs/vision.md`'s "no manual
/// claim" model - `Waiting` is read-only situational awareness, `AssignedToMe` is the operator's own
/// actionable work), and a console client always wants both together to render the queue view in one
/// round trip.
/// </summary>
public sealed record OperatorQueueResponse(
    IReadOnlyList<ConversationSummaryDto> Waiting, IReadOnlyList<ConversationSummaryDto> AssignedToMe);
