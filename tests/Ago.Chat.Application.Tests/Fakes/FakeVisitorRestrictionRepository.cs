using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real <c>VisitorRestrictionRepository</c>'s own contract in memory - append-only
/// rows keyed loosely by <c>(SiteId, VisitorId)</c>, "active" meaning not lifted and either no expiry
/// or an expiry still in the future relative to whatever <c>now</c> a caller passes (the real
/// repository's own remarks: no background job, checked fresh on every read).</summary>
public sealed class FakeVisitorRestrictionRepository : IVisitorRestrictionRepository
{
    private sealed class Row
    {
        public required Guid Id;
        public required SiteId SiteId;
        public required VisitorId VisitorId;
        public required VisitorRestrictionKind Kind;
        public required DateTimeOffset RestrictedAt;
        public required OperatorId RestrictedBy;
        public required DateTimeOffset? ExpiresAt;
        public required ConversationId SourceConversationId;
        public DateTimeOffset? LiftedAt;
        public OperatorId? LiftedBy;
    }

    private readonly List<Row> _rows = [];

    public IReadOnlyList<(SiteId SiteId, VisitorId VisitorId, VisitorRestrictionKind Kind, DateTimeOffset? ExpiresAt)> Restrictions =>
        _rows.Select(r => (r.SiteId, r.VisitorId, r.Kind, r.ExpiresAt)).ToList();

    public Task RestrictAsync(
        SiteId siteId, VisitorId visitorId, OperatorId restrictedBy, VisitorRestrictionKind kind, DateTimeOffset? expiresAt,
        ConversationId sourceConversationId, Guid recordId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _rows.Add(new Row
        {
            Id = recordId,
            SiteId = siteId,
            VisitorId = visitorId,
            Kind = kind,
            RestrictedAt = now,
            RestrictedBy = restrictedBy,
            ExpiresAt = expiresAt,
            SourceConversationId = sourceConversationId,
        });
        return Task.CompletedTask;
    }

    public Task<bool> LiftAsync(
        SiteId siteId, VisitorId visitorId, OperatorId liftedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = _rows.Where(r => r.SiteId == siteId && r.VisitorId == visitorId && IsActive(r, now)).ToList();
        foreach (var row in active)
        {
            row.LiftedAt = now;
            row.LiftedBy = liftedBy;
        }

        return Task.FromResult(active.Count > 0);
    }

    public Task<bool> IsActiveAsync(SiteId siteId, VisitorId visitorId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Any(r => r.SiteId == siteId && r.VisitorId == visitorId && IsActive(r, now)));

    public Task<VisitorRestrictionKind?> GetActiveKindAsync(
        SiteId siteId, VisitorId visitorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = _rows.Where(r => r.SiteId == siteId && r.VisitorId == visitorId && IsActive(r, now)).ToList();
        if (active.Count == 0)
        {
            return Task.FromResult<VisitorRestrictionKind?>(null);
        }

        var kind = active.Any(r => r.Kind == VisitorRestrictionKind.Block)
            ? VisitorRestrictionKind.Block
            : active.OrderByDescending(r => r.RestrictedAt).First().Kind;
        return Task.FromResult<VisitorRestrictionKind?>(kind);
    }

    public Task<VisitorRestrictionPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        var items = _rows
            .Where(r => r.SiteId == siteId)
            .OrderByDescending(r => r.Id)
            .Where(r => beforeId is null || IsBefore(r.Id, beforeId.Value))
            .Take(limit)
            .Select(r => new VisitorRestrictionItem(
                r.Id, r.VisitorId, r.Kind, r.RestrictedAt, r.RestrictedBy, r.ExpiresAt, r.SourceConversationId, r.LiftedAt, r.LiftedBy))
            .ToList();

        return Task.FromResult(new VisitorRestrictionPage(items, null));
    }

    private static bool IsActive(Row row, DateTimeOffset now) =>
        row.LiftedAt is null && (row.ExpiresAt is null || row.ExpiresAt > now);

    // Guid has no natural ordering the way the real bigint-shaped UUIDv7 keyset relies on - this fake
    // only needs "some stable order", never real cursor semantics, since no test exercises pagination
    // across a fake boundary.
    private static bool IsBefore(Guid id, Guid cursor) => id.CompareTo(cursor) < 0;
}
