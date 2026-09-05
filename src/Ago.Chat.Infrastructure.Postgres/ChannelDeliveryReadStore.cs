using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Hand-written SQL over the write model, never through the aggregate (adr/0004) - the same
/// split <c>WebhookDeliveryReadStore</c> already draws for its own table.
///
/// <para><b>Tenant scope.</b> Filtered on both <c>conversation_id</c> and <c>site_id</c> - unlike
/// <c>WebhookDeliveryReadStore</c>, which can only filter on <c>endpoint_id</c> because
/// <c>webhook_deliveries</c> carries no <c>site_id</c> column of its own, this table does, so the read
/// itself enforces the boundary rather than depending solely on a caller-side comparison after the
/// fact. <c>GetChannelDeliveriesForConversationHandler</c>'s own operator-assignment check is still what
/// decides whether this read runs at all; this query's own <c>site_id</c> filter is the second,
/// independent line - the same defence-in-depth <c>CrossTenantRouteIsolationTests</c> proves for
/// every other tenant-scoped read.</para></summary>
public sealed class ChannelDeliveryReadStore(NpgsqlDataSource dataSource) : IChannelDeliveryReadStore
{
    private const string Sql = """
        select id as "Id", message_id as "MessageId", channel_kind as "ChannelKind", status as "Status",
               provider_message_id as "ProviderMessageId", failure_reason as "FailureReason",
               attempted_at as "AttemptedAt"
        from channel_deliveries
        where conversation_id = @ConversationId
          and site_id = @SiteId
        order by attempted_at desc
        """;

    public async Task<IReadOnlyList<ChannelDeliverySummaryItem>> GetForConversationAsync(
        ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<ChannelDeliveryRow>(new CommandDefinition(
            Sql,
            new { ConversationId = conversationId.Value, SiteId = siteId.Value },
            cancellationToken: cancellationToken));

        return rows.Select(ToSummaryItem).ToList();
    }

    private static ChannelDeliverySummaryItem ToSummaryItem(ChannelDeliveryRow r) => new(
        new ChannelDeliveryId(r.Id),
        new MessageId(r.MessageId),
        Enum.Parse<ChannelKind>(r.ChannelKind),
        Enum.Parse<ChannelDeliveryStatus>(r.Status),
        r.ProviderMessageId,
        r.FailureReason,
        new DateTimeOffset(DateTime.SpecifyKind(r.AttemptedAt, DateTimeKind.Utc)));

    private sealed record ChannelDeliveryRow(
        Guid Id,
        Guid MessageId,
        string ChannelKind,
        string Status,
        string? ProviderMessageId,
        string? FailureReason,
        DateTime AttemptedAt);
}
