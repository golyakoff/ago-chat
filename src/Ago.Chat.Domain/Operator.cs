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
/// <para><b>`13-03`: <see cref="RemovedAt"/></b> - the operator-removal mechanism `13-01` named but did
/// not build (its own Out of scope), folded into this item because nothing else in the roadmap owns it.
/// A plain flag on this aggregate rather than a separate "removal" aggregate: it has no lifecycle of its
/// own beyond "set once, never unset" (<see cref="Remove"/>'s own remarks), so the identical "one
/// column, no object to bundle it into yet" judgement `Site.Tier`/`Site.SeatLimit`'s own remarks already
/// make for the analogous case applies here too.</para>
///
/// <para><b>`25-170`: "holds a seat" is no longer a fact this aggregate carries at all.</b> Before this
/// item, a single <c>HoldsSeat</c> flag lived here and <see cref="CanSignIn"/> read it directly - which
/// worked only by accident for the seeded Admin role, whose own holder was exempted from it by a second,
/// separate rule (<c>holdsManageOperatorsPermission</c>, below). This item's own root-cause finding is
/// that "holds a seat" is not a fact about an operator <em>account</em> at all - it is a fact about one
/// <c>(operator, role)</c> pairing, since a founder holds two roles from registration and must be able to
/// lose one role's seat independently of the other's. That fact now lives on <c>operator_roles.HoldsSeat</c>
/// (<c>Ago.Chat.Infrastructure.Postgres.Persistence.OperatorRoleRecord</c>), a join-table column no
/// aggregate wraps - the identical "no invariant here for an aggregate to enforce" reasoning the
/// `active_chats` shadow property already established for a different column on a different table. See
/// <see cref="CanSignIn"/>'s own remarks for what this aggregate does with that fact once a caller has
/// resolved it.</para>
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

    /// <summary>`23-02`: a copy of the token's own `name` claim, captured at invite redemption
    /// (`OperatorInviteRedemptionRepository`) or bootstrap registration (`RegisterSiteHandler`) and
    /// **rewritten at every sign-in** (`decisions.md` §1) - never queried live from Keycloak
    /// (`personal-data.md`'s own "not a small change" warning is why this exists as a column at all,
    /// not a join). Optional at construction for the identical reason <see cref="ExternalSubjectId"/>
    /// is: `MintDemoTenantHandler`'s minted identity carries no claims to copy, so its own operator
    /// stays permanently unnamed rather than inventing one. The refresh itself never goes through this
    /// aggregate - see `IOperatorRepository.RefreshIdentityAsync`'s own remarks for why it is raw SQL,
    /// the same "no invariant to enforce, so no reason to load the aggregate" reasoning
    /// `OperatorCapacityStore`'s `active_chats` compare-and-set already established for a different
    /// column on this same table - so there is no domain method here that ever changes this value.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>`23-02`: the identical shape and the identical source as <see cref="DisplayName"/> -
    /// the token's own `email` claim, copied and refreshed the same way, for the same reason.</summary>
    public string? Email { get; }

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
        string? displayName = null,
        string? email = null,
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
        DisplayName = displayName;
        Email = email;
        RemovedAt = removedAt;
    }

    // EF Core materialization only (1-04) - never called by domain code.
    private Operator()
    {
    }

    /// <summary>The operator explicitly wants to be <see cref="OperatorStatus.Online"/> right now,
    /// regardless of what they were doing before - the "I'm back" action
    /// (`OperatorHub.SetAwayAsync(false)`, `23-20`), and also the pre-`23-20` connect path's own
    /// meaning for an operator who was never away. Unconditional on purpose: this is the one caller
    /// allowed to overwrite a deliberate <see cref="OperatorStatus.Away"/>, because it is the operator
    /// saying so, not a side effect of a connection existing - see <see cref="NoteConnected"/> for the
    /// passive counterpart that must not. Idempotent: calling this while already
    /// <see cref="OperatorStatus.Online"/> is a harmless no-op, not an error.</summary>
    public void GoOnline() => Status = OperatorStatus.Online;

    /// <summary>`23-20`: the passive counterpart of <see cref="GoOnline"/> - called by
    /// `OperatorHub.OnConnectedAsync` on every connection (a first connect and every reconnect alike)
    /// instead of calling <see cref="GoOnline"/> directly, which is what that call site did before this
    /// item and is exactly the defect it names: an operator who deliberately went
    /// <see cref="OperatorStatus.Away"/> and then had their connection blip - an ordinary SignalR
    /// automatic reconnect is indistinguishable, at this hub, from any other disconnect-then-reconnect
    /// - would be silently carried back to <see cref="OperatorStatus.Online"/> by the mere fact of
    /// reconnecting, with no act of theirs behind it. A reconnect has no way to know whether the
    /// operator meant to step away five minutes ago, so it must not use the caller that means "this
    /// operator, specifically, wants to be online now" - only <see cref="GoOnline"/> (via
    /// `SetAwayAsync(false)`) carries that meaning once this method takes the connect path over. A
    /// genuinely <see cref="OperatorStatus.Offline"/> operator (nothing has ever told this aggregate
    /// otherwise) is still moved to <see cref="OperatorStatus.Online"/> here - the entire behaviour this
    /// call site had before this item, preserved for every operator who never went away - and an
    /// already-<see cref="OperatorStatus.Online"/> operator is left alone, same as before.</summary>
    public void NoteConnected()
    {
        if (Status == OperatorStatus.Offline)
        {
            Status = OperatorStatus.Online;
        }
    }

    /// <summary>The realtime connection registry has zero live connections for this operator - called
    /// from `OperatorHub.OnDisconnectedAsync`, only when <c>HubConnectionRegistration.OnDisconnectedAsync</c>
    /// reports this was the last one. Deliberately immediate, not deferred to `4-04`'s grace-period
    /// consumer: that grace period exists to avoid prematurely releasing an *assigned conversation* on
    /// a brief network blip, a different and costlier decision than this one. Excluding a
    /// momentarily-disconnected operator from new assignments for the few hundred milliseconds a
    /// reconnect actually takes has no comparable cost, and leaving them assignable while genuinely
    /// gone would route a new visitor to a connection that cannot receive it.
    ///
    /// <para>`23-20`: leaves a deliberate <see cref="OperatorStatus.Away"/> alone rather than
    /// overwriting it with <see cref="OperatorStatus.Offline"/>. Both states are already excluded from
    /// assignment identically - the engine's own filter is `Status == Online`, not "not Offline" - so
    /// this changes nothing about who is assignable. What it protects is the *next* reconnect: without
    /// this guard, an away operator's last connection dropping would erase the fact they went away, and
    /// <see cref="NoteConnected"/> would then find <see cref="OperatorStatus.Offline"/> on the
    /// reconnect and carry them back to <see cref="OperatorStatus.Online"/> - the identical defect this
    /// item exists to close, just reached through the disconnect path instead of the connect one. The
    /// item's own rule - a deliberate Away "is cleared only by the operator" - means only by the
    /// operator, not by any connection lifecycle event in either direction.</para></summary>
    public void GoOffline()
    {
        if (Status != OperatorStatus.Away)
        {
            Status = OperatorStatus.Offline;
        }
    }

    /// <summary>`23-20`: a deliberate act distinct from <see cref="GoOffline"/> - the operator is still
    /// connected and still holds every conversation currently `Assigned` to them (this item's own
    /// Scope: "going away is not going offline and is not a release" - `OperatorConversationReleaser`
    /// and `23-03`'s assignment intervals are untouched by this method and by this item). They are
    /// simply not the person expected to answer a *new* one. The assignment engine's own
    /// `Status == Online` filter (`SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer`) and
    /// `OperatorRepository.AnyOnlineForSiteAsync` already treat anything other than `Online` as "not
    /// online" - so nothing downstream needs to learn a new state to stop routing to an away operator or
    /// to stop counting them as coverage for `14-04`'s offline auto-reply; this method only has to make
    /// the state reachable. Reversed only by <see cref="GoOnline"/>, called explicitly by the operator -
    /// never automatically, see <see cref="NoteConnected"/> and <see cref="GoOffline"/>'s own remarks
    /// for why a mere connect or disconnect must not do this instead.</summary>
    public void GoAway() => Status = OperatorStatus.Away;

    /// <summary>
    /// `23-71`: `decisions/0006`'s "only the owner and as many operators as are paid for can sign in" -
    /// originally expressed here as `HoldsSeat || holdsManageOperatorsPermission`, an Admin-permission
    /// exemption that let a seatless administrator sign in to administer their own account even though
    /// their own seat said no.
    ///
    /// <para><b>`25-170`: the exemption is gone; this is now the one rule it always should have been.</b>
    /// The exemption was a symptom, not the fix - it existed only because "holds a seat" used to be one
    /// flag per <em>account</em>, so an Administrator (who by design consumes no Operator-role seat) had
    /// to be carved out by permission or could never sign in at all. Now that a seat is a fact about one
    /// <c>(operator, role)</c> pairing, an account simply can sign in whenever <em>any</em> role it
    /// currently holds has its own seat - an Administrator's own Admin-role row defaults to holding one
    /// (`operator_roles`' own remarks) the same way an Operator-role row always has, with no permission
    /// carve-out required to reach the identical outcome. An account disabled on every role it holds -
    /// Admin-role included - is disabled, full stop, the same as one disabled on its Operator-role seat;
    /// there is no longer a second door.</para>
    ///
    /// <para><paramref name="anyRoleHoldsSeat"/> is supplied by the caller, never resolved here:
    /// <c>operator_roles</c> is an infrastructure-backed table (<c>IOperatorRoleRepository</c>,
    /// `adr/0016`'s own dependency rule), and Domain must not depend on it - the alternative, giving
    /// <see cref="Operator"/> a reference to that repository so it could resolve this itself, would make
    /// the aggregate untestable without a database and would put an infrastructure concern inside the
    /// one layer that must never see it. This method is deliberately just the rule; the lookup that
    /// produces its input (<c>IOperatorRoleRepository.HoldsAnySeatAsync</c>) is the caller's own job -
    /// <c>Application.UseCases.ResolveOperatorIdentity.OperatorSignInEligibility</c>, the one place both
    /// callers that decide sign-in eligibility (<c>ResolveOperatorIdentityHandler</c>,
    /// <c>ListMyTenanciesHandler</c>) already share it from.</para>
    ///
    /// <para><b>Never widens routing.</b> This answers "may sign in", nothing else - the assignment
    /// engine and every other routing/on-duty query (`SkipLockedAssignmentClaimer`,
    /// `RedisLockAssignmentClaimer`, `IOperatorRepository.AnyOnlineForSiteAsync`,
    /// `AssignConversationHandler`'s own self-claim guard) still gate on holding the seeded
    /// <c>"Operator"</c> role's own seat specifically (<c>IOperatorRoleRepository.HoldsRoleSeatAsync</c>)
    /// - "may administer this account" and "may be routed a conversation" are two different questions,
    /// and this method only ever answers the first.</para>
    /// </summary>
    public bool CanSignIn(bool anyRoleHoldsSeat) => anyRoleHoldsSeat;

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
