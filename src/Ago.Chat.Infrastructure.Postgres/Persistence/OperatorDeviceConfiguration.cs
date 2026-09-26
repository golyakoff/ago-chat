using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `26-03`/`adr/0179` §1: `operator_devices` - see <see cref="OperatorDevice"/>'s own remarks for why
/// this is a Domain aggregate rather than a bare row.
/// </summary>
internal sealed class OperatorDeviceConfiguration : IEntityTypeConfiguration<OperatorDevice>
{
    public void Configure(EntityTypeBuilder<OperatorDevice> builder)
    {
        builder.ToTable("operator_devices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").HasConversion(IdConverters.OperatorDevice).ValueGeneratedNever();
        builder.Property(d => d.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(d => d.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.Operator);
        builder.Property(d => d.InstallationId).HasColumnName("installation_id")
            .HasMaxLength(OperatorDevice.MaxInstallationIdLength);

        // `26-122`: nullable at the DB level - additive/expand-compatible with the image immediately
        // before this migration (`docs/conventions/git-workflow.md`'s own expand/contract rule): that
        // image's own INSERTs know nothing of this column and must keep succeeding. `null` only for a
        // row written before this column existed (`OperatorDevice`'s own remarks); every registration
        // from an updated client always supplies one.
        builder.Property(d => d.DeviceId).HasColumnName("device_id").HasMaxLength(OperatorDevice.MaxDeviceIdLength);

        // Stored as the CLR member name, the same default HasConversion<string>() shape ChannelKind/
        // ConversationState/AttachmentState already use - nothing constrains this column beyond the
        // enum itself, so the plain default is honest (ChannelIdentityConfiguration's own remarks).
        builder.Property(d => d.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.Platform).HasColumnName("platform").HasMaxLength(OperatorDevice.MaxPlatformLength);
        builder.Property(d => d.Token).HasColumnName("token").HasMaxLength(OperatorDevice.MaxTokenLength);
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(d => d.RevokedAt).HasColumnName("revoked_at");
        builder.Property(d => d.LastFailureAt).HasColumnName("last_failure_at");
        // Bounded the identical way ChannelDeliveryConfiguration's own failure_reason column is, and
        // for the identical reason (OperatorDevice.FailureReason's own remarks) - not written by this
        // item, present so `26-04`/`26-05` need no second migration for it.
        builder.Property(d => d.FailureReason).HasColumnName("failure_reason")
            .HasMaxLength(ChannelDelivery.MaxProviderDetailLength);

        // `site_id`/`operator_id` cascade the same way every other per-tenant table does
        // (data-model.md's "every table holding a tenant's data cascades from sites") -
        // ConversationAssignmentIntervalConfiguration's own identical dual-FK precedent and its own
        // remarks: neither a site nor an operator is ever hard-deleted directly (operators are
        // soft-removed via RemovedAt, 13-03; SiteErasureQuery.DeleteSiteAsync's own single
        // `delete from sites` is what actually removes a site row, and its cascade is what this item's
        // own erasure verification (SiteErasureIntegrationTests) proves reaches this table), so this FK
        // exists for referential integrity as much as for erasure.
        builder.HasOne<Site>().WithMany().HasForeignKey(d => d.SiteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operator>().WithMany().HasForeignKey(d => d.OperatorId).OnDelete(DeleteBehavior.Cascade);

        // Pre-`26-122`'s own identity, kept rather than dropped (expand/contract:
        // `docs/conventions/git-workflow.md` - dropping it is a contract-phase change this migration does
        // not make). Harmless to keep live: a genuinely new device's own installation id is always freshly
        // generated (`DataStoreInstallationId`'s own doc comment), so this can only ever collide in the
        // exact concurrent-first-registration race `ux_operator_devices_operator_device` below now also
        // guards (`OperatorDeviceRepository.SaveAsync`'s own remarks on why both constraint names are
        // translated).
        builder.HasIndex(d => new { d.OperatorId, d.InstallationId })
            .IsUnique()
            .HasDatabaseName("ux_operator_devices_operator_installation");

        // `26-122`: the row's real identity now (`OperatorDevice`'s own remarks) - an upsert target for
        // `RegisterOperatorDeviceHandler`, and the storage-level backstop for "one row per physical device
        // per tenancy" the same "index is the backstop, write path is the mechanism" division
        // ChannelIdentityConfiguration's own remarks draw for its own unique index. Not partial: Postgres
        // already treats every `NULL` as distinct from every other, so the several pre-migration rows
        // that have not yet been adopted (`device_id IS NULL`) never collide with each other or with a
        // real value, the identical property the installation index above already relies on.
        builder.HasIndex(d => new { d.OperatorId, d.DeviceId })
            .IsUnique()
            .HasDatabaseName("ux_operator_devices_operator_device");

        // `adr/0179` §1: a token must never be live on two rows - a restored device backup, or a
        // reinstall that inherits a token, can genuinely produce two rows holding the same value.
        // Partial (revoked_at IS NULL only), the identical ux_channel_identities_site_kind_address_active
        // shape and for the identical reason: a revoked row must never block a fresh registration of the
        // same token by a different (or the same) install later.
        builder.HasIndex(d => new { d.Provider, d.Token })
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ux_operator_devices_provider_token_active");

        // The fan-out's own future read (`IOperatorDeviceRepository.ListActiveForOperatorAsync`,
        // `26-05`) - named and indexed now, unused until that item exists (this item's own Done-when).
        // Partial for the identical reason the unique index above is: a revoked device must be
        // invisible to this query, not merely filtered out by every caller that remembers to.
        builder.HasIndex(d => d.OperatorId)
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ix_operator_devices_operator_active");
    }
}
