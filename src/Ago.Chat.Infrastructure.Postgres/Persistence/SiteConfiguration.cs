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

        // AllowedOrigins is a computed property (IReadOnlyList<string>) over a private List<string>
        // field - Site never exposes a settable collection, so EF is pointed at the field directly.
        builder.Property<List<string>>("_allowedOrigins").HasColumnName("allowed_origins");
        builder.Ignore(s => s.AllowedOrigins);
    }
}
