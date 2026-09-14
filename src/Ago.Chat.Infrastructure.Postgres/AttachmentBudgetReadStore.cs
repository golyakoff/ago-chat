using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`23-80`'s <see cref="IAttachmentBudgetReadStore"/> - a bare read of
/// <c>sites.attachment_bytes_reserved</c>, the exact column <see cref="SiteAttachmentStorageBudgetStore"/>
/// (`23-76`) already maintains as the enforcement figure. See that read port's own remarks for why
/// this is a second, tiny port rather than a new method on <see cref="ISiteAttachmentStorageBudget"/>
/// itself.</summary>
public sealed class AttachmentBudgetReadStore(NpgsqlDataSource dataSource) : IAttachmentBudgetReadStore
{
    private const string Sql = "SELECT attachment_bytes_reserved FROM sites WHERE id = @SiteId";

    public async Task<long> GetReservedBytesAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // `QuerySingleOrDefaultAsync<long?>`, not `<long>`: a site id the caller already resolved
        // through `ISiteRepository` before calling here is expected to exist, but a missing row must
        // still read as zero rather than throw - the same "no evidence and zero are the same fact"
        // reasoning this port's own remarks state, applied defensively rather than assumed.
        var reserved = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        return reserved ?? 0L;
    }
}
