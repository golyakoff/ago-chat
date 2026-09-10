using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `25-43`. <see cref="PublishedPriceVersion"/>'s own table, `published_price_versions` - a child of
/// <see cref="PricedResource"/> (<see cref="PricedResourceConfiguration"/>'s own remarks), never
/// written to except by <see cref="PricedResource.Publish"/> and never deleted (`Domain.PublishedPriceVersion`'s
/// own "insert-only" remarks - the schema half of the same guarantee).
/// </summary>
internal sealed class PublishedPriceVersionConfiguration : IEntityTypeConfiguration<PublishedPriceVersion>
{
    public void Configure(EntityTypeBuilder<PublishedPriceVersion> builder)
    {
        builder.ToTable("published_price_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasConversion(IdConverters.PublishedPriceVersion).ValueGeneratedNever();
        builder.Property(v => v.PricedResourceId).HasColumnName("priced_resource_id").HasConversion(IdConverters.PricedResource).IsRequired();

        // `Domain.PublishedPriceVersion`'s own remarks: denormalised deliberately, so every real charge
        // site's own hot read (IPriceCatalogRepository.FindCurrentAsync/FindVersionAsync) never has to
        // join PricedResource just to filter by the key it actually has.
        builder.Property(v => v.Key).HasColumnName("price_key").HasConversion(IdConverters.PriceKey)
            .HasMaxLength(PriceKey.MaxLength).IsRequired();

        builder.Property(v => v.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(v => v.Version).HasColumnName("version").HasMaxLength(16).IsRequired();
        builder.Property(v => v.AmountRub).HasColumnName("amount_rub").HasPrecision(10, 2).IsRequired();
        builder.Property(v => v.PublishedAt).HasColumnName("published_at").IsRequired();

        // `FindCurrentAsync`'s own predicate: filter by price_key, order by sequence descending, take
        // one row. One composite index the ordering-by-sequence column ends serves that read and
        // doubles as this table's own uniqueness guard: two versions can never share a
        // (price_key, sequence) pair, the identical invariant ix_published_document_versions_key_sequence
        // enforces for Document, restated here for PricedResource.
        builder.HasIndex(v => new { v.Key, v.Sequence }).IsUnique().HasDatabaseName("ix_published_price_versions_key_sequence");
    }
}
