namespace Ago.Chat.Domain;

/// <summary>
/// `16-03`: the lifecycle of one tenant-export request - domain vocabulary, the same placement
/// reasoning as <see cref="AttachmentState"/>/<see cref="ConversationState"/> (a business-meaningful
/// state both `Ago.Chat.Application`'s handlers and `Ago.Chat.Worker`'s job need to agree on, so it
/// cannot live in either alone).
///
/// <para><c>Pending</c> until <c>Ago.Chat.Worker</c>'s <c>SiteExportJob</c> claims and processes the
/// request; <c>Ready</c> once the archive is uploaded and an object key is recorded; <c>Failed</c> is
/// terminal, unlike erasure's own retry-forever shape - an export is a one-shot request the tenant
/// can simply ask for again, so there is no value in silently retrying a request that already failed
/// once, and a terminal state lets the console show the tenant an honest "this attempt failed" rather
/// than a spinner that never resolves.</para>
///
/// <para><c>Expired</c> (added by the TTL half of this same item): a second terminal state reached
/// only from <c>Ready</c>, once <c>Ago.Chat.Worker</c>'s <c>SiteExportPruneJob</c> has deleted the
/// archive object past its own retention window. Not folded into <c>Failed</c> - a failed export never
/// produced an artifact at all (nothing to explain beyond <c>FailureReason</c>), while an expired one
/// did, and the console owes the tenant a different sentence for "this succeeded, then was cleaned up"
/// than for "this attempt did not work." A row never moves out of <c>Expired</c> either: the same
/// one-shot reasoning above applies - a tenant who wants the data again simply asks for a new
/// export.</para>
///
/// <para><c>Processing</c> (`25-72`): a third, deliberately transient state between <c>Pending</c> and
/// either <c>Ready</c> or <c>Failed</c> - a row a <c>SiteExportJob</c> replica has atomically claimed
/// (<c>Ago.Chat.Worker.SiteExportClaimQuery.ClaimPendingBatchAsync</c>) and is currently
/// building/uploading. Exists so two replicas ticking at the same time can never both read the same
/// <c>Pending</c> row and both build the same archive (`25-72`'s own Found note) - the same "flip the
/// status atomically before doing the slow work" shape <c>Ago.Chat.Worker.AttachmentOrphanSweepQuery</c>'s
/// own claim already establishes for a different table. Unlike <c>Ready</c>/<c>Failed</c>/<c>Expired</c>,
/// a <c>Processing</c> row is not meant to be a durable resting state - it resolves to <c>Ready</c> or
/// <c>Failed</c> within one build-and-upload cycle, or, if the replica holding it crashed mid-flight, is
/// swept back to <c>Pending</c> once it has sat <c>Processing</c> longer than
/// <c>SiteExportJobOptions.StaleProcessingTimeout</c> - the recovery path this item's own Scope asks
/// for, so a crashed worker never leaves a request stuck forever in a state nothing else ever revisits.
/// </para>
/// </summary>
public enum ExportStatus
{
    Pending,
    Ready,
    Failed,
    Expired,
    Processing,
}
