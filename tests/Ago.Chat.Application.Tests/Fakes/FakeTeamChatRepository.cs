using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-32`'s <see cref="ITeamChatRepository"/> - an in-memory stand-in for the sequence
/// assignment and retry-dedup <see cref="Infrastructure.Postgres.TeamChatRepository"/> does for real
/// against Postgres (that behaviour has its own Testcontainers coverage, `TeamChatRepositoryTests`) -
/// this fake exists only so <c>SendTeamMessageHandlerTests</c> can assert what the handler decided
/// without a database.</summary>
public sealed class FakeTeamChatRepository : ITeamChatRepository
{
    private int _sequence;

    public List<TeamMessage> Posted { get; } = [];

    public Task<TeamMessage> PostAsync(
        SiteId siteId,
        OperatorId authorId,
        bool authorIsAdmin,
        MessageBody body,
        Guid? clientMessageId,
        TeamMessageId id,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (clientMessageId is { } cmid)
        {
            var existing = Posted.FirstOrDefault(m => m.SiteId == siteId && m.ClientMessageId == cmid);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }
        }

        _sequence++;
        var message = new TeamMessage(id, siteId, authorId, authorIsAdmin, body, _sequence, clientMessageId, now);
        Posted.Add(message);
        return Task.FromResult(message);
    }
}
