using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.BulkDeleteSiteAttachments;
using Ago.Chat.Application.UseCases.GetSiteAttachmentEgress;
using Ago.Chat.Application.UseCases.GetSiteAttachmentStorageSummary;
using Ago.Chat.Application.UseCases.ListSiteAttachments;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Storage;

/// <summary>
/// `23-80`/`23-82`: "Администрирование -> Хранилище" - the tenant's own read of everything their site
/// holds, plus the bulk-delete that clears it. Every route here is `"RequireOperatorIdentity"` at the
/// gate and <see cref="Domain.Permission.SiteConfigure"/> inside each handler, the same two-layer shape
/// <c>SiteConsentDocumentEndpoints</c> already uses (see that file's own remarks) - the route only
/// proves "some operator," the handler proves "an operator who may configure *this* site."
/// </summary>
public static class SiteAttachmentStorageEndpoints
{
    public static void MapSiteAttachmentStorageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/attachments").RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleListAsync);
        group.MapGet("/largest-conversations", HandleLargestConversationsAsync);
        group.MapGet("/storage-summary", HandleStorageSummaryAsync);
        group.MapGet("/egress", HandleEgressAsync);
        group.MapPost("/bulk-delete", HandleBulkDeleteAsync);
    }

    private static async Task<IResult> HandleListAsync(
        Guid siteId,
        string? sort,
        string? filter,
        string? cursorValue,
        Guid? cursorAttachmentId,
        int? limit,
        ListSiteAttachmentsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseSort(sort, out var parsedSort))
        {
            return Results.BadRequest($"Unknown sort '{sort}'.");
        }

        if (!TryParseFilter(filter, out var parsedFilter))
        {
            return Results.BadRequest($"Unknown filter '{filter}'.");
        }

        var cursor = cursorValue is not null && cursorAttachmentId is not null
            ? new AttachmentListCursor(cursorValue, cursorAttachmentId.Value)
            : null;

        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListSiteAttachments(new SiteId(siteId), user.GetOperatorId(), parsedSort, parsedFilter, cursor, limit),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var page = result.Value;
        return Results.Ok(new AttachmentListPageResponse(
            [.. page.Items.Select(ToDto)],
            page.NextCursor is { } next ? new AttachmentListCursorDto(next.Value, next.AttachmentId) : null));
    }

    private static async Task<IResult> HandleLargestConversationsAsync(
        Guid siteId,
        int? limit,
        GetLargestConversationsForSiteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetLargestConversationsForSite(new SiteId(siteId), user.GetOperatorId(), limit), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(i => new LargestConversationResponse(i.ConversationId.Value, i.TotalBytes, i.AttachmentCount))
            .ToList());
    }

    private static async Task<IResult> HandleStorageSummaryAsync(
        Guid siteId,
        GetSiteAttachmentStorageSummaryHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetSiteAttachmentStorageSummary(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(new AttachmentStorageSummaryResponse(result.Value.UsedBytes, result.Value.TotalBytes));
    }

    private static async Task<IResult> HandleEgressAsync(
        Guid siteId,
        string? month,
        GetSiteAttachmentEgressHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        DateOnly? periodMonth = null;
        if (!string.IsNullOrEmpty(month))
        {
            if (!DateOnly.TryParseExact(month, "yyyy-MM", out var parsed))
            {
                return Results.BadRequest($"'month' must be 'yyyy-MM', got '{month}'.");
            }

            periodMonth = new DateOnly(parsed.Year, parsed.Month, 1);
        }

        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetSiteAttachmentEgress(new SiteId(siteId), user.GetOperatorId(), periodMonth), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var egress = result.Value;
        return Results.Ok(new AttachmentEgressResponse(
            egress.PeriodMonth.ToString("yyyy-MM"), egress.DownloadCount, egress.BytesOut));
    }

    private static async Task<IResult> HandleBulkDeleteAsync(
        Guid siteId,
        BulkDeleteAttachmentsRequest request,
        BulkDeleteSiteAttachmentsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var attachmentIds = (request.AttachmentIds ?? []).Select(id => new AttachmentId(id)).ToList();

        var result = await handler.HandleAsync(
            new BulkDeleteSiteAttachments(new SiteId(siteId), user.GetOperatorId(), attachmentIds), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var outcome = result.Value;
        return Results.Ok(new BulkDeleteAttachmentsResponse(
            outcome.DeletedCount, outcome.FreedBytes, outcome.NotFoundIds, outcome.AlreadyGoneCount));
    }

    private static bool TryParseSort(string? sort, out AttachmentListSort parsed)
    {
        switch (sort)
        {
            case null or "":
            case "sizeDesc":
                parsed = AttachmentListSort.SizeDescending;
                return true;
            case "typeAsc":
                parsed = AttachmentListSort.TypeAscending;
                return true;
            case "ageAsc":
                parsed = AttachmentListSort.AgeAscending;
                return true;
            case "conversationAsc":
                parsed = AttachmentListSort.ConversationAscending;
                return true;
            case "senderAsc":
                parsed = AttachmentListSort.SenderAscending;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseFilter(string? filter, out AttachmentListFilterKind parsed)
    {
        switch (filter)
        {
            case null or "":
            case "none":
                parsed = AttachmentListFilterKind.None;
                return true;
            case "neverDownloaded":
                parsed = AttachmentListFilterKind.NeverDownloaded;
                return true;
            case "duplicates":
                parsed = AttachmentListFilterKind.Duplicates;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static AttachmentListItemResponse ToDto(AttachmentListItem item) => new(
        item.Id.Value,
        item.ConversationId.Value,
        item.ContentType,
        item.SizeBytes,
        item.CreatedAt,
        item.DownloadCount,
        item.LastDownloadedAt,
        item.SenderKind?.ToString(),
        item.SenderId,
        item.IsDuplicate);

    public sealed record AttachmentListItemResponse(
        Guid Id,
        Guid ConversationId,
        string ContentType,
        long SizeBytes,
        DateTimeOffset CreatedAt,
        long DownloadCount,
        DateTimeOffset? LastDownloadedAt,
        string? SenderKind,
        Guid? SenderId,
        bool IsDuplicate);

    public sealed record AttachmentListCursorDto(string Value, Guid AttachmentId);

    public sealed record AttachmentListPageResponse(
        IReadOnlyList<AttachmentListItemResponse> Items, AttachmentListCursorDto? NextCursor);

    public sealed record LargestConversationResponse(Guid ConversationId, long TotalBytes, int AttachmentCount);

    public sealed record AttachmentStorageSummaryResponse(long UsedBytes, long TotalBytes);

    public sealed record AttachmentEgressResponse(string PeriodMonth, long DownloadCount, long BytesOut);

    public sealed record BulkDeleteAttachmentsRequest(IReadOnlyList<Guid>? AttachmentIds);

    public sealed record BulkDeleteAttachmentsResponse(
        int DeletedCount, long FreedBytes, IReadOnlyList<Guid> NotFoundIds, int AlreadyGoneCount);
}
