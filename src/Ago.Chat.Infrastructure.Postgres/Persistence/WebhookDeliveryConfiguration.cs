using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").HasConversion(IdConverters.WebhookDelivery).ValueGeneratedNever();
        builder.Property(d => d.EndpointId).HasColumnName("endpoint_id").HasConversion(IdConverters.WebhookEndpoint);
        // `6-05`: the source event's EventEnvelope.MessageId - see WebhookDelivery's own remarks for
        // why this table's unique index below on (endpoint_id, message_id), not a second table, is
        // the "inbox-style ledger keyed by (message_id, endpoint_id)" the backlog asks for.
        builder.Property(d => d.MessageId).HasColumnName("message_id");
        builder.Property(d => d.EventType).HasColumnName("event_type").IsRequired();
        // jsonb, not text - this item's own scope names it explicitly; `db-migration` skill's own
        // rule ("jsonb... goes on the Stage 9 friction list" for the eventual MySQL port) is satisfied
        // in data-model.md alongside this change.
        builder.Property(d => d.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(d => d.Attempt).HasColumnName("attempt");
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(d => d.ResponseStatus).HasColumnName("response_status");
        // Bounded at the database too, matching the domain-level cap (WebhookDelivery.MaxResponseSnippetLength)
        // - "never store an unbounded response body" is enforced at both the entity and the schema,
        // not trusted to only one of them.
        builder.Property(d => d.ResponseSnippet).HasColumnName("response_snippet")
            .HasMaxLength(WebhookDelivery.MaxResponseSnippetLength);
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.DeliveredAt).HasColumnName("delivered_at");

        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.EndpointId);

        // Serves GetWebhookDeliveriesHandler's own keyset query: WHERE endpoint_id = @x AND (id < @cursor)
        // ORDER BY id DESC - a composite index with id last matches both the equality filter and the
        // sort/keyset-comparison column, the same shape data-model.md already establishes for
        // ix_conversations_waiting.
        builder.HasIndex(d => new { d.EndpointId, d.Id }).HasDatabaseName("ix_webhook_deliveries_endpoint_id_id");

        // `6-05`: the idempotency ledger itself - "a redelivered ConversationClosed must not double-send
        // to an endpoint that already got it" (backlog) is this UNIQUE constraint, enforced by Postgres,
        // not a separate in-memory or application-level check alone. WebhookDeliveryRepository.SaveAsync
        // catches the resulting unique-violation and treats it as "already recorded, no-op" - the same
        // catch-the-violation shape Ago.Platform.Persistence.Postgres.EfInboxChecker already uses for its
        // own (message_id, consumer) ledger; this is that same pattern at the (message_id, endpoint_id)
        // grain the generic inbox table cannot express on its own.
        builder.HasIndex(d => new { d.EndpointId, d.MessageId }).IsUnique().HasDatabaseName("ux_webhook_deliveries_endpoint_id_message_id");
    }
}
