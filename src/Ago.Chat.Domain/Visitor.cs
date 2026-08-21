namespace Ago.Chat.Domain;

/// <summary>
/// Anonymous, identified only by a signed token the host issues and validates (realtime.md) - this
/// entity carries no token material itself, only the identity and timestamps that are this system's
/// business, not the host's.
/// </summary>
public sealed class Visitor
{
    public VisitorId Id { get; }

    public SiteId SiteId { get; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public Visitor(VisitorId id, SiteId siteId, DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    /// <summary>Records a return visit - the reason history survives a reload (vision.md).</summary>
    public void Touch(DateTimeOffset now)
    {
        LastSeenAt = now;
    }

    // EF Core materialization only (1-04) - never called by domain code.
    private Visitor()
    {
    }
}
