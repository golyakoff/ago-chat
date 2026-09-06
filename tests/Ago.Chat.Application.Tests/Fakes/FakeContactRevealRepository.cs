using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>An in-memory <see cref="IContactRevealRepository"/> that records every call it receives,
/// so a handler test can assert both "a row was written" and "the row names the right operator/
/// contact detail" - and, just as importantly, that a refused or not-found path writes none at all.
/// The same shape <see cref="FakeAccessRecordRepository"/> already establishes for its own sibling
/// port.</summary>
public sealed class FakeContactRevealRepository : IContactRevealRepository
{
    private readonly List<ContactRevealToWrite> _recorded = [];

    public IReadOnlyList<ContactRevealToWrite> Recorded => _recorded;

    public Task RecordAsync(ContactRevealToWrite reveal, CancellationToken cancellationToken)
    {
        _recorded.Add(reveal);
        return Task.CompletedTask;
    }

    public Task<ContactRevealPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        var items = _recorded
            .Where(r => r.SiteId == siteId)
            .OrderByDescending(r => r.Id)
            .Where(r => beforeId is null || r.Id.CompareTo(beforeId.Value) < 0)
            .Take(limit)
            .Select(r => new ContactRevealItem(r.Id, r.OccurredAt, r.ConversationId, r.ContactDetailId, r.OperatorId.Value, r.Surface))
            .ToList();

        return Task.FromResult(new ContactRevealPage(items, null));
    }
}
