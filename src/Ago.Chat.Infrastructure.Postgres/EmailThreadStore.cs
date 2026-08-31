using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`14-09`: the EF adapter for <see cref="IEmailThreadStore"/> -
/// <see cref="ChannelCredentialRepository"/>'s own detached-means-insert shape.</summary>
public sealed class EmailThreadStore(AgoChatDbContext db) : IEmailThreadStore
{
    public Task<EmailThreadState?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken) =>
        db.EmailThreads.FirstOrDefaultAsync(t => t.ConversationId == conversationId, cancellationToken);

    public async Task SaveAsync(EmailThreadState state, CancellationToken cancellationToken)
    {
        if (db.Entry(state).State == EntityState.Detached)
        {
            db.EmailThreads.Add(state);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
