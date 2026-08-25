using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RegisterSite;

/// <summary>
/// `10-02`: the bootstrap step `10-01`'s `RequireKeycloakIdentity` policy exists to authenticate -
/// given a validated Keycloak principal with no `operators` row yet, creates one `Site`, seeds both
/// of this codebase's built-in roles for it, creates one `Operator`, and assigns both roles, all in
/// one transaction (<see cref="ISiteRegistrationRepository.TryRegisterAsync"/>).
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
    IOperatorRepository operators,
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
    private static readonly string[] OperatorRolePermissions =
        [Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value];

    private static readonly string[] AdminRolePermissions =
        [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value];

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

        // Fast-path pre-check for the common case (a caller who already registered, retrying or
        // re-clicking) - ISiteRegistrationRepository.TryRegisterAsync's own unique-index check is
        // what actually closes the race between two concurrent registrations from the same identity;
        // this is purely so that ordinary repeat case does not pay for a Site/two Roles/Operator
        // worth of id generation and a doomed insert first.
        var existingOperator = await operators.GetByExternalSubjectIdAsync(command.ExternalSubjectId, cancellationToken);
        if (existingOperator is not null)
        {
            return ConversationErrors.SiteAlreadyRegistered();
        }

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
        // Offline, not Online - this operator has not connected yet (presence, Stage 3, is what
        // actually flips this once their console session opens); unlike `1-05`'s seed script, which
        // sets its demo operators Online purely for manual-verification convenience, a real
        // registration should not lie about a connection that has not happened. Capacity 5 matches
        // the same seed script's own value - a starting default, not a measured one (`CLAUDE.md`).
        var operatorEntity = new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: command.ExternalSubjectId);

        var operatorRole = new RoleSeed(idGenerator.NewId(now), "Operator", OperatorRolePermissions);
        var adminRole = new RoleSeed(idGenerator.NewId(now), "Admin", AdminRolePermissions);

        var registered = await registrations.TryRegisterAsync(
            new SiteRegistration(site, operatorEntity, operatorRole, adminRole), cancellationToken);
        if (!registered)
        {
            // Lost the race against a concurrent registration from the same identity -
            // ISiteRegistrationRepository's own remarks on why this is the real correctness check,
            // not the pre-check above.
            return ConversationErrors.SiteAlreadyRegistered();
        }

        return new RegisteredSite(siteId.Value, operatorId.Value);
    }
}
