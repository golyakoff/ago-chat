namespace Ago.Chat.Domain;

/// <summary>
/// An authenticated agent of one site. Capacity enforcement is `4-01`'s `IOperatorCapacity`, a raw-SQL
/// port deliberately outside this aggregate (see the `active_chats` shadow property in
/// `OperatorConfiguration`). Presence (<see cref="Status"/>) is this aggregate's own: <see cref="GoOnline"/>/
/// <see cref="GoOffline"/> exist because nothing did before this fix - the assignment engine
/// (`SkipLockedAssignmentClaimer`, `RedisLockAssignmentClaimer`) has always filtered candidates on
/// `Status == Online`, but every runtime-created operator (`MintDemoTenantHandler`, `RegisterSiteHandler`)
/// was born `Offline` and had no way to become anything else - only the demo seed script's raw SQL ever
/// wrote `Online`, which is why assignment only ever looked like it worked.
/// </summary>
public sealed class Operator
{
    public OperatorId Id { get; }

    public SiteId SiteId { get; }

    public OperatorStatus Status { get; private set; }

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

    /// <summary>The realtime connection registry has a live SignalR connection for this operator -
    /// called from `OperatorHub.OnConnectedAsync`. Idempotent: a second connection (another tab)
    /// arriving while already <see cref="OperatorStatus.Online"/> is a harmless no-op, not an error -
    /// nothing here counts connections, only whether at least one exists.</summary>
    public void GoOnline() => Status = OperatorStatus.Online;

    /// <summary>The realtime connection registry has zero live connections for this operator - called
    /// from `OperatorHub.OnDisconnectedAsync`, only when <c>HubConnectionRegistration.OnDisconnectedAsync</c>
    /// reports this was the last one. Deliberately immediate, not deferred to `4-04`'s grace-period
    /// consumer: that grace period exists to avoid prematurely releasing an *assigned conversation* on
    /// a brief network blip, a different and costlier decision than this one. Excluding a
    /// momentarily-disconnected operator from new assignments for the few hundred milliseconds a
    /// reconnect actually takes has no comparable cost, and leaving them assignable while genuinely
    /// gone would route a new visitor to a connection that cannot receive it.</summary>
    public void GoOffline() => Status = OperatorStatus.Offline;
}
