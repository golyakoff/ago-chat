using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasConversion(IdConverters.Site).ValueGeneratedNever();
        builder.Property(s => s.PublicKey).HasColumnName("public_key").IsRequired();
        // `10-02`: additive, nullable-at-the-database-level via a default rather than a backfill -
        // no existing row (the demo site included, seeded outside this codebase's own migrations by
        // `ago-deploy/seed/create-demo-tenant.sh`) had a name before this column existed.
        builder.Property(s => s.Name).HasColumnName("name").IsRequired().HasDefaultValue(string.Empty);

        // AllowedOrigins is a computed property (IReadOnlyList<string>) over a private List<string>
        // field - Site never exposes a settable collection, so EF is pointed at the field directly.
        builder.Property<List<string>>("_allowedOrigins").HasColumnName("allowed_origins");
        builder.Ignore(s => s.AllowedOrigins);
    }
}
