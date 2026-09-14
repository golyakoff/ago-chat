using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`25-04`: "this site has the AI add-on on, from this instant" - one row per site, keyed by
/// <see cref="SiteId"/> alone, the same natural-key shape
/// <see cref="ModuleQuantityGrantConfiguration"/> already uses and for the same reason.
///
/// <para><b>A real foreign key to <c>sites</c>, unlike <see cref="AcceptanceRecordConfiguration"/>.</b>
/// That table deliberately has none because `adr/0111` keeps an acceptance as evidence *through* an
/// erasure; this row is not evidence, it is a live setting, and a site that no longer exists has no
/// setting to hold - so it cascades with the site exactly the way a widget configuration does. The
/// evidence half of `25-04` lives in <c>acceptance_records</c> and
/// <c>ai_processing_basis_declarations</c>, and only the second of those needed a decision about
/// erasure.</para></summary>
internal sealed class AiAddOnEnablementConfiguration : IEntityTypeConfiguration<AiAddOnEnablement>
{
    public void Configure(EntityTypeBuilder<AiAddOnEnablement> builder)
    {
        builder.ToTable("ai_add_on_enablements");
        builder.HasKey(e => e.SiteId);

        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).ValueGeneratedNever();
        builder.Property(e => e.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(e => e.EnabledAt).HasColumnName("enabled_at").HasColumnType("timestamptz");
        builder.Property(e => e.EnabledBy).HasColumnName("enabled_by").HasConversion(IdConverters.NullableOperator);
        builder.Property(e => e.AcceptedDocumentKey)
            .HasColumnName("accepted_document_key").HasMaxLength(AiAddOnEnablement.MaxDocumentKeyLength);
        builder.Property(e => e.AcceptedDocumentVersion)
            .HasColumnName("accepted_document_version").HasMaxLength(AiAddOnEnablement.MaxDocumentVersionLength);
        builder.Property(e => e.DisabledAt).HasColumnName("disabled_at").HasColumnType("timestamptz");
        builder.Property(e => e.DisabledBy).HasColumnName("disabled_by").HasConversion(IdConverters.NullableOperator);

        // Computed from IsEnabled/EnabledAt, never stored - the same reasoning
        // ModuleQuantityGrant.EffectiveQuantity's own Ignore() carries: a stored copy is a second place
        // the answer could drift from its inputs, and this particular answer gates what leaves the
        // deployment.
        builder.Ignore(e => e.EffectiveFrom);

        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId).OnDelete(DeleteBehavior.Cascade);
    }
}
