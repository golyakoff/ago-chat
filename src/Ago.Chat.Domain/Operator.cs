namespace Ago.Chat.Domain;

/// <summary>
/// An authenticated agent of one site. Presence (<see cref="Status"/>) and capacity enforcement are
/// not wired to anything yet - Redis presence is Stage 3, the capacity-checked assignment engine is
/// Stage 4 (roadmap.md). This entity carries the fields now so `1-04`'s schema has a real shape to
/// map, without building the behaviour before anything calls it.
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }

    public SiteId SiteId { get; }

    public OperatorStatus Status { get; }

    public int Capacity { get; }

    /// <summary>`5-05`: the Keycloak-issued `sub` claim identifying this operator to the IdP - how
    /// `Ago.Chat.Api`'s `IClaimsTransformation` resolves a validated OIDC token back to this row
    /// (`adr/0022`). Optional, not required at construction, so every existing caller that builds an
    /// `Operator` without an external identity (every test in this codebase today) keeps compiling -
    /// nothing before `5-05` had one to provide.</summary>
    public string? ExternalSubjectId { get; }

    public Operator(OperatorId id, SiteId siteId, OperatorStatus status, int capacity, string? externalSubjectId = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "Operator capacity must be positive.");
        }

        Id = id;
        SiteId = siteId;
        Status = status;
        Capacity = capacity;
        ExternalSubjectId = externalSubjectId;
    }

    // EF Core materialization only (1-04) - never called by domain code.
    private Operator()
    {
    }
}
