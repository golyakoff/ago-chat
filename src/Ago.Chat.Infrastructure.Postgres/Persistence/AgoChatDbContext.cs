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
    public DbSet<ChannelIdentity> ChannelIdentities => Set<ChannelIdentity>();
    public DbSet<ChannelCredential> ChannelCredentials => Set<ChannelCredential>();
    public DbSet<OperatorInvite> OperatorInvites => Set<OperatorInvite>();
    public DbSet<BillingSubscription> BillingSubscriptions => Set<BillingSubscription>();
    public DbSet<BillingWebhookEvent> BillingWebhookEvents => Set<BillingWebhookEvent>();
    internal DbSet<RoleRecord> Roles => Set<RoleRecord>();
    internal DbSet<OperatorRoleRecord> OperatorRoles => Set<OperatorRoleRecord>();
    // `16-03`: migration-scaffolding only - ExportRequestEntity's own remarks explain why nothing
    // ever queries this DbSet.
    internal DbSet<ExportRequestEntity> ExportRequests => Set<ExportRequestEntity>();
    // `13-06`: migration-scaffolding only, the same shape - MessageArchiveEntity's own remarks.
    internal DbSet<MessageArchiveEntity> MessageArchives => Set<MessageArchiveEntity>();
    // `18-04`: ConversationNote/Tag's own EF-backed tables - see each type's own remarks on why they
    // are real tables and not owned collections on Conversation/Site.
    public DbSet<ConversationNote> ConversationNotes => Set<ConversationNote>();
    public DbSet<Tag> Tags => Set<Tag>();
    internal DbSet<ConversationTagRecord> ConversationTags => Set<ConversationTagRecord>();
    // `20-07`: EnabledModule has a real direct writer (EnabledModuleRepository); ModuleTask does not -
    // it is reached only through Conversation's own "_moduleTasks" navigation
    // (ConversationConfiguration), the identical "migration-scaffolding only" shape MessageArchives
    // and ExportRequests above use, for the identical reason - nothing ever queries this DbSet on its
    // own, only EF's own migration tooling needs it registered to generate the table.
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();
    internal DbSet<ModuleTask> ModuleTasks => Set<ModuleTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgoChatDbContext).Assembly);
        // adr/0017: the one line a product's DbContext needs to opt into the shared outbox/inbox
        // schema - Ago.Platform.Persistence.Postgres owns the table shape, not this project.
        modelBuilder.ApplyOutboxInboxConfiguration();
    }
}
