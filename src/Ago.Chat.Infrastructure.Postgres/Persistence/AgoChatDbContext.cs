using Ago.Chat.Domain;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

public sealed class AgoChatDbContext(DbContextOptions<AgoChatDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Visitor> Visitors => Set<Visitor>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    internal DbSet<RoleRecord> Roles => Set<RoleRecord>();
    internal DbSet<OperatorRoleRecord> OperatorRoles => Set<OperatorRoleRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgoChatDbContext).Assembly);
        // adr/0017: the one line a product's DbContext needs to opt into the shared outbox/inbox
        // schema - Ago.Platform.Persistence.Postgres owns the table shape, not this project.
        modelBuilder.ApplyOutboxInboxConfiguration();
    }
}
