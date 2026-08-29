using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RegisterSite;

/// <summary>
/// `10-02`: the bootstrap step `10-01`'s `RequireKeycloakIdentity` policy exists to authenticate -
/// given a validated Keycloak principal, creates one `Site`, seeds both of this codebase's built-in
/// roles for it, creates one `Operator`, and assigns both roles, all in one transaction
/// (<see cref="ISiteRegistrationRepository.TryRegisterAsync"/>).
///
/// <para><b>`13-07`/`adr/0068`: no longer "with no `operators` row yet".</b> Before this item, a
/// `sub` that already resolved to any `operators` row was refused `409` by a pre-check this handler
/// used to run. That pre-check is gone - a `sub` administering one `Site` already may call this again
/// and provision another, exactly like a first-time caller. See the `TryRegisterAsync` call below for
/// what that leaves of this handler's own correctness guarantee.</para>
///
/// <b>Why one wider transaction, not `data-model.md`'s usual "one aggregate per transaction":</b> this
/// is a genuine multi-row *provisioning* step, not an ordinary write to one aggregate - `Site`, two
/// `Role`s, `Operator`, and two `operator_roles` rows only mean anything together. `1-05`'s seed
/// script already produces the identical shape, just non-transactionally via idempotent
/// `ON CONFLICT DO NOTHING` SQL, because a re-run of a seed script failing halfway is harmless (the
/// next run finishes the job). A real caller hitting this endpoint gets exactly one attempt - a
/// partial failure here must not leave a site with no roles, or a Keycloak identity that resolves to
/// an `Operator` row holding no permissions at all (a token that then passes
/// `RequireOperatorIdentity` but can do nothing, a strictly worse failure mode than the request
/// simply failing outright and the caller retrying the whole call).
/// </summary>
public sealed class RegisterSiteHandler(
    ISiteRegistrationRepository registrations,
    IRateLimiter rateLimiter,
    RegisterSiteRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    // `5-08`/`adr/0016`'s own fixed permission sets - restated here (not referenced through a shared
    // constant) because `1-05`'s seed script already restates them too as raw SQL array literals; a
    // third place doing the same restatement would still need to independently match those two, so
    // this handler is the natural third since it is the first thing that ever *writes* these lists
    // instead of comparing against them.
    // `18-04`: ConversationNoteWrite/ConversationTag join the Operator set here - found missing
    // 2026-08-29 while landing that item (both permissions existed and both new handlers already
    // checked them, but no real site's Operator role ever actually held either, so every real operator
    // was permanently Forbidden from the feature - the same "the type exists and something depends on
    // it, but nothing ever wires the caller up to hold it" shape as the same day's billing DI gap,
    // this time in role seeding rather than a service container). Operator-level, not Admin-only: a
    // note/tag is ordinary day-to-day conversation handling, the same category as
    // ConversationRead/Send/Assign already here, not a site-configuration action.
    private static readonly string[] OperatorRolePermissions =
        [
            Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value,
            Permission.ConversationNoteWrite.Value, Permission.ConversationTag.Value,
        ];

    // `16-02`: SiteErase/ConversationErase join the Admin set here too - see Permission's own remarks
    // on why both are Admin-only, and this class's own remarks above on why this array is restated in
    // three independent places rather than shared.
    // `16-03`: SiteExport joins them - same reasoning, same restatement.
    private static readonly string[] AdminRolePermissions =
        [
            Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value,
            Permission.SiteErase.Value, Permission.ConversationErase.Value, Permission.SiteExport.Value,
        ];

    public async Task<Result<RegisteredSite>> HandleAsync(RegisterSite command, CancellationToken cancellationToken)
    {
        // Rate limit first, before any database work - the same "a bad caller still costs them a
        // token, never costs us a query" ordering `AuthEndpoints.HandleVisitorSessionAsync` and
        // `CreateAttachmentHandler` already use. Per-subject checked before per-IP: a caller who was
        // never going to pass their own bucket should not also spend a share of the (coarser, shared)
        // IP budget finding that out.
        var subjectLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"register-site:sub:{command.ExternalSubjectId}"),
            new RateLimitRule(rateLimitOptions.PerSubjectCapacity, rateLimitOptions.PerSubjectRefillPerSecond),
            cancellationToken);
        if (!subjectLimit.Allowed)
        {
            return ConversationErrors.SiteRegistrationRateLimited(subjectLimit.RetryAfter);
        }

        var ipLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"register-site:ip:{command.RequestIp}"),
            new RateLimitRule(rateLimitOptions.PerIpCapacity, rateLimitOptions.PerIpRefillPerSecond),
            cancellationToken);
        if (!ipLimit.Allowed)
        {
            return ConversationErrors.SiteRegistrationRateLimited(ipLimit.RetryAfter);
        }

        // `13-07`/`adr/0068`: the "identity already has an operator row anywhere -> 409" fast-path
        // pre-check that used to live here is gone. `operators.external_subject_id`'s index is now
        // composite `(external_subject_id, site_id)` (`OperatorConfiguration`), so a `sub` that
        // already administers one `Site` is exactly as free to register another as a first-time
        // caller - that is this whole item's point, not an oversight of a check this handler used to
        // make. See this handler's own remarks below, at the `TryRegisterAsync` call, for what the
        // real (database-level) guard now means once the pre-check is gone.
        if (string.IsNullOrWhiteSpace(command.SiteName))
        {
            return ConversationErrors.SiteInvalidName("Site display name cannot be empty.");
        }

        var originError = OriginValidator.Validate(command.InitialAllowedOrigin);
        if (originError is not null)
        {
            return ConversationErrors.SiteInvalidOrigin(originError);
        }

        var now = clock.UtcNow;

        // `10-02`'s own Scope: "Site.PublicKey generated via the existing IIdGenerator port - never
        // Guid.NewGuid() directly" - a dedicated id, not a reuse of the new Site's own id, so a public
        // key never doubles as a guessable pointer to the row's primary key.
        var publicKey = $"site_{idGenerator.NewId(now):N}";
        var siteId = new SiteId(idGenerator.NewId(now));
        // `12-02`: `createdAt` from the same `IClock` reading everything else in this handler uses -
        // the first and only writer of `sites.created_at`, which `12-02`'s owner overview reads.
        var site = new Site(siteId, publicKey, [command.InitialAllowedOrigin], command.SiteName, now);

        var operatorId = new OperatorId(idGenerator.NewId(now));
        // Offline, not Online - this operator has not connected yet. `4-06` is what actually flips
        // this once their console session opens (OperatorHub.OnConnectedAsync); unlike `1-05`'s seed
        // script, which sets its demo operators Online purely for manual-verification convenience, a
        // real registration should not lie about a connection that has not happened. Capacity 5
        // matches the same seed script's own value - a starting default, not a measured one
        // (`CLAUDE.md`).
        var operatorEntity = new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: command.ExternalSubjectId);

        var operatorRole = new RoleSeed(idGenerator.NewId(now), "Operator", OperatorRolePermissions);
        var adminRole = new RoleSeed(idGenerator.NewId(now), "Admin", AdminRolePermissions);

        // `13-07`/`adr/0068`: before this item, `false` here meant "two concurrent registrations from
        // the same identity raced past the pre-check above and collided on the old, globally-unique
        // `external_subject_id` index" - a real, reachable race, which is why the pre-check existed
        // only as a fast path and this check was the actual guarantee (`ISiteRegistrationRepository`'s
        // own remarks). With the index now composite `(external_subject_id, site_id)` and `siteId`
        // freshly generated by `IIdGenerator` on *every* call (UUIDv7 - a real timestamp plus ~74
        // random bits, `Ago.Chat.Infrastructure.Postgres.UuidV7Generator`), two concurrent
        // registrations from the same identity essentially cannot collide on the new composite key at
        // all: each call mints its own independent random `siteId`, so the only way `TryRegisterAsync`
        // can still return `false` is `IIdGenerator` itself producing the same id twice, which is the
        // generator's own collision probability, not a race this handler creates. That makes this
        // path unreachable in ordinary operation - kept anyway, defensively, rather than removed: it
        // is a single `if`, it costs nothing on the success path, and "the id generator never
        // collides" is an assumption worth not silently relying on in the one handler that would
        // otherwise insert five rows on top of a violated unique index.
        var registered = await registrations.TryRegisterAsync(
            new SiteRegistration(site, operatorEntity, operatorRole, adminRole), cancellationToken);
        if (!registered)
        {
            return ConversationErrors.SiteAlreadyRegistered();
        }

        return new RegisteredSite(siteId.Value, operatorId.Value);
    }
}
