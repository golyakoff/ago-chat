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
    // `14-12`
    public DbSet<PendingChannelLinkRequest> PendingChannelLinkRequests => Set<PendingChannelLinkRequest>();
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
    // `24-13`: migration-scaffolding only, the same shape - ErasureRecordEntity's own remarks.
    internal DbSet<ErasureRecordEntity> ErasureRecords => Set<ErasureRecordEntity>();
    // `24-12`: migration-scaffolding only, the same shape - AccessRecordEntity's own remarks.
    internal DbSet<AccessRecordEntity> AccessRecords => Set<AccessRecordEntity>();
    // `18-04`: ConversationNote/Tag's own EF-backed tables - see each type's own remarks on why they
    // are real tables and not owned collections on Conversation/Site.
    public DbSet<ConversationNote> ConversationNotes => Set<ConversationNote>();
    public DbSet<Tag> Tags => Set<Tag>();
    internal DbSet<ConversationTagRecord> ConversationTags => Set<ConversationTagRecord>();
    // `23-03`: internal - reached through IConversationAssignmentLog (ConversationAssignmentLog's own
    // Set<T>() calls) by every real caller; exposed here only so integration tests can seed/verify rows
    // directly, the same visibility ConversationTags right above already has for the identical reason.
    internal DbSet<ConversationAssignmentInterval> ConversationAssignments => Set<ConversationAssignmentInterval>();
    // `20-07`: EnabledModule has a real direct writer (EnabledModuleRepository); ModuleTask does not -
    // it is reached only through Conversation's own "_moduleTasks" navigation
    // (ConversationConfiguration), the identical "migration-scaffolding only" shape MessageArchives
    // and ExportRequests above use, for the identical reason - nothing ever queries this DbSet on its
    // own, only EF's own migration tooling needs it registered to generate the table.
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();
    internal DbSet<ModuleTask> ModuleTasks => Set<ModuleTask>();
    // `14-14`: VisitorContactDetail's own table - see its own remarks for why it is not folded into
    // ChannelIdentities.
    public DbSet<VisitorContactDetail> VisitorContactDetails => Set<VisitorContactDetail>();
    // `14-09`: EmailThreadState's own table - see its own remarks for why it is a 1:1 extension of
    // Conversation rather than a column on it.
    public DbSet<EmailThreadState> EmailThreads => Set<EmailThreadState>();
    // `14-15`: PendingPhoneVerification's own table - the sibling of PendingChannelLinkRequests above,
    // for a channel that cannot supply that item's own inbound evidence.
    public DbSet<PendingPhoneVerification> PendingPhoneVerifications => Set<PendingPhoneVerification>();
    // `20-11`: the per-booking priority list's own table - a real DbSet (unlike ModuleTasks above) since
    // ModuleTaskChannelPreferenceRepository queries it directly, keyed on ModuleTaskId by value rather than
    // reached through Conversation's own encapsulated navigation.
    public DbSet<ModuleTaskChannelPreference> ModuleTaskChannelPreferences => Set<ModuleTaskChannelPreference>();
    // `24-01`: AcceptanceRecord's own table - see AcceptanceRecordConfiguration's own remarks for why
    // it carries no foreign key to any subject's own table.
    public DbSet<AcceptanceRecord> AcceptanceRecords => Set<AcceptanceRecord>();
    // `24-02`: Document is the aggregate root (DocumentRepository's own write path);
    // PublishedDocumentVersions is a real DbSet too, unlike Conversation's own Messages, because
    // IDocumentRepository's public read path (FindVersionAsync/FindCurrentAsync) queries it directly
    // rather than always loading the parent aggregate - IDocumentRepository's own remarks.
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<PublishedDocumentVersion> PublishedDocumentVersions => Set<PublishedDocumentVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgoChatDbContext).Assembly);
        // adr/0017: the one line a product's DbContext needs to opt into the shared outbox/inbox
        // schema - Ago.Platform.Persistence.Postgres owns the table shape, not this project.
        modelBuilder.ApplyOutboxInboxConfiguration();
    }
}
