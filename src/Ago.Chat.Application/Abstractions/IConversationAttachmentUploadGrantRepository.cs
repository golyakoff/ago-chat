using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-78`: the write side of a conversation's attachment-upload grant - raw SQL, never through the
/// <see cref="Conversation"/> aggregate, the identical shape <see cref="IConversationBlockRepository"/>
/// already established for its own current-state pair (that interface's own remarks give the reasoning
/// in full: <see cref="IConversationRepository.GetByIdAsync"/> loads the whole aggregate, messages
/// included, so routing a grant/revoke through it would both load a conversation's full history to flip
/// two columns and race this row's `xmin` against every ordinary message send the visitor this grant
/// concerns is probably in the middle of).
///
/// <para><b>One statement, not two</b>, the identical <c>24-10</c> shape
/// <see cref="IConversationBlockRepository"/>'s own remarks describe - a single SQL statement built
/// from a data-modifying CTE that conditionally updates <c>conversations.attachment_upload_granted_at</c>/
/// <c>attachment_upload_granted_by</c> (the row lock that condition takes is what makes two concurrent
/// requests race-free under Postgres's read-committed rules), followed by a bare existence check that
/// answers whether the conversation exists at all, independent of whether the update fired.</para>
///
/// <para><b>No append-only audit trail, unlike <c>conversation_block_records</c>.</b> `24-10` built one
/// because that item is answering a statutory "was every block/unblock act recorded" question
/// (`ConversationBlockRecordKind`'s own remarks); nothing in `23-78`'s own Done-when asks for a
/// persisted history of every grant/revoke act, only that the *current* state be attributed - who
/// granted it, when - which <see cref="Conversation.AttachmentUploadGrantedAt"/>/
/// <see cref="Conversation.AttachmentUploadGrantedBy"/> already answer for free once loaded. Building
/// the extra table anyway would have meant a second migration surface for this item's one allowed
/// migration to carry no named requirement.</para>
/// </summary>
public interface IConversationAttachmentUploadGrantRepository
{
    /// <summary>Permits a visitor-side attachment upload for one conversation, scoped to
    /// <paramref name="siteId"/> the same way <see cref="IConversationBlockRepository.BlockAsync"/> is -
    /// a conversation belonging to a different site answers <see cref="AttachmentUploadGrantOutcome.NotFound"/>,
    /// never a distinct "wrong tenant" outcome (the same not-found-not-forbidden choice every other
    /// per-conversation check in this codebase makes for a resource outside the caller's tenant).</summary>
    Task<AttachmentUploadGrantOutcome> GrantAsync(
        ConversationId conversationId, SiteId siteId, OperatorId grantedBy, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>The reverse act - clears both columns back to <see langword="null"/>, the identical
    /// "unblocking restores exactly the prior state" shape <see cref="IConversationBlockRepository.UnblockAsync"/>
    /// already gives for its own pair. <see cref="AttachmentUploadGrantOutcome.AlreadyInState"/> here
    /// means the conversation carried no grant to begin with - there is no third state to distinguish,
    /// the identical reasoning <see cref="IConversationBlockRepository.UnblockAsync"/>'s own remarks
    /// give.</summary>
    Task<AttachmentUploadGrantOutcome> RevokeAsync(
        ConversationId conversationId, SiteId siteId, OperatorId revokedBy, DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>The three-way answer <see cref="IConversationAttachmentUploadGrantRepository"/>'s own
/// methods need, the identical shape <see cref="ConversationBlockOutcome"/> already establishes for its
/// sibling interface - a caller needs to tell "no such conversation" apart from "nothing changed
/// because it was already in the state asked for" apart from "the write actually happened", and the
/// repository is the one place that can answer without a second round trip.</summary>
public enum AttachmentUploadGrantOutcome
{
    /// <summary>No conversation with this id exists for this site.</summary>
    NotFound,

    /// <summary>The conversation exists but was already in the state this call asked to move it out of
    /// (already granted, for <see cref="IConversationAttachmentUploadGrantRepository.GrantAsync"/>;
    /// already not granted, for <see cref="IConversationAttachmentUploadGrantRepository.RevokeAsync"/>).</summary>
    AlreadyInState,

    /// <summary>The requested transition happened.</summary>
    Applied,
}
