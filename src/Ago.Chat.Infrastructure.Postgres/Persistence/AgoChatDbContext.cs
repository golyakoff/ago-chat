using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

public sealed class AgoChatDbContext(DbContextOptions<AgoChatDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Visitor> Visitors => Set<Visitor>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    internal DbSet<RoleRecord> Roles => Set<RoleRecord>();
    internal DbSet<OperatorRoleRecord> OperatorRoles => Set<OperatorRoleRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgoChatDbContext).Assembly);
    }
}
