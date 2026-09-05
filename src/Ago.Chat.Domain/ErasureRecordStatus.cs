namespace Ago.Chat.Domain;

/// <summary>
/// `24-13`: the lifecycle of one erasure receipt - domain vocabulary for the same reason
/// <see cref="ErasureScope"/> is.
///
/// <para><b>Unlike <see cref="ExportStatus"/>, <see cref="Failed"/> is not terminal.</b>
/// <see cref="ExportStatus"/>'s own remarks draw this exact contrast already: an export is a one-shot
/// request a tenant can simply ask for again, so a failure can be terminal there. Erasure has always
/// retried forever (<c>ConversationErasureJob</c>/<c>SiteErasureJob</c>'s own
/// <c>PeriodicTimer</c>/catch-log-retry shape, unchanged by this item) - a conversation or site left
/// flagged after a failed cycle is picked up again next tick, not abandoned. A receipt that recorded
/// <see cref="Failed"/> as a dead end would misdescribe that retry: the same record moves from
/// <see cref="Failed"/> back to <see cref="Completed"/> the moment a later cycle actually finishes the
/// work, so <see cref="Failed"/> here means "the last attempt did not finish", not "this will never
/// finish". <see cref="Completed"/> is the only state nothing ever leaves - once every row and object
/// this process can reach is gone, there is nothing left for a later cycle to retry.</para>
/// </summary>
public enum ErasureRecordStatus
{
    Pending,
    Failed,
    Completed,
}
