using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Hand-written SQL over the write model, never through the aggregate (adr/0004) - the same
/// split `ConversationReadStore` already draws for message history.</summary>
public sealed class WebhookDeliveryReadStore(NpgsqlDataSource dataSource) : IWebhookDeliveryReadStore
{
    // Keyset on `id` alone - delivery ids are uuid v7 (IIdGenerator), so id order already is creation
    // order, the same reasoning ConversationReadStore's own AllForSiteSql comment gives for
    // conversations. No `active` filter on the endpoint - delivery history for a revoked endpoint
    // stays queryable (GetWebhookDeliveriesHandler's own remarks); this query never joins
    // webhook_endpoints at all.
    private const string Sql = """
        select id as "Id", event_type as "EventType", attempt as "Attempt", status as "Status",
               response_status as "ResponseStatus", response_snippet as "ResponseSnippet",
               created_at as "CreatedAt", delivered_at as "DeliveredAt"
        from webhook_deliveries
        where endpoint_id = @EndpointId
          and (@BeforeId is null or id < @BeforeId)
        order by id desc
        limit @PageSize
        """;

    public async Task<WebhookDeliveryPage> GetForEndpointAsync(
        WebhookEndpointId endpointId, Guid? beforeId, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<WebhookDeliveryRow>(new CommandDefinition(
            Sql,
            new { EndpointId = endpointId.Value, BeforeId = beforeId, PageSize = pageSize },
            cancellationToken: cancellationToken));

        var items = rows.Select(ToSummaryItem).ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Id.Value : (Guid?)null;
        return new WebhookDeliveryPage(items, nextCursor);
    }

    private static WebhookDeliverySummaryItem ToSummaryItem(WebhookDeliveryRow r) => new(
        new WebhookDeliveryId(r.Id),
        r.EventType,
        r.Attempt,
        Enum.Parse<WebhookDeliveryStatus>(r.Status),
        r.ResponseStatus,
        r.ResponseSnippet,
        new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        r.DeliveredAt is { } deliveredAt ? new DateTimeOffset(DateTime.SpecifyKind(deliveredAt, DateTimeKind.Utc)) : null);

    private sealed record WebhookDeliveryRow(
        Guid Id,
        string EventType,
        int Attempt,
        string Status,
        int? ResponseStatus,
        string? ResponseSnippet,
        DateTime CreatedAt,
        DateTime? DeliveredAt);
}
