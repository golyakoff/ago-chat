using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `25-43`. <see cref="PricedResource"/>'s own table, `priced_resources` - the aggregate root that owns
/// <see cref="PublishedPriceVersionConfiguration"/>'s own child rows, the identical
/// <c>documents</c>/<c>published_document_versions</c> shape <see cref="DocumentConfiguration"/>
/// already establishes (<see cref="PricedResource"/>'s own remarks on why this mirrors that shape
/// deliberately rather than inventing a parallel one).
/// </summary>
internal sealed class PricedResourceConfiguration : IEntityTypeConfiguration<PricedResource>
{
    public void Configure(EntityTypeBuilder<PricedResource> builder)
    {
        builder.ToTable("priced_resources");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").HasConversion(IdConverters.PricedResource).ValueGeneratedNever();
        builder.Property(r => r.Key).HasColumnName("price_key").HasConversion(IdConverters.PriceKey)
            .HasMaxLength(PriceKey.MaxLength).IsRequired();
        builder.Property(r => r.LastSequence).HasColumnName("last_sequence");

        // One row per key - the same "the key IS the identity, a duplicate is a real conflict"
        // reasoning DocumentConfiguration's own ix_documents_key gives, restated here.
        builder.HasIndex(r => r.Key).IsUnique().HasDatabaseName("ix_priced_resources_key");

        // Postgres's own system column - the identical optimistic-concurrency mechanism
        // DocumentConfiguration's own remarks describe, reused so two concurrent publishes for the same
        // key cannot both compute the same next LastSequence.
        builder.Property<uint>("xmin").IsRowVersion();

        // Versions/Current are computed properties reading the same _versions field - without this
        // Ignore, EF's own convention would claim _versions as their backing field too, the identical
        // collision DocumentConfiguration's own remarks describe.
        builder.Ignore(r => r.Versions);
        builder.Ignore(r => r.Current);

        // Never a settable collection (clean-architecture.md: no public setters) - EF is pointed at the
        // private backing field directly, so the aggregate loads without going through Publish.
        builder.HasMany<PublishedPriceVersion>("_versions")
            .WithOne()
            .HasForeignKey(v => v.PricedResourceId)
            .OnDelete(DeleteBehavior.Restrict); // `25-43`: a version outlives its resource row exactly as
                                                // long as the resource row itself does - nothing in this codebase ever deletes a
                                                // PricedResource, the identical structural guarantee DocumentConfiguration's own remarks
                                                // describe. Restrict rather than Cascade is what would make a future DELETE on
                                                // priced_resources (one this codebase has no code path to issue) fail loudly instead of
                                                // silently taking every published price with it.
        builder.Navigation("_versions").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
