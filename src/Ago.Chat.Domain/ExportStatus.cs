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
/// </summary>
public enum ExportStatus
{
    Pending,
    Ready,
    Failed,
}
