using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.BulkDeleteSiteAttachments;

public sealed record BulkDeleteSiteAttachments(SiteId SiteId, OperatorId RequestedBy, IReadOnlyList<AttachmentId> AttachmentIds);

/// <summary><see cref="NotFoundIds"/> covers both a truly nonexistent id and one belonging to a
/// different tenant - the identical info-hiding shape <c>DeleteAttachmentHandler</c>'s own remarks
/// already establish for the single-attachment delete ("a different site must read identically to one
/// that does not exist"), which is exactly what <c>BulkDeleteAttachmentsAcrossTenantsIsRefusedTests</c>
/// proves by fault injection rather than by reading the code. <see cref="AlreadyGoneCount"/> is
/// everything else skipped - already <see cref="Domain.AttachmentState.Deleted"/>, or still
/// <see cref="Domain.AttachmentState.Pending"/> and never a real held file - folded into one count
/// rather than a second id list, because neither case is a caller mistake worth naming individually the
/// way a wrong-tenant id is.</summary>
public sealed record BulkDeleteSiteAttachmentsResult(
    int DeletedCount, long FreedBytes, IReadOnlyList<Guid> NotFoundIds, int AlreadyGoneCount);
