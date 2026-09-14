using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `25-04` decision 5: its own table, `ai_processing_basis_declarations` - <b>never a column on
/// `ai_add_on_enablements` and never a row in `acceptance_records`</b>. The separation is the
/// requirement, not an implementation preference: a reader must be able to tell "the tenant accepted our
/// terms" from "the tenant asserted they have a basis covering their own visitors", and one table
/// holding both would have made those two statements share a timestamp and an author.
///
/// <para><b>No foreign key on <c>site_id</c> - the same erasure decision
/// <see cref="AcceptanceRecordConfiguration"/> made, for the same reason.</b> `adr/0111` keeps an
/// acceptance whole through erasure because it is evidence that processing had a lawful basis at the
/// time; a declaration is evidence of exactly the same kind, about the same processing, and would be
/// worth strictly less if it vanished with the site whose conduct it documents. Declaring it with an FK
/// would wire it into <c>SiteErasureQuery.DeleteSiteAsync</c>'s cascade list by default.</para>
/// </summary>
internal sealed class AiProcessingBasisDeclarationConfiguration : IEntityTypeConfiguration<AiProcessingBasisDeclaration>
{
    public void Configure(EntityTypeBuilder<AiProcessingBasisDeclaration> builder)
    {
        builder.ToTable("ai_processing_basis_declarations");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id")
            .HasConversion(IdConverters.AiProcessingBasisDeclaration).ValueGeneratedNever();

        builder.Property(d => d.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(d => d.DeclaredBy).HasColumnName("declared_by").HasConversion(IdConverters.Operator).IsRequired();
        builder.Property(d => d.DeclaredAt).HasColumnName("declared_at").HasColumnType("timestamptz").IsRequired();
        builder.Property(d => d.ClientIp)
            .HasColumnName("client_ip").HasMaxLength(AiProcessingBasisDeclaration.MaxClientIpLength);
        builder.Property(d => d.UserAgent)
            .HasColumnName("user_agent").HasMaxLength(AiProcessingBasisDeclaration.MaxUserAgentLength);

        // The only read is "this site's most recent declaration" - one composite index serves the filter
        // and the ordering together, the same shape ix_acceptance_records_subject already takes.
        builder.HasIndex(d => new { d.SiteId, d.DeclaredAt }).HasDatabaseName("ix_ai_basis_declarations_site");
    }
}
