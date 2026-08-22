namespace Ago.Chat.Domain;

/// <summary>
/// `pending` until a HEAD-verified upload proves the object matches what was declared at presign
/// time; `ready` once a message may reference it; `deleted` is written only by `5-04`'s orphan sweep
/// (nothing in `5-03` transitions to it yet - the value exists now because the column does).
/// </summary>
public enum AttachmentState
{
    Pending,
    Ready,
    Deleted,
}
