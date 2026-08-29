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
///
/// <para><b>`13-03`: <see cref="HoldsSeat"/> and <see cref="RemovedAt"/></b> - the seat-assignment and
/// operator-removal mechanism `13-01` named but did not build (its own Out of scope), folded into this
/// item because nothing else in the roadmap owns it. Both are plain flags on this aggregate rather than
/// a separate "seat assignment" aggregate: neither has any lifecycle of its own beyond "on or off" /
/// "set once, never unset" (<see cref="Remove"/>'s own remarks), so the identical "one column, no object
/// to bundle it into yet" judgement `Site.Tier`/`Site.SeatLimit`'s own remarks already make for the
/// analogous case applies here too.</para>
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

    /// <summary>`13-03`: does this operator currently occupy one of the site's paid seats. Defaults to
    /// <see langword="true"/> at construction - every operator created today (self-registration,
    /// invite redemption) is created within `13-01`'s own seat-limit check and therefore already fits,
    /// so "holds a seat" is the correct starting state for every row this codebase has ever written,
    /// not a special case.
    ///
    /// <para>Toggled by <see cref="ToggleSeat"/>, a site's `Permission.SiteManageOperators` holder's own
    /// call, up to the site's current `SeatLimit` (the caller's own capacity check, not this aggregate's
    /// - the same "aggregate applies, caller enforces the cross-aggregate rule" split
    /// `OperatorInviteRedemptionRepository`'s own seat check already draws against `Operator` rows).
    /// `false` resolves to no `OperatorId` claim at all (`ResolveOperatorIdentityHandler`'s own query -
    /// the exact same shape as no `operators` row existing, so `RequireOperatorIdentity` already refuses
    /// it with no new policy code, `decisions/0006`'s own "only the owner and as many operators as are
    /// paid for can sign in").</para></summary>
    public bool HoldsSeat { get; private set; } = true;

    /// <summary>`13-03`: when this operator was removed from their site, or <see langword="null"/> for
    /// one still active - "this person is gone", set once by <see cref="Remove"/> and never cleared
    /// (there is no "un-remove" in this item's own Scope). A removed operator resolves to no
    /// `OperatorId` claim (the identical mechanism <see cref="HoldsSeat"/> uses, and permanently
    /// excluded from `13-01`'s own seat-count check - `OperatorInviteRedemptionRepository`'s own
    /// `COUNT(*)` needs `AND removed_at IS NULL`, this item's own named regression fix) - but nothing
    /// about the operator's own history, past messages, or account data is touched
    /// (`decisions/0006`'s "all its data stay intact"), matching `16-02`'s own narrower, data-preserving
    /// removal shape rather than its erasure one.</summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public Operator(
        OperatorId id,
        SiteId siteId,
        OperatorStatus status,
        int capacity,
        string? externalSubjectId = null,
        bool holdsSeat = true,
        DateTimeOffset? removedAt = null)
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
        HoldsSeat = holdsSeat;
        RemovedAt = removedAt;
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

    /// <summary>`13-03`: a site's `Permission.SiteManageOperators` holder assigns or releases this
    /// operator's own seat. No guard against toggling a removed operator back on - <see cref="Remove"/>
    /// is the permanent one; this method has nothing to say about it, and a caller that loaded a removed
    /// operator to toggle its seat has already made a mistake this aggregate cannot see.</summary>
    public void ToggleSeat(bool holdsSeat) => HoldsSeat = holdsSeat;

    /// <summary>`13-03`: "this person is gone" - a site's `Permission.SiteManageOperators` holder's own
    /// call. Raises <see cref="OperatorRemoved"/> so `Ago.Chat.Worker` can release this operator's
    /// `Assigned` conversations back to `Waiting` (<c>OperatorConversationReleaser</c>'s own existing
    /// logic) in a consumer, out of this request's own transaction - the same "state change commits,
    /// the wider consequence is a separate, retried step" shape the outbox exists for (CLAUDE.md rule
    /// 4), rather than reaching across host boundaries to call the releaser directly from
    /// <c>Ago.Chat.Api</c>.</summary>
    public void Remove(DateTimeOffset now)
    {
        if (RemovedAt is not null)
        {
            throw new InvalidOperationException($"Operator {Id.Value} has already been removed.");
        }

        RemovedAt = now;
        _domainEvents.Add(new OperatorRemoved(Id, SiteId, now));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
